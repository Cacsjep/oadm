using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Infrastructure;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;
using Oadm.Sdk.Vapix;

namespace Oadm.Client.Tests;

/// <summary>
/// The shared live video: resolution by size or Full, the silent swap to a new stream, stop while hidden, channels,
/// reconnects and errors, the details, the controls and fullscreen. Delays are zero unless a test needs them.
/// </summary>
public sealed class LiveVideoViewModelTests
{
    private static readonly LiveVideoOptions Instant = new()
    {
        ResizeDebounce = TimeSpan.Zero,
        HiddenStopDelay = TimeSpan.Zero,
        SlowStartDelay = TimeSpan.FromMinutes(1),
    };

    private readonly LiveVideoFakes _fakes = new();
    private readonly LiveVideoSettings _settings = new();

    private LiveVideoViewModel Create(LiveVideoOptions? options = null) =>
        new(_fakes.Api, _fakes.Decoders, new ImmediateUiDispatcher(), NullLogger.Instance, _settings, options ?? Instant)
        {
            Backoff = [TimeSpan.FromMilliseconds(20)],
        };

    private async Task<LiveVideoViewModel> LiveAsync(LiveVideoOptions? options = null, int width = 1000, int height = 560)
    {
        var video = Create(options);
        video.SetViewport(width, height);
        video.Start("a", title: "P3265-V");
        var stream = await _fakes.StreamAsync(0);
        stream.Send();
        await TestSupport.WaitUntilAsync(() => video.State == LiveViewState.Live);
        return video;
    }

    [Theory]
    [InlineData(1000, 560, 1280)]  // fills 1000 px wide without enlarging
    [InlineData(400, 225, 480)]
    [InlineData(320, 1000, 320)]   // narrow and tall: the width decides
    [InlineData(4000, 2250, 1920)] // larger than the camera: the largest
    [InlineData(0, 0, 1920)]
    public void The_resolution_is_the_smallest_that_fills_the_picture(int width, int height, int expected)
    {
        VideoResolution[] list = [new(1920, 1080), new(1280, 720), new(800, 450), new(640, 360), new(480, 270), new(320, 180), new(1080, 1080)];
        Assert.Equal(expected, LiveVideoViewModel.ChooseResolution(list, width, height).Width);
        Assert.Equal(new VideoResolution(640, 480), LiveVideoViewModel.ChooseResolution([], 640, 480));
    }

    [Fact]
    public void A_4_to_3_camera_that_also_lists_16_to_9_is_shown_in_16_to_9()
    {
        VideoResolution[] mixed = [new(2592, 1944), new(1920, 1080), new(1280, 960), new(1280, 720), new(800, 600), new(640, 360)];
        Assert.Equal(new VideoResolution(1280, 720), LiveVideoViewModel.ChooseResolution(mixed, 1000, 700));
        Assert.Equal(new VideoResolution(1920, 1080), LiveVideoViewModel.LargestResolution(mixed)); // Full
        Assert.Equal(new VideoResolution(1920, 1080), LiveVideoViewModel.ChooseResolution(mixed, 0, 0));

        VideoResolution[] onlyFourToThree = [new(2592, 1944), new(1280, 960), new(800, 600)];
        Assert.Equal(new VideoResolution(1280, 960), LiveVideoViewModel.ChooseResolution(onlyFourToThree, 1000, 700));
        Assert.Equal(new VideoResolution(2592, 1944), LiveVideoViewModel.LargestResolution(onlyFourToThree));
    }

    [Fact]
    public async Task Start_asks_for_the_resolution_of_the_size_at_the_adaptive_frame_rate_and_shows_the_picture()
    {
        using var video = Create();
        video.SetViewport(1000, 560);
        video.Start("a");
        Assert.True(video.ShowRing); // connecting: the ring, no picture
        var stream = await _fakes.StreamAsync(0);
        Assert.Equal("a", stream.Request.DeviceId);
        Assert.Equal([VideoCodec.H265, VideoCodec.H264], stream.Request.AcceptedCodecs);
        Assert.Equal((1280, 720, 20, 1), (stream.Request.MaxWidth, stream.Request.MaxHeight, stream.Request.Fps, stream.Request.Camera));

        stream.Send(VideoCodec.H265);
        await TestSupport.WaitUntilAsync(() => video.Image is not null);
        Assert.Equal(LiveViewState.Live, video.State);
        Assert.False(video.ShowRing);
        Assert.False(video.HasError);
        Assert.Equal(("H.265", "640x360"), (video.Details.Codec, video.Details.Resolution));
        Assert.False(video.ShowDetails); // the setting is off by default
        _settings.ShowDetails = true;
        Assert.True(video.ShowDetails);
    }

