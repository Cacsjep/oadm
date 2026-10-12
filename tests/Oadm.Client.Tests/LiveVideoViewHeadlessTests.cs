using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Client.Controls;
using Oadm.Client.Infrastructure;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>The live video rendered headless: what the picture area shows, the hover buttons, the controls, fullscreen.</summary>
public sealed class LiveVideoViewHeadlessTests
{
    private static readonly string? ShotDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");

    [FfmpegFact]
    public async Task The_side_panel_shows_the_picture_details_hover_buttons_and_the_controls()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var device = (await api.ListDevicesAsync(CancellationToken.None)).First(d => d.Model.Contains("P3265", StringComparison.Ordinal));
            var settings = new LiveVideoSettings { ShowDetails = true };
            using var video = new LiveVideoViewModel(api, new FfmpegVideoDecoderFactory(), new AvaloniaUiDispatcher(), NullLogger.Instance, settings);
            var panel = new LiveViewPanel { Title = "AXIS P3265-V  ·  View Area 1", Video = video };
            var window = new Window { Width = 760, Height = 520, Content = panel };
            window.Show();
            panel.IsOpen = true;
            Dispatcher.UIThread.RunJobs();
            video.Start(device.Id, title: device.Model);
            Assert.True(await UntilAsync(() => video.Image is not null && video.HasControls));

            // The size decides the resolution: the picture is about 700 px wide, not full HD.
            Assert.NotEqual(VideoDetails.None, video.Details);
            var hover = Find<StackPanel>(window, s => s.Classes.Contains("videoHover"));
            Assert.False(hover.IsVisible);
            var picture = window.GetVisualDescendants().OfType<Panel>().First(p => p.Name == "Picture");
            window.MouseMove(picture.TranslatePoint(new Point(picture.Bounds.Width / 2, picture.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(hover.IsVisible);
            Assert.False(Find<ProgressRing>(window, _ => true).IsEffectivelyVisible);
            Assert.True(Find<TextBlock>(window, t => t.Classes.Contains("videoDetails")).IsEffectivelyVisible);

            Assert.True(Find<OpticsBar>(window, _ => true).IsVisible); // open from the start
            Save(window, "client-liveview-panel.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_picture_area_shows_the_ring_the_error_or_the_GPU_warning()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var fakes = new LiveVideoFakes();
            fakes.Decoders.GpuProblem = GpuDecodingTexts.LinuxDriver;
            using var video = new LiveVideoViewModel(fakes.Api, fakes.Decoders, new AvaloniaUiDispatcher(), NullLogger.Instance, new LiveVideoSettings());
            var window = new Window { Width = 640, Height = 400, Content = new LiveVideoView { DataContext = video } };
            window.Show();
            video.Start("b");
            Dispatcher.UIThread.RunJobs();
            var ring = Find<ProgressRing>(window, _ => true);
            Assert.True(ring.IsEffectivelyVisible);

            Assert.True(await UntilAsync(() => fakes.StreamCount == 1));
            fakes.Streams[0].Send();
            Assert.True(await UntilAsync(() => video.HasDecodingWarning));
            Dispatcher.UIThread.RunJobs();
            var warning = Find<StatusChip>(window, c => c.Name == "GpuWarning");
            Assert.True(warning.IsEffectivelyVisible);
            Assert.Equal(GpuDecodingTexts.LinuxDriver, ToolTip.GetTip(warning));
            Assert.False(ring.IsEffectivelyVisible);

            fakes.Streams[0].Fail(new RpcException(new Status(StatusCode.PermissionDenied, "The camera rejected the stored credentials.")));
            Assert.True(await UntilAsync(() => video.HasError));
            Dispatcher.UIThread.RunJobs();
            var error = Find<StatusChip>(window, c => c.IsError);
            Assert.True(error.IsEffectivelyVisible);
            Assert.Equal("The camera rejected the stored credentials.", error.Text);
            Save(window, "client-liveview-error.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Fullscreen_opens_a_window_of_its_own_and_Esc_closes_it()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var fakes = new LiveVideoFakes();
            using var video = new LiveVideoViewModel(fakes.Api, fakes.Decoders, new AvaloniaUiDispatcher(), NullLogger.Instance, new LiveVideoSettings());
            var window = new Window { Width = 640, Height = 400, Content = new LiveVideoView { DataContext = video } };
            window.Show();
            video.Start("a");
            Assert.True(await UntilAsync(() => fakes.StreamCount == 1));

            video.ToggleFullscreenCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var fullscreen = Assert.IsType<FullscreenVideoWindow>(Assert.Single(window.OwnedWindows));
            Assert.Equal(WindowState.FullScreen, fullscreen.WindowState);
            Assert.True(await UntilAsync(() => fakes.StreamCount == 2)); // its own stream
            var own = fakes.Streams[1];

            fullscreen.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows);
            Assert.True(await UntilAsync(() => own.Cancelled));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static T Find<T>(Window window, Func<T, bool> match) where T : Visual =>
        window.GetVisualDescendants().OfType<T>().First(match);

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                return false;
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        return true;
    }

    private static void Save(Window window, string name)
    {
        if (!string.IsNullOrEmpty(ShotDir) && window.CaptureRenderedFrame() is { } frame)
        {
            Directory.CreateDirectory(ShotDir);
            frame.Save(Path.Combine(ShotDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
