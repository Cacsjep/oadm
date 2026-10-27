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
/// Linux. When it is missing the page says what to install (<see cref="EmbeddedBrowserTexts"/>, shared with the Help window).
/// </summary>
public static class WebRuntime
{
    public static WebRuntimeState Check()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Of(WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2), EmbeddedBrowserTexts.WindowsMissing);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Of(WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WkWebView), EmbeddedBrowserTexts.MacMissing);
            }

            if (OperatingSystem.IsLinux())
            {
                var wpe = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WpeWebKit);
                return wpe.IsInstalled ? WebRuntimeState.Available : Of(WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebKitGtk), EmbeddedBrowserTexts.LinuxMissing);
            }

            return new WebRuntimeState(false, EmbeddedBrowserTexts.OtherMissing);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeLoadException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new WebRuntimeState(false, EmbeddedBrowserTexts.ForThisSystem + " (" + ex.Message + ")");
        }
    }

    private static WebRuntimeState Of(DetailedWebViewAdapterInfo info, string missing) =>
        info.IsInstalled && info.IsSupported
            ? WebRuntimeState.Available
            : new WebRuntimeState(false, string.IsNullOrWhiteSpace(info.UnavailableReason) ? missing : missing + " (" + info.UnavailableReason + ")");
}
