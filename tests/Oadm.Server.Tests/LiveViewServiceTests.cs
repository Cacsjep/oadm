using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;
using Oadm.Server.Tests.Support;
using Oadm.Sdk.Devices;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>LiveViewService over the in-process server with a fake camera stream behind the real hub.</summary>
public sealed class LiveViewServiceTests
{
    [Fact]
    public async Task TwoViewersShareOneUpstreamThatClosesAfterTheLastLeaves()
    {
        var factory = new FakeSourceFactory();
        await using var host = await StartAsync(factory);
        var device = await AddDeviceAsync(host);

        using var cancelA = new CancellationTokenSource();
        using var cancelB = new CancellationTokenSource();
        using var a = host.LiveView.Watch(Request(device, Proto.VideoCodec.H264, Proto.VideoCodec.H265), cancellationToken: cancelA.Token);
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(factory.Opened.Count == 1), "first upstream");
        using var b = host.LiveView.Watch(Request(device, Proto.VideoCodec.H265), cancellationToken: cancelB.Token);
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(host.Get<LiveViewHub>().ActiveUpstreams == 1), "hub upstream");

        var source = factory.Opened.Single();
        Assert.Equal(VideoCodecKind.H265, source.Key.Codec);
        Assert.Equal("640x360", source.Key.Size.ToString());
        source.Emit(Frame(true, 1));
        source.Emit(Frame(false, 2));

        var fromA = await ReadAsync(a, 2);
        var fromB = await ReadAsync(b, 2);
        Assert.Equal(Proto.VideoCodec.H265, fromA[0].Codec);
        Assert.True(fromA[0].Keyframe);
        Assert.Equal([0, 0, 0, 1, 0x26], fromA[0].Data.ToByteArray());
        Assert.Equal((640, 360), (fromA[0].Width, fromA[0].Height));
        Assert.NotNull(fromA[0].Captured);
        Assert.Equal([1L, 2L], fromB.Select(f => f.RtpTimestamp));
        Assert.Single(factory.Opened);

        await cancelA.CancelAsync();
        await Task.Delay(100);
        Assert.False(source.Disposed);

        await cancelB.CancelAsync();
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(source.Disposed), "upstream closed after the last viewer");
        Assert.Equal(0, host.Get<LiveViewHub>().ActiveUpstreams);
    }

    [Fact]
    public async Task ListSourcesReturnsViewAreasAndWatchStreamsTheChosenOne()
    {
        var factory = new FakeSourceFactory
        {
            Sources = [new(1, "View Area 1", 0, [new(1920, 1080), new(640, 360)]), new(2, "View Area 2", 0, [new(1280, 720), new(640, 360)])],
        };
        await using var host = await StartAsync(factory);
        var device = await AddDeviceAsync(host);

        var sources = await host.LiveView.ListSourcesAsync(new Proto.LiveViewSourcesRequest { DeviceId = device.ToString() });
        Assert.Collection(
            sources.Sources,
            s => Assert.Equal((1, "View Area 1", 1920, 1080), (s.Camera, s.Name, s.MaxWidth, s.MaxHeight)),
            s => Assert.Equal((2, "View Area 2", 1280, 720), (s.Camera, s.Name, s.MaxWidth, s.MaxHeight)));

        var request = Request(device, Proto.VideoCodec.H264);
        request.Camera = 2;
        using var call = host.LiveView.Watch(request);
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(factory.Opened.Count == 1), "upstream");
        Assert.Equal(2, factory.Opened[0].Key.Camera);
        factory.Opened[0].Emit(Frame(true, 1));
        var frame = Assert.Single(await ReadAsync(call, 1));
        Assert.Equal(2, frame.Camera);
    }

    [Fact]
    public async Task DefaultCameraIsReportedInTheFrames()
    {
        var factory = new FakeSourceFactory();
        await using var host = await StartAsync(factory);
        var device = await AddDeviceAsync(host);
        using var call = host.LiveView.Watch(Request(device, Proto.VideoCodec.H264));
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(factory.Opened.Count == 1), "upstream");
        factory.Opened[0].Emit(Frame(true, 1));
        Assert.Equal(1, Assert.Single(await ReadAsync(call, 1)).Camera);
    }

    [Fact]
    public async Task UnknownSourceIsAFailedPrecondition()
    {
        await using var host = await StartAsync(new FakeSourceFactory());
        var device = await AddDeviceAsync(host);
        var request = Request(device, Proto.VideoCodec.H264);
        request.Camera = 5;
        using var call = host.LiveView.Watch(request);
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task UnknownDeviceIsNotFound()
    {
        await using var host = await StartAsync(new FakeSourceFactory());
        using var call = host.LiveView.Watch(new Proto.LiveViewRequest { DeviceId = Guid.NewGuid().ToString() });
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task NoCommonCodecIsAFailedPrecondition()
    {
        await using var host = await StartAsync(new FakeSourceFactory { CameraCodecs = [VideoCodecKind.H264] });
        var device = await AddDeviceAsync(host);
        using var call = host.LiveView.Watch(Request(device, Proto.VideoCodec.H265));
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task RejectedCredentialsArePermissionDenied()
    {
        await using var host = await StartAsync(new FakeSourceFactory { OpenError = new LiveViewException(LiveViewError.Unauthorized, "The camera rejected the stored credentials.") });
        var device = await AddDeviceAsync(host);
        using var call = host.LiveView.Watch(Request(device, Proto.VideoCodec.H264));
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task UpstreamFailureEndsTheCallAsUnavailable()
    {
        var factory = new FakeSourceFactory();
        await using var host = await StartAsync(factory);
        var device = await AddDeviceAsync(host);
        using var call = host.LiveView.Watch(Request(device, Proto.VideoCodec.H264));
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(factory.Opened.Count == 1), "upstream");
        factory.Opened[0].Fail(new LiveViewException(LiveViewError.Unreachable, "The camera stopped sending video."));
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal("The camera stopped sending video.", ex.Status.Detail);
    }

    [Fact]
    public async Task CertificateChangedDeviceIsRefused()
    {
        await using var host = await StartAsync(new FakeSourceFactory());
        var device = await AddDeviceAsync(host, DeviceStatus.CertificateChanged);
        using var call = host.LiveView.Watch(Request(device, Proto.VideoCodec.H264));
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    private static Task<TestServerHost> StartAsync(FakeSourceFactory factory) =>
        TestServerHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<ILiveVideoSourceFactory>();
            services.AddSingleton<ILiveVideoSourceFactory>(factory);
        });

    private static async Task<Guid> AddDeviceAsync(TestServerHost host, DeviceStatus status = DeviceStatus.Ok)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = "B8A44F631339", Address = "10.0.0.48", Model = "P3265-V", Status = status },
            CancellationToken.None);
        return device.Id;
    }

    private static Proto.LiveViewRequest Request(Guid device, params Proto.VideoCodec[] codecs)
    {
        var request = new Proto.LiveViewRequest { DeviceId = device.ToString(), MaxWidth = 640, MaxHeight = 360, Fps = 10 };
        request.AcceptedCodecs.AddRange(codecs);
        return request;
    }

    private static LiveVideoFrame Frame(bool keyframe, long ts) =>
        new(VideoCodecKind.H265, [0, 0, 0, 1, keyframe ? (byte)0x26 : (byte)0x02], keyframe, 640, 360, DateTimeOffset.UtcNow, null, ts);

    private static async Task<List<Proto.LiveViewFrame>> ReadAsync(AsyncServerStreamingCall<Proto.LiveViewFrame> call, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frames = new List<Proto.LiveViewFrame>();
        while (frames.Count < count && await call.ResponseStream.MoveNext(timeout.Token))
        {
            frames.Add(call.ResponseStream.Current);
        }

        return frames;
    }

    private sealed class FakeSourceFactory : ILiveVideoSourceFactory
    {
        public List<FakeSource> Opened { get; } = [];

        public IReadOnlyList<VideoCodecKind> CameraCodecs { get; init; } = [VideoCodecKind.H264, VideoCodecKind.H265];

        public Exception? OpenError { get; init; }

        public IReadOnlyList<VideoSourceInfo>? Sources { get; init; }

        public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct) =>
            Task.FromResult(new LiveViewCapabilities(CameraCodecs, [new(1920, 1080), new(1280, 720), new(640, 360), new(320, 180)], Sources));

        public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct)
        {
            if (OpenError is not null)
            {
                throw OpenError;
            }

            var source = new FakeSource(key);
            lock (Opened)
            {
                Opened.Add(source);
            }

            return Task.FromResult<ILiveVideoSource>(source);
        }
    }

    private sealed class FakeSource(LiveStreamKey key) : ILiveVideoSource
    {
        private readonly Channel<LiveVideoFrame> _frames = Channel.CreateUnbounded<LiveVideoFrame>();

        public LiveStreamKey Key { get; } = key;

        public bool Disposed { get; private set; }

        public void Emit(LiveVideoFrame frame) => _frames.Writer.TryWrite(frame);

        public void Fail(Exception error) => _frames.Writer.TryComplete(error);

        public async IAsyncEnumerable<LiveVideoFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
