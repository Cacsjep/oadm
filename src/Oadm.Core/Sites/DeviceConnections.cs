using System.Net;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Vapix;

namespace Oadm.Core.Sites;

/// <summary>
/// The one place that builds <see cref="VapixConnectionOptions"/>, so every connection to a device carries its route
/// (docs/spec/sites.md, Server: Routing). A test checks that no other code in src/ or plugins/ creates options; the banned-API
/// analyzer cannot see a missing route. Callers change address, scheme or pin with a <c>with</c> expression; the route
/// stays the device's.
/// </summary>
public static class DeviceConnections
{
    /// <summary>The default per-request timeout of a device connection.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The route of a managed device: its site, or the server's own network.</summary>
    public static DeviceRoute RouteOf(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceRoute(device.SiteId);
    }

    /// <summary>"https" or "http" as the device row stores it.</summary>
    public static string SchemeOf(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Scheme == DeviceScheme.Http ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
    }

    /// <summary>
    /// A managed device: its stored address, scheme, pinned certificate and route, with <paramref name="credentials"/>.
    /// <paramref name="allowBasicOverHttp"/> is the server setting <c>Devices.AllowBasicOverHttp</c> (on every route).
    /// </summary>
    public static VapixConnectionOptions For(Device device, NetworkCredential? credentials = null, TimeSpan? timeout = null, bool allowBasicOverHttp = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new VapixConnectionOptions
        {
            Address = device.Address,
            Scheme = SchemeOf(device),
            Credentials = credentials,
            PinnedCertificateFingerprint = device.CertFingerprintSha256,
            Timeout = timeout ?? DefaultTimeout,
            Route = RouteOf(device),
            AllowBasicOverHttp = allowBasicOverHttp,
        };
    }

    /// <summary>
    /// A discovered device on the add page (not managed yet): its connect address and route on <paramref name="scheme"/>,
    /// trust on first use. <paramref name="allowBasicOverHttp"/> (the add page's login to a device that passed the Axis
    /// check) is honored through a site only when the server setting <c>Devices.AllowBasicOverHttp</c> is on
    /// (<paramref name="basicOverHttpSetting"/>): without it Basic over plain HTTP never crosses a site node.
    /// </summary>
    public static VapixConnectionOptions For(DiscoveredDevice device, string scheme, TimeSpan timeout, NetworkCredential? credentials = null,
        bool allowBasicOverHttp = false, bool basicOverHttpSetting = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        return new VapixConnectionOptions
        {
            Address = device.ConnectAddress,
            Scheme = scheme,
            Credentials = credentials,
            Timeout = timeout,
            Route = device.Route,
            AllowBasicOverHttp = basicOverHttpSetting || (allowBasicOverHttp && device.Route.IsLocal),
        };
    }
}
