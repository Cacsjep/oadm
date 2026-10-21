using Avalonia.Data.Converters;

namespace Oadm.Client.Devices;

/// <summary>Whether a device at a site can open in Web View (docs/spec/sites.md, Client).</summary>
public enum SiteWebAccess
{
    /// <summary>Web View is turned off (or the client does not know it yet).</summary>
    Off,

    /// <summary>Web View is on and the role may open devices.</summary>
    Available,

    /// <summary>Web View is on, but the role may not show it or open devices.</summary>
    NotAllowed,
}

/// <summary>
/// The web interface of a device at a site opens in Web View (docs/spec/sites.md, Client: the user's computer usually cannot
/// reach the site); while Web View is off, or the role may not open devices, the address link and "Open web interface"
/// are disabled with the reason.
/// </summary>
public static class SiteWebViewTexts
{
    /// <summary>Tooltip of the address link of a device at a site.</summary>
    public const string OpenInWebView = "Open the web interface in Web View";

    /// <summary>Why the address link and "Open web interface" of a device at a site are disabled while Web View is off.</summary>
    public const string TurnOnWebView = "Turn on Web View to open devices at a site";

    /// <summary>Why they are disabled while the role may not open devices (the Web View page's own text).</summary>
    public const string NotAllowed = Oadm.Sdk.Client.DeviceTexts.OpenNotAllowed;

    /// <summary>The tooltip of the address link of a device on the server's own network.</summary>
    public const string OpenWebInterface = "Open the web interface of the device";

    /// <summary>The tooltip of a device at a site.</summary>
    public static string Tip(SiteWebAccess access) => access switch
    {
        SiteWebAccess.Available => OpenInWebView,
        SiteWebAccess.NotAllowed => NotAllowed,
        _ => TurnOnWebView,
    };

    /// <summary>[is at site, the tooltip of a device at a site] -> the address link's tooltip.</summary>
    public static IMultiValueConverter LinkTip { get; } = new FuncMultiValueConverter<object?, string>(values =>
    {
        object?[] items = [.. values];
        bool atSite = items.Length > 0 && items[0] is true;
        return atSite && items.Length > 1 && items[1] is string siteTip ? siteTip : OpenWebInterface;
    });

    /// <summary>[is at site, Web View available] -> whether the address link works.</summary>
    public static IMultiValueConverter LinkEnabled { get; } = new FuncMultiValueConverter<bool, bool>(values =>
    {
        bool[] flags = [.. values];
        return !(flags.Length > 0 && flags[0]) || (flags.Length > 1 && flags[1]);
    });
}