    [Fact]
    public async Task Full_quality_takes_the_largest_resolution_at_the_highest_frame_rate_and_swaps_silently()
    {
        using var video = await LiveAsync();
        var first = await _fakes.StreamAsync(0);
        var picture = video.Image;

        video.ToggleQualityCommand.Execute(null);
        Assert.Equal(LiveVideoQuality.Full, video.Quality);
        var second = await _fakes.StreamAsync(1);
        Assert.Equal((1920, 1080, 25), (second.Request.MaxWidth, second.Request.MaxHeight, second.Request.Fps));

        // Until the new stream delivers, the old one keeps showing.
        first.Send();
        await TestSupport.WaitUntilAsync(() => !ReferenceEquals(video.Image, picture));
        Assert.False(first.Cancelled);
        Assert.False(video.ShowRing);

        second.Send(width: 1920, height: 1080);
        await TestSupport.WaitUntilAsync(() => video.PictureSize.Width == 1920);
        await TestSupport.WaitUntilAsync(() => first.Cancelled);
        Assert.Equal(LiveViewState.Live, video.State);
    }

    [Fact]
    public async Task A_new_size_opens_a_new_stream_only_when_the_resolution_changes()
    {
        using var video = await LiveAsync(width: 1000, height: 560);
        video.SetViewport(1100, 600); // still 1280x720
        await Task.Delay(50);
        Assert.Equal(1, _fakes.StreamCount);

        video.SetViewport(400, 225);
        var second = await _fakes.StreamAsync(1);
        Assert.Equal((480, 270), (second.Request.MaxWidth, second.Request.MaxHeight));
        video.SetViewport(1000, 560); // back before the new one delivered: the new one is dropped
        await TestSupport.WaitUntilAsync(() => second.Cancelled);
        Assert.Equal(2, _fakes.StreamCount);
    }

    [Fact]
    public async Task A_size_must_rest_before_the_resolution_follows()
    {
        using var video = await LiveAsync(Instant with { ResizeDebounce = TimeSpan.FromSeconds(30) });
        video.SetViewport(400, 225);
        await Task.Delay(100);
        Assert.Equal(1, _fakes.StreamCount);
    }

    [Fact]
    public async Task A_new_stream_that_fails_keeps_the_old_one()
    {
        using var video = await LiveAsync();
        var first = await _fakes.StreamAsync(0);
        var picture = video.Image;

        video.ToggleQualityCommand.Execute(null);
        var second = await _fakes.StreamAsync(1);
        second.Fail(new RpcException(new Status(StatusCode.FailedPrecondition, "The camera refused the resolution.")));
        await Task.Delay(100);

        Assert.Equal(LiveViewState.Live, video.State);
        Assert.Same(picture, video.Image);
        Assert.False(first.Cancelled);
        Assert.False(video.ShowRing);
        Assert.Equal(2, _fakes.StreamCount); // no retry of the new one
        first.Send();
        await TestSupport.WaitUntilAsync(() => !ReferenceEquals(video.Image, picture));
    }

    [Fact]
    public async Task A_new_stream_that_is_slow_shows_the_ring_over_the_picture()
    {
        using var video = await LiveAsync(Instant with { SlowStartDelay = TimeSpan.Zero });
        Assert.False(video.ShowRing);
        video.ToggleQualityCommand.Execute(null);
        var second = await _fakes.StreamAsync(1);
        await TestSupport.WaitUntilAsync(() => video.ShowRing);
        Assert.NotNull(video.Image);

        second.Send(width: 1920, height: 1080);
        await TestSupport.WaitUntilAsync(() => !video.ShowRing);
    }

