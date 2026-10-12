using Avalonia.Controls;
using Avalonia.Platform;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.WebView.Client;

/// <summary>Navigation rail page of the "Web View" core plugin: its own cards (device tree, browser).</summary>
public sealed class WebViewPage : ICorePluginPage
{
    public string PluginId => WebViewPluginInfo.PluginId;

    public string Title => WebViewPluginInfo.DisplayName;

    public bool HasOwnCards => true;

    /// <summary>Checks the embedded browser of this computer; the tests replace it.</summary>
    internal static Func<WebRuntimeState> RuntimeCheck { get; set; } = WebRuntime.Check;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // The host shows the same view again after another rail page and disposes the view model (IDisposable) when the
        // page is removed, so the page itself never disposes it on leaving the screen.
        return new WebViewPageView { DataContext = new WebViewPageViewModel(ctx, RuntimeCheck()) };
    }
}

/// <summary>
/// The embedded browser of the operating system: WebView2 on Windows, WKWebView on macOS, WPE WebKit or WebKitGTK on
/// Linux. When it is missing the page says what to install.
/// </summary>
public static class WebRuntime
{
    public const string WindowsMissing =
        "The Microsoft Edge WebView2 Runtime is not installed. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703, then open this page again.";

    public const string LinuxMissing =
        "WebKitGTK is not installed. Install it, then restart OADM. Debian and Ubuntu: sudo apt install libwebkit2gtk-4.1-0. "
        + "Fedora: sudo dnf install webkit2gtk4.1. Arch: sudo pacman -S webkit2gtk-4.1.";

    public const string MacMissing = "The web view of macOS is not available. It needs macOS 10.15 or later.";

    public const string OtherMissing = "This operating system has no web view OADM can use.";

    public static WebRuntimeState Check()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Of(WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2), WindowsMissing);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Of(WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WkWebView), MacMissing);
            }

            if (OperatingSystem.IsLinux())
            {
                var wpe = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WpeWebKit);
                return wpe.IsInstalled ? WebRuntimeState.Available : Of(WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebKitGtk), LinuxMissing);
            }

            return new WebRuntimeState(false, OtherMissing);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeLoadException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new WebRuntimeState(false, (OperatingSystem.IsWindows() ? WindowsMissing : OperatingSystem.IsMacOS() ? MacMissing : LinuxMissing) + " (" + ex.Message + ")");
        }
    }

    private static WebRuntimeState Of(DetailedWebViewAdapterInfo info, string missing) =>
        info.IsInstalled && info.IsSupported
            ? WebRuntimeState.Available
            : new WebRuntimeState(false, string.IsNullOrWhiteSpace(info.UnavailableReason) ? missing : missing + " (" + info.UnavailableReason + ")");
}
