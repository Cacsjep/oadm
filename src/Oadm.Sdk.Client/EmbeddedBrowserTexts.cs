namespace Oadm.Sdk.Client;

/// <summary>
/// What to install when the embedded browser of the operating system is missing (WebView2 on Windows, WKWebView on macOS,
/// WPE WebKit or WebKitGTK on Linux): one text per system, shown by the Web View page and the Help window.
/// </summary>
public static class EmbeddedBrowserTexts
{
    /// <summary>Windows without the WebView2 runtime.</summary>
    public const string WindowsMissing =
        "The Microsoft Edge WebView2 Runtime is not installed. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703, then open this page again.";

    /// <summary>Linux without WPE WebKit and WebKitGTK.</summary>
    public const string LinuxMissing =
        "WebKitGTK is not installed. Install it, then restart OADM. Debian and Ubuntu: sudo apt install libwebkit2gtk-4.1-0. "
        + "Fedora: sudo dnf install webkit2gtk4.1. Arch: sudo pacman -S webkit2gtk-4.1.";

    /// <summary>macOS older than 10.15.</summary>
    public const string MacMissing = "The web view of macOS is not available. It needs macOS 10.15 or later.";

    /// <summary>Any other operating system.</summary>
    public const string OtherMissing = "This operating system has no web view OADM can use.";

    /// <summary>The text of the operating system this runs on.</summary>
    public static string ForThisSystem =>
        OperatingSystem.IsWindows() ? WindowsMissing
        : OperatingSystem.IsMacOS() ? MacMissing
        : OperatingSystem.IsLinux() ? LinuxMissing
        : OtherMissing;
}