    [Fact]
    public async Task Hidden_it_stops_and_keeps_the_picture_shown_it_streams_again()
    {
        using var video = await LiveAsync();
        var first = await _fakes.StreamAsync(0);
        var picture = video.Image;

        video.SetVisible(false);
        await TestSupport.WaitUntilAsync(() => first.Cancelled);
        Assert.Equal(LiveViewState.Paused, video.State);
        Assert.Same(picture, video.Image);
        Assert.False(video.ShowRing);

        video.SetVisible(true);
        var second = await _fakes.StreamAsync(1);
        Assert.Same(picture, video.Image); // until the new stream delivers
        second.Send();
        await TestSupport.WaitUntilAsync(() => video.State == LiveViewState.Live && !ReferenceEquals(video.Image, picture));
    }

    [Fact]
    public async Task A_video_started_while_hidden_stops_again_and_the_first_size_counts()
    {
        using var video = Create(Instant with { HiddenStopDelay = TimeSpan.FromMilliseconds(200) });
        video.SetVisible(false); // behind the fullscreen window, another page
        video.Start("a");
        var first = await _fakes.StreamAsync(0);
        Assert.Equal((1920, 1080), (first.Request.MaxWidth, first.Request.MaxHeight)); // no size yet: the default
        await TestSupport.WaitUntilAsync(() => first.Cancelled);
        Assert.Equal(LiveViewState.Paused, video.State);

        video.SetVisible(true);
        video.SetViewport(400, 225); // the first size arrives after the stream started
        await TestSupport.WaitUntilAsync(() => _fakes.Streams.Any(s => s.Request.MaxWidth == 480));
    }

    [Fact]
    public async Task Hidden_for_a_moment_it_keeps_streaming()
    {
        using var video = await LiveAsync(Instant with { HiddenStopDelay = TimeSpan.FromSeconds(30) });
        video.SetVisible(false);
        video.SetVisible(true);
        await Task.Delay(50);
        Assert.False((await _fakes.StreamAsync(0)).Cancelled);
        Assert.Equal(1, _fakes.StreamCount);
    }

    [Fact]
    public async Task Several_channels_show_the_switch_and_a_choice_restarts_on_that_channel()
    {
        using var video = await LiveAsync();
        var first = await _fakes.StreamAsync(0);
        Assert.True(video.ShowSourceSwitch);
        Assert.Equal(["1", "2"], video.Sources.Select(s => s.Label));
        Assert.True(video.Sources[0].IsSelected);
        Assert.Equal("View Area 1", video.SourceName);
        var chosen = new List<int>();
        video.CameraChosen += (_, camera) => chosen.Add(camera);

        video.SelectSourceCommand.Execute(video.Sources[0]); // the shown one: nothing happens
        await Task.Delay(50);
        Assert.Equal(1, _fakes.StreamCount);

        video.SelectSourceCommand.Execute(video.Sources[1]);
        var second = await _fakes.StreamAsync(1);
        Assert.Equal(2, second.Request.Camera);
        Assert.Null(video.Image); // another channel: the old picture goes
        await TestSupport.WaitUntilAsync(() => first.Cancelled);
        Assert.True(video.Sources[1].IsSelected);
        Assert.Equal("View Area 2", video.SourceName);
        Assert.Equal([1, 2], chosen);
    }

    [Fact]
    public async Task A_grid_sets_the_channel_and_hides_the_switch()
    {
        using var video = Create(Instant with { ShowSourceSwitch = false });
        video.Start("a", camera: 2);
        Assert.Equal(2, (await _fakes.StreamAsync(0)).Request.Camera);
        await TestSupport.WaitUntilAsync(() => video.HasSourceChoice);
        Assert.False(video.ShowSourceSwitch);
        Assert.True(video.Sources[1].IsSelected);
    }

    [Fact]
    public async Task One_channel_has_no_switch()
    {
        using var video = Create();
        video.Start("b");
        await TestSupport.WaitUntilAsync(() => video.Sources.Count == 1);
        Assert.False(video.ShowSourceSwitch);
        Assert.Null(video.SourceName);
    }

