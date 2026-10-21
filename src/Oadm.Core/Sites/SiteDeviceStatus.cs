using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Sites;

/// <summary>
/// The status a device shows: its stored status, or <see cref="DeviceStatus.SiteOffline"/> while its site is not online
/// (CLAUDE.md "# Sites", Site offline). Computed on every read and every published change, never stored, O(1) per device.
/// </summary>
public static class SiteDeviceStatus
{
    /// <summary>
    /// Fills <see cref="Device.SiteName"/> and sets <see cref="Device.Status"/> to <see cref="DeviceStatus.SiteOffline"/>
    /// while the device's site is not online (an unknown site counts as offline). A site removed with Keep devices names
    /// itself "&lt;site&gt; (removed)" and marks <see cref="Device.SiteRemoved"/>. Devices on the server's own network are
    /// left as they are. Changes <paramref name="device"/> in place.
    /// </summary>
    public static void Apply(Device device, ISiteStates sites)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(sites);
        if (device.SiteId is not { } siteId)
        {
            device.SiteName = null;
            device.SiteRemoved = false;
            return;
        }

        var removed = sites.RemovedNameOf(siteId);
        device.SiteRemoved = removed is not null && sites.NameOf(siteId) is null;
        device.SiteName = sites.NameOf(siteId) ?? (removed is null ? null : Sdk.Sites.SiteTexts.RemovedSite(removed));
        if (!sites.IsOnline(siteId) && device.Status != DeviceStatus.SiteOffline)
        {
            device.StoredStatus = device.Status;
            device.Status = DeviceStatus.SiteOffline;
        }
    }

    /// <summary>The status a device with <paramref name="stored"/> status at <paramref name="siteId"/> shows now.</summary>
    public static DeviceStatus Of(DeviceStatus stored, Guid? siteId, ISiteStates sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        return siteId is { } id && !sites.IsOnline(id) ? DeviceStatus.SiteOffline : stored;
    }
}

/// <summary>
/// The sites while no site implementation is registered: there are none, so every site id is unknown and counts as
/// offline (devices that carry a site id stay Site offline); devices on the server's own network are not affected.
/// </summary>
public sealed class NoSiteStates : ISiteStates
{
    /// <summary>The one instance.</summary>
    public static readonly NoSiteStates Instance = new();

    event EventHandler<SiteStatesChangedEventArgs>? ISiteStates.Changed
    {
        add { }
        remove { }
    }

    public string? NameOf(Guid siteId) => null;

    public bool IsOnline(Guid siteId) => false;

    public TimeSpan RoundTrip(Guid siteId) => TimeSpan.Zero;
}

/// <summary>Site errors inside other exceptions (an HTTP request whose connect failed at the site).</summary>
public static class SiteErrors
{
    /// <summary>The <see cref="SiteException"/> in <paramref name="ex"/> or its inner exceptions, or null.</summary>
    public static SiteException? Find(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SiteException site)
            {
                return site;
            }

            if (e is AggregateException { InnerExceptions.Count: > 0 } aggregate && Find(aggregate.InnerExceptions[0]) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }
}
