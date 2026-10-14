using Avalonia;

namespace Oadm.Client.Shell;

/// <summary>
/// Room the system window buttons take in the custom title bar. Avalonia reports only the title bar height
/// (<c>Window.WindowDecorationMargin</c>), not where the buttons sit, so each platform keeps its own space: macOS draws
/// its three buttons on the left, Windows and Linux (drawn by Avalonia) on the right, 3 x 46 px like Windows.
/// </summary>
public static class TitleBarLayout
{
    /// <summary>Margin of the title bar content: the app name left, the user and Log out right.</summary>
    public static Thickness ContentMargin { get; } = OperatingSystem.IsMacOS()
        ? new Thickness(80, 0, 12, 0)
        : new Thickness(20, 0, 3 * 46 + 12, 0);
}