    [Fact]
    public async Task A_new_picture_replaces_and_disposes_the_previous_one_and_stop_releases_everything()
    {
        using var video = await LiveAsync();
        var stream = await _fakes.StreamAsync(0);
        var first = (LiveVideoFakes.FakeImage)video.Image!;
        stream.Send();
        await TestSupport.WaitUntilAsync(() => !ReferenceEquals(video.Image, first));
        Assert.True(first.Disposed);

        var second = (LiveVideoFakes.FakeImage)video.Image!;
        video.Stop();
        Assert.Null(video.Image);
        Assert.True(second.Disposed);
        Assert.Equal(LiveViewState.Closed, video.State);
        await TestSupport.WaitUntilAsync(() => stream.Cancelled);
        await TestSupport.WaitUntilAsync(() => _fakes.Decoders.Created.All(d => d.Disposed));
    }

    [Fact]
    public async Task A_dropped_stream_reconnects_with_the_ring_and_the_reason()
    {
        using var video = await LiveAsync();
        (await _fakes.StreamAsync(0)).Fail(new RpcException(new Status(StatusCode.Unavailable, "The camera closed the video stream.")));
        var second = await _fakes.StreamAsync(1);
        second.Send();
        await TestSupport.WaitUntilAsync(() => video.State == LiveViewState.Live);
        Assert.Equal("a", second.Request.DeviceId);

        var waiting = new LiveVideoViewModel(_fakes.Api, _fakes.Decoders, new ImmediateUiDispatcher(), NullLogger.Instance, _settings, Instant)
        {
            Backoff = [TimeSpan.FromSeconds(30)],
        };
        using (waiting)
        {
            waiting.Start("b");
            (await _fakes.StreamAsync(2)).Fail(new RpcException(new Status(StatusCode.Unavailable, "The camera stopped sending video.")));
            await TestSupport.WaitUntilAsync(() => waiting.State == LiveViewState.Reconnecting);
            Assert.True(waiting.ShowRing);
            Assert.True(waiting.IsReconnecting); // the reason shows below the ring
            Assert.Equal("The camera stopped sending video. Retrying in 30 s.", waiting.StatusText);
        }
    }

    [Fact]
    public async Task A_permanent_error_shows_over_the_picture_without_retrying()
    {
        using var video = await LiveAsync();
        (await _fakes.StreamAsync(0)).Fail(new RpcException(new Status(StatusCode.PermissionDenied, "The camera rejected the stored credentials.")));
        await TestSupport.WaitUntilAsync(() => video.State == LiveViewState.Error);
        Assert.True(video.HasError);
        Assert.False(video.ShowRing);
        Assert.Null(video.Image);
        Assert.Equal("The camera rejected the stored credentials.", video.StatusText);
        await Task.Delay(50);
        Assert.Equal(1, _fakes.StreamCount);
    }

