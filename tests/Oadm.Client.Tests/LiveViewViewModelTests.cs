using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Media;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

public sealed class LiveViewViewModelTests
{
    private readonly IOadmApi _api = Substitute.For<IOadmApi>();
    private readonly FakeDecoderFactory _decoders = new();
    private readonly List<FakeStream> _streams = [];
    private readonly DeviceRowViewModel _camA = new(TestSupport.Device("a", "B8A44F000001", "10.0.0.48", "P3265-V"));
    private readonly DeviceRowViewModel _camB = new(TestSupport.Device("b", "B8A44F000002", "10.0.0.49", "M3106-L Mk II"));

    public LiveViewViewModelTests()
    {
        _api.WatchLiveViewAsync(Arg.Any<LiveViewRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var stream = new FakeStream(call.Arg<LiveViewRequest>());
            lock (_streams)
            {
                _streams.Add(stream);
            }

            return stream.ReadAsync(call.Arg<CancellationToken>());
        });
        _api.ListLiveViewSourcesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<string>() == "a"
                ? [Source(1, "View Area 1"), Source(2, "View Area 2")]
                : (IReadOnlyList<LiveViewSource>)[Source(1, "Camera")]);
    }

    private static LiveViewSource Source(int camera, string name) => new() { Camera = camera, Name = name, MaxWidth = 1920, MaxHeight = 1080 };

    private LiveViewViewModel CreateVm() => new(_api, _decoders, new ImmediateUiDispatcher(), NullLogger<LiveViewViewModel>.Instance)
    {
        Backoff = [TimeSpan.FromMilliseconds(20)],
    };

    [Fact]
    public async Task Open_requests_the_device_with_the_decodable_codecs_and_shows_frames()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);

        Assert.True(vm.IsOpen);
        Assert.Equal(LiveViewState.Connecting, vm.State);
        Assert.Equal("P3265-V", vm.Title);
        var stream = await StreamAsync(0);
        Assert.Equal("a", stream.Request.DeviceId);
        Assert.Equal([VideoCodec.H265, VideoCodec.H264], stream.Request.AcceptedCodecs);
        Assert.Equal((640, 360, 10), (stream.Request.MaxWidth, stream.Request.MaxHeight, stream.Request.Fps));

        stream.Send(VideoCodec.H265);
        await TestSupport.WaitUntilAsync(() => vm.Image is not null);
        Assert.Equal(LiveViewState.Live, vm.State);
        Assert.Equal("Live", vm.StateText);
        Assert.StartsWith("H.265  ·  640x360", vm.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_with_several_sources_shows_the_switch_with_the_first_selected()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        await TestSupport.WaitUntilAsync(() => vm.HasSourceChoice);

        Assert.Equal(["1", "2"], vm.Sources.Select(s => s.Label));
        Assert.Equal("View Area 2", vm.Sources[1].Name);
        Assert.True(vm.Sources[0].IsSelected);
        Assert.False(vm.Sources[1].IsSelected);
        Assert.EndsWith("View Area 1", vm.Subtitle, StringComparison.Ordinal);
        Assert.Equal(0, (await StreamAsync(0)).Request.Camera); // server default
    }

    [Fact]
    public async Task Single_source_device_has_no_switch()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camB);
        await TestSupport.WaitUntilAsync(() => vm.Sources.Count == 1);
        Assert.False(vm.HasSourceChoice);
        Assert.DoesNotContain("Camera", vm.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_the_source_restarts_the_stream_on_that_camera()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        await TestSupport.WaitUntilAsync(() => vm.HasSourceChoice);
        var first = await StreamAsync(0);
        first.Send(VideoCodec.H264, camera: 1);
        await TestSupport.WaitUntilAsync(() => vm.Image is not null);

        vm.SelectSourceCommand.Execute(vm.Sources[1]);

        var second = await StreamAsync(1);
        Assert.Equal(2, second.Request.Camera);
        Assert.Equal("a", second.Request.DeviceId);
        await TestSupport.WaitUntilAsync(() => first.Cancelled);
        Assert.Null(vm.Image);
        Assert.True(vm.Sources[1].IsSelected);
        Assert.False(vm.Sources[0].IsSelected);
        second.Send(VideoCodec.H264, camera: 2);
        await TestSupport.WaitUntilAsync(() => vm.State == LiveViewState.Live);
        Assert.Equal(2, vm.Camera);
        Assert.EndsWith("View Area 2", vm.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_the_current_source_does_not_restart()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        await TestSupport.WaitUntilAsync(() => vm.HasSourceChoice);
        var stream = await StreamAsync(0);
        stream.Send(VideoCodec.H264, camera: 1);
        await TestSupport.WaitUntilAsync(() => vm.Camera == 1);
        vm.SelectSourceCommand.Execute(vm.Sources[0]);
        await Task.Delay(50);
        Assert.Single(_streams);
    }

    [Fact]
    public async Task The_chosen_source_is_remembered_per_device_for_the_session()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        await TestSupport.WaitUntilAsync(() => vm.HasSourceChoice);
        vm.SelectSourceCommand.Execute(vm.Sources[1]);
        await StreamAsync(1);

        vm.ToggleCommand.Execute(_camB);     // another device: its own default
        Assert.Equal(0, (await StreamAsync(2)).Request.Camera);

        vm.ToggleCommand.Execute(_camA);     // back: view area 2 again
        Assert.Equal(2, (await StreamAsync(3)).Request.Camera);
        await TestSupport.WaitUntilAsync(() => vm.HasSourceChoice);
        Assert.True(vm.Sources[1].IsSelected);
    }

    [Fact]
    public async Task New_frame_replaces_and_disposes_the_previous_picture()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        var stream = await StreamAsync(0);
        stream.Send(VideoCodec.H264);
        await TestSupport.WaitUntilAsync(() => vm.Image is not null);
        var first = (FakeImage)vm.Image!;
        stream.Send(VideoCodec.H264);
        await TestSupport.WaitUntilAsync(() => !ReferenceEquals(vm.Image, first));
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task Same_icon_again_closes_and_stops_the_stream()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        var stream = await StreamAsync(0);
        stream.Send(VideoCodec.H264);
        await TestSupport.WaitUntilAsync(() => vm.Image is not null);
        var image = (FakeImage)vm.Image!;

        vm.ToggleCommand.Execute(_camA);

        Assert.False(vm.IsOpen);
        Assert.Null(vm.Image);
        Assert.Null(vm.Device);
        Assert.True(image.Disposed);
        await TestSupport.WaitUntilAsync(() => stream.Cancelled);
        await vm.Running;
        Assert.All(_decoders.Created, d => Assert.True(d.Disposed));
    }

    [Fact]
    public async Task Another_device_switches_the_stream()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        var first = await StreamAsync(0);

        vm.ToggleCommand.Execute(_camB);

        Assert.True(vm.IsOpen);
        Assert.Equal("b", vm.Device!.Id);
        var second = await StreamAsync(1);
        Assert.Equal("b", second.Request.DeviceId);
        await TestSupport.WaitUntilAsync(() => first.Cancelled);
        second.Send(VideoCodec.H264);
        await TestSupport.WaitUntilAsync(() => vm.State == LiveViewState.Live);
    }

    [Fact]
    public async Task Close_command_hides_the_panel()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        var stream = await StreamAsync(0);
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsOpen);
        Assert.Equal(LiveViewState.Closed, vm.State);
        await TestSupport.WaitUntilAsync(() => stream.Cancelled);
    }

    [Fact]
    public async Task Dropped_stream_reconnects()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        var first = await StreamAsync(0);
        first.Fail(new RpcException(new Status(StatusCode.Unavailable, "The camera closed the video stream.")));

        await TestSupport.WaitUntilAsync(() => vm.State == LiveViewState.Reconnecting || _streams.Count > 1);
        var second = await StreamAsync(1);
        second.Send(VideoCodec.H264);
        await TestSupport.WaitUntilAsync(() => vm.State == LiveViewState.Live);
        Assert.Equal("a", second.Request.DeviceId);
    }

    [Fact]
    public async Task Reconnecting_state_shows_the_reason()
    {
        var vm = new LiveViewViewModel(_api, _decoders, new ImmediateUiDispatcher(), NullLogger<LiveViewViewModel>.Instance)
        {
            Backoff = [TimeSpan.FromSeconds(30)],
        };
        using (vm)
        {
            vm.ToggleCommand.Execute(_camA);
            (await StreamAsync(0)).Fail(new RpcException(new Status(StatusCode.Unavailable, "The camera stopped sending video.")));
            await TestSupport.WaitUntilAsync(() => vm.State == LiveViewState.Reconnecting);
            Assert.Equal("Reconnecting", vm.StateText);
            Assert.Equal("The camera stopped sending video. Retrying in 30 s.", vm.DetailText);
        }
    }

    [Fact]
    public async Task Permanent_error_is_shown_without_retrying()
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        (await StreamAsync(0)).Fail(new RpcException(new Status(StatusCode.PermissionDenied, "The camera rejected the stored credentials.")));
        await TestSupport.WaitUntilAsync(() => vm.State == LiveViewState.Error);
        await vm.Running;
        Assert.Equal("The camera rejected the stored credentials.", vm.DetailText);
        Assert.Single(_streams);
    }

    [Fact]
    public void Missing_decoder_shows_a_clear_error_and_never_calls_the_server()
    {
        _decoders.Available = false;
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        Assert.True(vm.IsOpen);
        Assert.Equal(LiveViewState.Error, vm.State);
        Assert.Equal(LiveViewViewModel.DecoderUnavailableText, vm.DetailText);
        _api.DidNotReceive().WatchLiveViewAsync(Arg.Any<LiveViewRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_the_viewed_device_closes_the_panel()
    {
        var api = Substitute.For<IOadmApi>();
        api.WatchLiveViewAsync(Arg.Any<LiveViewRequest>(), Arg.Any<CancellationToken>()).Returns(new FakeStream(new LiveViewRequest()).ReadAsync(CancellationToken.None));
        api.ListLiveViewSourcesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([Source(1, "Camera")]);
        using var fixture = new DevicesFixture(api);
        fixture.SeedDevices(TestSupport.Device("a", "B8A44F000001", "10.0.0.48", "P3265-V"));
        DeviceRowViewModel row = fixture.Store.Find("a")!;
        fixture.Devices.LiveView.Open(row);
        Assert.True(fixture.Devices.LiveView.IsOpen);

        fixture.SeedDevices();
        await TestSupport.WaitUntilAsync(() => !fixture.Devices.LiveView.IsOpen);
    }

    [Theory]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.Camera, true)]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.Encoder, true)]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.Intercom, true)]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.Unknown, true)]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.Speaker, false)]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.DoorController, false)]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.IoModule, false)]
    [InlineData(Oadm.Contracts.V1.DeviceCategory.Radar, false)]
    public void Live_view_support_follows_the_device_category(Oadm.Contracts.V1.DeviceCategory category, bool expected)
    {
        var device = TestSupport.Device("x", "B8A44F000009", "10.0.0.9", "any");
        device.Category = category;
        device.HasVideo = category is Oadm.Contracts.V1.DeviceCategory.Camera or Oadm.Contracts.V1.DeviceCategory.Encoder or Oadm.Contracts.V1.DeviceCategory.Intercom;
        Assert.Equal(expected, LiveViewSupport.IsSupported(new DeviceRowViewModel(device)));
    }

    private async Task<FakeStream> StreamAsync(int index)
    {
        await TestSupport.WaitUntilAsync(() =>
        {
            lock (_streams)
            {
                return _streams.Count > index;
            }
        });
        lock (_streams)
        {
            return _streams[index];
        }
    }

    private sealed class FakeStream(LiveViewRequest request)
    {
        private readonly Channel<LiveViewFrame> _frames = Channel.CreateUnbounded<LiveViewFrame>();

        public LiveViewRequest Request { get; } = request;

        public bool Cancelled { get; private set; }

        public void Send(VideoCodec codec, int camera = 1) =>
            _frames.Writer.TryWrite(new LiveViewFrame { Codec = codec, Keyframe = true, Width = 640, Height = 360, Camera = camera, Data = Google.Protobuf.ByteString.CopyFrom(0, 0, 0, 1) });

        public void Fail(Exception error) => _frames.Writer.TryComplete(error);

        public async IAsyncEnumerable<LiveViewFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            using var registration = ct.Register(() => Cancelled = true);
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
            {
                yield return frame;
            }
        }
    }

    private sealed class FakeDecoderFactory : IVideoDecoderFactory
    {
        public bool Available { get; set; } = true;

        public List<FakeDecoder> Created { get; } = [];

        public IReadOnlyList<VideoCodec> SupportedCodecs => Available ? [VideoCodec.H265, VideoCodec.H264] : [];

        public string? UnavailableReason => Available ? null : "avcodec-63.dll not found";

        public IVideoDecoder Create(VideoCodec codec)
        {
            var decoder = new FakeDecoder();
            lock (Created)
            {
                Created.Add(decoder);
            }

            return decoder;
        }
    }

    private sealed class FakeDecoder : IVideoDecoder
    {
        public bool Disposed { get; private set; }

        public LiveImage? Decode(LiveViewFrame frame) => new(new FakeImage(), frame.Width, frame.Height);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeImage : IImage, IDisposable
    {
        public bool Disposed { get; private set; }

        public Size Size => new(640, 360);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
