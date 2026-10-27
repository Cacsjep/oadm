using Avalonia.Platform;

using Oadm.Sdk.Client;

namespace Oadm.Client.Help;

/// <summary>Whether the embedded browser exists on this computer, and what to install when not.</summary>
public sealed record EmbeddedBrowserState(bool IsAvailable, string? Problem)
{
    public static EmbeddedBrowserState Available { get; } = new(true, null);
}

/// <summary>
/// The embedded browser of the operating system for the Help window: WebView2 on Windows, WKWebView on macOS, WPE WebKit
/// or WebKitGTK on Linux, checked like the Web View page does; the texts are <see cref="EmbeddedBrowserTexts"/>.
/// </summary>
public static class EmbeddedBrowser
{
    public static EmbeddedBrowserState Check()
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
                return wpe.IsInstalled ? EmbeddedBrowserState.Available : Of(WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebKitGtk), EmbeddedBrowserTexts.LinuxMissing);
            }

            return new EmbeddedBrowserState(false, EmbeddedBrowserTexts.OtherMissing);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeLoadException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new EmbeddedBrowserState(false, EmbeddedBrowserTexts.ForThisSystem + " (" + ex.Message + ")");
        }
    }

    private static EmbeddedBrowserState Of(DetailedWebViewAdapterInfo info, string missing) =>
        info.IsInstalled && info.IsSupported
            ? EmbeddedBrowserState.Available
            : new EmbeddedBrowserState(false, string.IsNullOrWhiteSpace(info.UnavailableReason) ? missing : missing + " (" + info.UnavailableReason + ")");
}
