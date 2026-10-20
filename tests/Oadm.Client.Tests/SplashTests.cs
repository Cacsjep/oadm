using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Time.Testing;

using Oadm.Client.Controls;
using Oadm.Client.Shell;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>The start splash: the steps, when it closes, the slice intro of the mark, and headless renders.</summary>
public sealed class SplashTests
{
    [Fact]
    public void Steps_set_the_status()
    {
        var vm = new SplashViewModel();
        Assert.Equal("Starting", vm.Status);

        vm.Step("Connecting to localhost:5080");
        vm.Step("Loading devices");

        Assert.Equal("Loading devices", vm.Status);
        Assert.Throws<ArgumentException>(() => vm.Step(" "));
    }

    [Fact]
    public async Task The_next_window_draws_behind_the_splash_before_the_exit_runs()
    {
        var time = new FakeTimeProvider();
        var view = new FakeSplash();
        var vm = new SplashViewModel();
        var loaded = new TaskCompletionSource();
        var shown = false;

        var nextDrawn = new TaskCompletionSource();
        var run = SplashSequence.RunAsync(view, vm, () => loaded.Task, () =>
        {
            Assert.False(view.ExitStarted, "the next window opens behind the splash, before the exit");
            shown = true;
            return nextDrawn.Task;
        }, time);

        Assert.True(view.IntroStarted);
        view.Intro.SetResult();
        loaded.SetResult();
        await Eventually.StaysTrueAsync(() => !view.ExitStarted, "no exit before the minimum time", TimeSpan.FromMilliseconds(200));

        time.Advance(SplashSequence.MinimumShown);
        await Eventually.TrueAsync(() => shown, "the next window opens");
        Assert.Equal("Ready", vm.Status);
        await Eventually.StaysTrueAsync(() => !view.ExitStarted, "no exit before its first frame", TimeSpan.FromMilliseconds(200));

        nextDrawn.SetResult();
        await Eventually.TrueAsync(() => view.ExitStarted, "the exit plays over the drawn window");
        view.Exit.SetResult();
        await run;
    }

    [Fact]
    public async Task The_splash_waits_for_the_loading_and_for_the_intro()
    {
        var time = new FakeTimeProvider();
        var view = new FakeSplash();
        var loaded = new TaskCompletionSource();
        var run = SplashSequence.RunAsync(view, new SplashViewModel(), () => loaded.Task, () => Task.CompletedTask, time);

        time.Advance(TimeSpan.FromSeconds(5));
        view.Intro.SetResult();
        await Eventually.StaysTrueAsync(() => !view.ExitStarted, "still loading", TimeSpan.FromMilliseconds(200));

        loaded.SetResult();
        await Eventually.TrueAsync(() => view.ExitStarted, "the exit starts once loaded");
        view.Exit.SetResult();
        await run;
    }

    [Fact]
    public async Task The_mark_slices_in_rests_whole_and_slices_out()
    {
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var mark = new SplashMark();
            Assert.Equal(SplashMark.Bands, mark.BandOpacities.Count);

            mark.ShowIntroAt(TimeSpan.Zero);
            Assert.All(mark.BandOpacities, o => Assert.Equal(0, o));
            Assert.False(mark.IsResting);

            mark.ShowIntroAt(TimeSpan.FromMilliseconds(100));
            Assert.True(mark.BandOpacities[0] > mark.BandOpacities[6], "the top band comes first");

            mark.ShowIntroAt(SplashMark.IntroDuration);
            Assert.All(mark.BandOpacities, o => Assert.Equal(1, o, 3));

            mark.ShowRest();
            Assert.True(mark.IsResting);
            Assert.All(mark.BandOpacities, o => Assert.Equal(0, o));

            mark.ShowExitAt(TimeSpan.FromMilliseconds(150));
            Assert.False(mark.IsResting);
            Assert.True(mark.BandOpacities[6] < mark.BandOpacities[0], "the bottom band leaves first");

            mark.ShowExitAt(SplashMark.ExitDuration);
            Assert.All(mark.BandOpacities, o => Assert.Equal(0, o, 3));
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_splash_shows_the_mark_and_the_step_only_and_plays_through()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var vm = new SplashViewModel();
            var window = new SplashWindow { DataContext = vm };
            window.Show();
            vm.Step("Connecting to localhost:5080");
            Pump();

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(["Connecting to localhost:5080"], texts);
            Assert.Empty(window.GetVisualDescendants().OfType<ProgressBar>());

            window.SplashMark.ShowIntroAt(TimeSpan.FromMilliseconds(220));
            Capture(window, outDir, "client-splash-intro.png");
            window.SplashMark.ShowRest();
            vm.Step("Loading devices");
            Capture(window, outDir, "client-splash.png");
            window.SplashMark.ShowExitAt(TimeSpan.FromMilliseconds(200));
            Capture(window, outDir, "client-splash-exit.png");

            // The real frames: the intro ends resting, the exit ends with the content faded.
            await PumpUntil(window.PlayIntroAsync());
            Assert.True(window.SplashMark.IsResting);
            await PumpUntil(window.PlayExitAsync());
            Assert.All(window.SplashMark.BandOpacities, o => Assert.Equal(0, o, 3));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps frames until the animation ended (the headless render clock follows real time).</summary>
    private static async Task PumpUntil(Task animation)
    {
        var ceiling = System.Diagnostics.Stopwatch.StartNew();
        while (!animation.IsCompleted && ceiling.Elapsed < TimeSpan.FromSeconds(30))
        {
            Pump();
            await Task.Delay(16).ConfigureAwait(true);
        }

        await animation.ConfigureAwait(true);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        Pump();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            string theme = Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") ?? "Dark";
            frame.Save(Path.Combine(outDir, theme.ToLowerInvariant() + "-" + name), PngBitmapEncoderOptions.Default);
        }
    }

    private sealed class FakeSplash : ISplashAnimation
    {
        public TaskCompletionSource Intro { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IntroStarted { get; private set; }

        public bool ExitStarted { get; private set; }

        public bool ExitDone => Exit.Task.IsCompleted;

        public Task PlayIntroAsync()
        {
            IntroStarted = true;
            return Intro.Task;
        }

        public Task PlayExitAsync()
        {
            ExitStarted = true;
            return Exit.Task;
        }
    }
}
