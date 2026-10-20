namespace Oadm.Client.Shell;

/// <summary>The animations of the start splash (the window; a fake in tests).</summary>
public interface ISplashAnimation
{
    /// <summary>The mark slices in and rests.</summary>
    Task PlayIntroAsync();

    /// <summary>The mark slices out and the splash fades.</summary>
    Task PlayExitAsync();
}

/// <summary>
/// When the start splash closes: the intro plays while the client loads; the splash stays until the intro is done, at
/// least <see cref="MinimumShown"/> after the start and until the loading is done; then "Ready", the next window opens
/// behind the splash and draws its first frame, and only then the exit plays over it (no blank window in between: the
/// main window's first layout takes a moment).
/// </summary>
public static class SplashSequence
{
    /// <summary>The mark stays at least this long, also when the client is ready earlier.</summary>
    public static readonly TimeSpan MinimumShown = TimeSpan.FromMilliseconds(650);

    public static async Task RunAsync(
        ISplashAnimation view, SplashViewModel splash, Func<Task> load, Func<Task> showNext, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(splash);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(showNext);
        ArgumentNullException.ThrowIfNull(time);

        var minimum = Task.Delay(MinimumShown, time);
        var intro = view.PlayIntroAsync();
        await load().ConfigureAwait(true);
        await Task.WhenAll(intro, minimum).ConfigureAwait(true);
        splash.Step("Ready");
        await showNext().ConfigureAwait(true);
        await view.PlayExitAsync().ConfigureAwait(true);
    }
}
