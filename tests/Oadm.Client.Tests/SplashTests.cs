using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Controls;
using Oadm.Client.Shell;

namespace Oadm.Client.Tests;

/// <summary>The start splash: steps and progress, the logo geometry, and headless renders of the animation.</summary>
public sealed class SplashTests
{
    [Fact]
    public void Steps_set_the_status_and_the_progress_never_goes_back()
    {
        var vm = new SplashViewModel();
        Assert.Equal("Starting", vm.Status);

        vm.Step("Connecting to localhost:5080", 25);
        vm.Step("Loading devices", 60);
        vm.Step("Something late", 40);

        Assert.Equal("Something late", vm.Status);
        Assert.Equal(60, vm.Progress);
        vm.Step("Ready", 150);
        Assert.Equal(100, vm.Progress);
        Assert.True(SplashViewModel.MinimumDuration >= SplashLogo.CompleteAfter);
    }

    [Fact]
    public void The_hexagon_has_six_rounded_corners_inside_its_box()
    {
        string data = SplashLogo.HexPathData(SplashLogo.Radius);

        Assert.StartsWith("M", data, StringComparison.Ordinal);
        Assert.Equal(6, data.Count(c => c == 'Q'));
        Assert.EndsWith("Z", data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_splash_renders_the_logo_while_it_builds_and_when_it_is_complete()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var vm = new SplashViewModel();
            var window = new SplashWindow { DataContext = vm };
            window.Show();
            vm.Step("Connecting to localhost:5080", 25);
            await AdvanceAsync(TimeSpan.FromSeconds(1.2));
            Capture(window, outDir, "client-splash-building.png");

            vm.Step("Loading devices", 60);
            await AdvanceAsync(TimeSpan.FromSeconds(2.1));
            Capture(window, outDir, "client-splash.png");

            var logo = window.GetVisualDescendants().OfType<SplashLogo>().Single();
            var hexagons = logo.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().ToList();
            Assert.Equal(14, hexagons.Count); // seven hexagons: fill + outline each
            Assert.All(hexagons.Where(p => p.Fill is not null), p => Assert.True(p.Opacity > 0.99, "every hexagon is filled at 3.3 s"));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    /// <summary>
    /// Lets the animation run for <paramref name="time"/>: the headless render clock follows real time, so the frames are
    /// pumped (1/60 s) while the time really passes.
    /// </summary>
    private static async Task AdvanceAsync(TimeSpan time)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < time)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(16).ConfigureAwait(true);
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
