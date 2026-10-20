using Avalonia;

namespace Oadm.Theme.Shell;

/// <summary>
/// Room the system window buttons take in the custom title bar (the OADM client's main window, the OADM Site Node app).
/// Avalonia reports only the title bar height (<c>Window.WindowDecorationMargin</c>), not where the buttons sit, so each
/// platform keeps its own space: macOS draws its three buttons on the left, Windows and Linux (drawn by Avalonia) on the
/// right, 46 px each like Windows.
/// </summary>
public static class TitleBarLayout
{
    /// <summary>Width of one caption button on Windows and Linux (theme <c>CaptionButtonWidth</c>).</summary>
    public const double CaptionButtonWidth = 46;

    /// <summary>Margin of the title bar content of a window with minimize, maximize and close: the app name left, the user and Log out right.</summary>
    public static Thickness ContentMargin { get; } = For(3);

    /// <summary>Margin of the title bar content of a window without maximize (<c>CanMaximize="False"</c>): minimize and close.</summary>
    public static Thickness ContentMarginWithoutMaximize { get; } = For(2);

    private static Thickness For(int buttons) => OperatingSystem.IsMacOS()
        ? new Thickness(80, 0, 12, 0)
        : new Thickness(20, 0, buttons * CaptionButtonWidth + 12, 0);
}