    [Fact]
    public void A_missing_decoder_shows_a_clear_error_and_never_calls_the_server()
    {
        _fakes.Decoders.Available = false;
        using var video = Create();
        video.Start("a");
        Assert.Equal(LiveViewState.Error, video.State);
        Assert.Equal(LiveVideoViewModel.DecoderUnavailableText, video.StatusText);
        _fakes.Api.DidNotReceive().WatchLiveViewAsync(Arg.Any<LiveViewRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_details_say_GPU_or_the_icon_warns_what_to_do()
    {
        _fakes.Decoders.UsesGpu = true;
        using (var video = await LiveAsync())
        {
            Assert.Equal("GPU", video.Details.Decoding);
            Assert.False(video.HasDecodingWarning);
        }

        _fakes.Decoders.UsesGpu = false;
        _fakes.Decoders.GpuProblem = GpuDecodingTexts.LinuxDriver;
        using var cpu = Create();
        cpu.Start("b");
        (await _fakes.StreamAsync(1)).Send();
        await TestSupport.WaitUntilAsync(() => cpu.HasDecodingWarning);
        Assert.Equal(GpuDecodingTexts.LinuxDriver, cpu.DecodingWarning);
        Assert.Equal("Processor", cpu.Details.Decoding);
        cpu.Stop();
        Assert.False(cpu.HasDecodingWarning);
    }

    [Fact]
    public async Task A_new_adaptive_frame_rate_opens_a_new_stream()
    {
        using var video = await LiveAsync();
        _settings.AdaptiveFps = 8;
        Assert.Equal(8, (await _fakes.StreamAsync(1)).Request.Fps);
        _settings.AdaptiveFps = 99;
        Assert.Equal(LiveVideoSettings.MaxAdaptiveFps, _settings.AdaptiveFps);
    }

    [Fact]
    public async Task The_controls_open_with_the_camera_the_button_closes_them_and_options_hide_parts()
    {
        _fakes.Api.GetOpticsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new OpticsState { Supported = true, HasZoom = true, HasFocus = true, MaxMagnification = 3 });
        using var video = await LiveAsync();
        await TestSupport.WaitUntilAsync(() => video.HasControls);
        Assert.True(video.IsControlsOpen); // open from the start
        Assert.True(video.ShowOpticsBar);
        Assert.False(video.ShowPtz);

        _settings.CanControlOptics = false; // a role without Optics Control
        Assert.False(video.HasControls);
        Assert.False(video.IsControlsOpen);
        _settings.CanControlOptics = true;
        Assert.True(video.IsControlsOpen); // the user did not close them

        video.ToggleControlsCommand.Execute(null);
        Assert.False(video.ShowOpticsBar);
        _settings.CanControlOptics = false;
        _settings.CanControlOptics = true;
        Assert.False(video.IsControlsOpen); // closed by the user stays closed

        using var closed = Create(Instant with { ControlsOpen = false });
        closed.Start("b");
        await TestSupport.WaitUntilAsync(() => closed.HasControls);
        Assert.False(closed.IsControlsOpen);

        using var noFocus = Create(Instant with { OpticsFocus = false });
        noFocus.Start("b");
        await TestSupport.WaitUntilAsync(() => noFocus.HasControls);
        Assert.True(noFocus.Optics.HasZoom);
        Assert.False(noFocus.Optics.HasFocus);

        using var noButton = Create(Instant with { ShowControlsButton = false });
        noButton.Start("b");
        await TestSupport.WaitUntilAsync(() => noButton.Optics.IsVisible);
        Assert.False(noButton.HasControls);
    }

    [Fact]
    public async Task The_PTZ_circle_shows_only_while_the_controls_are_open()
    {
        _fakes.Api.GetPtzAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PtzState { Supported = true, CanMove = true, CanZoom = true, Presets = { "Gate" } });
        using var video = await LiveAsync();
        await TestSupport.WaitUntilAsync(() => video.HasControls);
        Assert.True(video.ShowPtz);
        video.ToggleControlsCommand.Execute(null);
        Assert.False(video.ShowPtz);

        using var noPresets = Create(Instant with { PtzPresets = false, PtzMove = false });
        noPresets.Start("b");
        await TestSupport.WaitUntilAsync(() => noPresets.Ptz.IsVisible);
        Assert.False(noPresets.Ptz.HasPresets);
        Assert.False(noPresets.Ptz.CanMove);
        Assert.True(noPresets.Ptz.CanZoom);
    }

    [Fact]
    public async Task Fullscreen_has_its_own_stream_and_the_video_below_rests_meanwhile()
    {
        using var video = await LiveAsync();
        var below = await _fakes.StreamAsync(0);
        var requested = 0;
        video.FullscreenRequested += (_, _) => requested++;
        video.ToggleFullscreenCommand.Execute(null);
        Assert.Equal(1, requested);

        var fullscreen = video.CreateFullscreen();
        Assert.True(fullscreen.IsFullscreen);
        Assert.Equal("P3265-V", fullscreen.Title);
        var own = await _fakes.StreamAsync(1);
        Assert.Equal(1, own.Request.Camera);
        await TestSupport.WaitUntilAsync(() => below.Cancelled);
        Assert.Equal(LiveViewState.Paused, video.State);

        fullscreen.Dispose();
        await TestSupport.WaitUntilAsync(() => own.Cancelled);
        var again = await _fakes.StreamAsync(2);
        Assert.Equal("a", again.Request.DeviceId);

        using var noFullscreen = Create(Instant with { AllowFullscreen = false });
        noFullscreen.FullscreenRequested += (_, _) => requested++;
        noFullscreen.Start("b");
        noFullscreen.ToggleFullscreenCommand.Execute(null);
        Assert.Equal(1, requested);
    }
}
