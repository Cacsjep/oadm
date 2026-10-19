using Avalonia.Data.Converters;

namespace Oadm.Client.Devices;

/// <summary>
/// The web interface of a device at a site opens in Web View (docs/sites.md, Client: the user's computer usually cannot
/// reach the site); while Web View is off the address link and "Open web interface" are disabled with
/// <see cref="TurnOnWebView"/>.
/// </summary>
public static class SiteWebViewTexts
{
    /// <summary>Tooltip of the address link of a device at a site.</summary>
    public const string OpenInWebView = "Open the web interface in Web View";

    /// <summary>Why the address link and "Open web interface" of a device at a site are disabled.</summary>
    public const string TurnOnWebView = "Turn on Web View to open devices at a site";

    /// <summary>The tooltip of the address link of a device on the server's own network.</summary>
    public const string OpenWebInterface = "Open the web interface of the device";

    /// <summary>[is at site, Web View on] -> the address link's tooltip.</summary>
    public static IMultiValueConverter LinkTip { get; } = new FuncMultiValueConverter<bool, string>(values =>
    {
        bool[] flags = [.. values];
        bool atSite = flags.Length > 0 && flags[0];
        bool webViewOn = flags.Length > 1 && flags[1];
        return !atSite ? OpenWebInterface : webViewOn ? OpenInWebView : TurnOnWebView;
    });

    /// <summary>[is at site, Web View on] -> whether the address link works.</summary>
    public static IMultiValueConverter LinkEnabled { get; } = new FuncMultiValueConverter<bool, bool>(values =>
    {
        bool[] flags = [.. values];
        return !(flags.Length > 0 && flags[0]) || (flags.Length > 1 && flags[1]);
    });
}
