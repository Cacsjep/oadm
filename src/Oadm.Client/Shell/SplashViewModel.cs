using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Client.Shell;

/// <summary>
/// The start splash (user decision 2026-10-08): shown while the client really loads (server connection, remembered login,
/// first device list), the status line says the current step and the bar follows the steps. It stays at least until the
/// logo animation is complete (<see cref="MinimumDuration"/>), then closes as soon as the client is ready.
/// </summary>
public sealed partial class SplashViewModel : ObservableObject
{
    /// <summary>The logo and the wordmark are complete (<see cref="Controls.SplashLogo.CompleteAfter"/> plus the wordmark).</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(2.7);

    /// <summary>How long the client waits for the first device list before it shows the main window anyway.</summary>
    public static readonly TimeSpan DevicesTimeout = TimeSpan.FromSeconds(15);

    public const string Subtitle = "Open AXIS Device Management";

    [ObservableProperty]
    public partial string Status { get; private set; } = "Starting";

    /// <summary>0..100.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; } = 5;

    public void Step(string status, double progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        Status = status;
        Progress = Math.Clamp(Math.Max(progress, Progress), 0, 100);
    }
}
