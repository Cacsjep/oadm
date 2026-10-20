using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Client.Shell;

/// <summary>
/// The start splash: shown while the client really loads (server connection, remembered login, first device list); the
/// line below the mark says the current step. See <see cref="SplashSequence"/> for when it closes.
/// </summary>
public sealed partial class SplashViewModel : ObservableObject
{
    /// <summary>How long the client waits for the first device list before it shows the main window anyway.</summary>
    public static readonly TimeSpan DevicesTimeout = TimeSpan.FromSeconds(15);

    [ObservableProperty]
    public partial string Status { get; private set; } = "Starting";

    public void Step(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        Status = status;
    }
}
