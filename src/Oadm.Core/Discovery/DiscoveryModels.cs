using System.Net;

namespace Oadm.Core.Discovery;

/// <summary>Authentication state of a discovered device, as far as an anonymous probe can tell.</summary>
public enum DiscoveredDeviceStatus
{
    /// <summary>Not probed yet (e.g. just seen via mDNS).</summary>
    Unknown = 0,

    /// <summary>Factory default, no administrator password set (systemready.cgi needsetup = yes).</summary>
    PasswordNotSet,

    /// <summary>Password set; VAPIX answered 401 with an AXIS_&lt;serial&gt; realm.</summary>
    CredentialsRequired,

    /// <summary>Password set but getAllProperties answered 200 without credentials (anonymous access enabled).</summary>
    AnonymousAccess,

    /// <summary>Seen via mDNS but the HTTP(S) probe got no Axis answer.</summary>
    Unreachable,
}

/// <summary>How a device was found. A device can be found by several sources within one session.</summary>
[Flags]
public enum DiscoverySources
{
    None = 0,
    Mdns = 1,
    RangeScan = 2,

    /// <summary>An address the user entered ("Add manually").</summary>
    Manual = 4,
}

/// <summary>Result of an anonymous, read-only VAPIX probe of one address.</summary>
/// <param name="Address">Probed address.</param>
/// <param name="Serial">Serial/MAC, upper hex without separators.</param>
/// <param name="Scheme">"https" or "http", whichever answered first.</param>
/// <param name="Model">ProdNbr, e.g. "P3265-V", when the device disclosed it anonymously.</param>
/// <param name="ProductName">ProdFullName, e.g. "AXIS P3265-V Dome Camera".</param>
/// <param name="FirmwareVersion">AXIS OS version, e.g. "12.11.77".</param>
/// <param name="Status">Authentication state.</param>
/// <param name="AnonymousFullAccess">True when getAllProperties answered 200 without credentials.</param>
/// <param name="ProductType">ProdType, e.g. "Dome Camera", when the device disclosed it anonymously.</param>
public sealed record DeviceProbeResult(
    IPAddress Address,
    string Serial,
    string Scheme,
    string? Model,
    string? ProductName,
    string? FirmwareVersion,
    DiscoveredDeviceStatus Status,
    bool AnonymousFullAccess,
    string? ProductType = null);

/// <summary>A device in a discovery session, deduplicated by serial.</summary>
/// <param name="DiscoveredId">Stable id within the session. Equal to <paramref name="Serial"/>.</param>
/// <param name="Serial">Serial/MAC, upper hex without separators.</param>
/// <param name="Address">IPv4 address (latest seen).</param>
/// <param name="HostName">mDNS host name without ".local", null if not seen via mDNS.</param>
/// <param name="Model">ProdNbr, null until known.</param>
/// <param name="FirmwareVersion">AXIS OS version, null until known.</param>
/// <param name="Status">Authentication state.</param>
/// <param name="Scheme">"https" or "http", null until probed.</param>
/// <param name="Sources">All sources that reported this device in the session.</param>
/// <param name="LastSeenUtc">Last time any source reported the device.</param>
/// <param name="ProductType">ProdType, null until known.</param>
/// <param name="EnteredAddress">Manual probe only: the address as the user entered it (host[:port]); becomes the device address on add.</param>
/// <param name="DnsName">The verified DNS name of <paramref name="Address"/> (<see cref="IDnsNameLookup"/>), null when DNS has none or
/// the lookup did not finish yet; the device address on add when the page asks for DNS names.</param>
public sealed record DiscoveredDevice(
    string DiscoveredId,
    string Serial,
    IPAddress Address,
    string? HostName,
    string? Model,
    string? FirmwareVersion,
    DiscoveredDeviceStatus Status,
    string? Scheme,
    DiscoverySources Sources,
    DateTimeOffset LastSeenUtc,
    string? ProductType = null,
    string? EnteredAddress = null,
    string? DnsName = null)
{
    /// <summary>The address to connect to: the entered one (may carry a port or host name), else the IP.</summary>
    public string ConnectAddress => EnteredAddress ?? Address.ToString();

    /// <summary>Where the device answered: the server's own network (default) or a site. Every connection to it uses this route.</summary>
    public Sites.DeviceRoute Route { get; init; }

    /// <summary>
    /// The other routes the same device (MAC address) answered through in the session (docs/spec/sites.md,
    /// Discovery and moves at sites): the row shows <see cref="Route"/>, its tooltip names these.
    /// </summary>
    public DeviceRoutes OtherRoutes { get; init; } = DeviceRoutes.Empty;
}

/// <summary>A set of routes with value equality (a discovered device's other routes), in a stable order.</summary>
public sealed class DeviceRoutes : IEquatable<DeviceRoutes>
{
    private readonly Sites.DeviceRoute[] _routes;

    private DeviceRoutes(Sites.DeviceRoute[] routes)
    {
        _routes = routes;
    }

    public static DeviceRoutes Empty { get; } = new([]);

    public IReadOnlyList<Sites.DeviceRoute> Items => _routes;

    public int Count => _routes.Length;

    public bool Contains(Sites.DeviceRoute route) => Array.IndexOf(_routes, route) >= 0;

    /// <summary>This set with <paramref name="route"/> (the server's own network first, then the sites by id).</summary>
    public DeviceRoutes With(Sites.DeviceRoute route) =>
        Contains(route) ? this : new([.. _routes.Append(route).OrderBy(r => r.SiteId ?? Guid.Empty)]);

    /// <summary>This set without <paramref name="route"/>.</summary>
    public DeviceRoutes Without(Sites.DeviceRoute route) =>
        Contains(route) ? new([.. _routes.Where(r => r != route)]) : this;

    public bool Equals(DeviceRoutes? other) => other is not null && _routes.AsSpan().SequenceEqual(other._routes);

    public override bool Equals(object? obj) => Equals(obj as DeviceRoutes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var route in _routes)
        {
            hash.Add(route);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _routes);
}

/// <summary>Kind of discovery session.</summary>
public enum DiscoverySessionKind
{
    ZeroConf,
    RangeScan,

    /// <summary>One address entered by the user; finishes like a range scan.</summary>
    Manual,
}

/// <summary>Handle of a discovery session.</summary>
public sealed record DiscoverySession(string Id, DiscoverySessionKind Kind);

/// <summary>
/// One event of <see cref="DiscoveryService.WatchAsync"/>: either a new/changed device, or a
/// range scan progress/finish notification (then <see cref="Device"/> is null).
/// </summary>
/// <param name="Device">New or changed device, null for pure progress events.</param>
/// <param name="ProgressPercent">Range scan progress 0-100; 0 for zero-conf sessions.</param>
/// <param name="Finished">True on the final event of a range scan session.</param>
public sealed record DiscoveryEvent(DiscoveredDevice? Device, int ProgressPercent, bool Finished)
{
    /// <summary>
    /// The final event of an address probe that found nothing: why, when a site said so ("Port 8443 is not allowed at
    /// Building B. Allow it in the OADM Site Node app."); null = nothing answered.
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>One route of an address probe: where it probes and how (the caller resolves names and knows ports and schemes).</summary>
/// <param name="Route">The server's own network or a site.</param>
/// <param name="Probe">The anonymous probe on that route; null result = nothing answered. A <see cref="Sites.SiteException"/>
/// is the site's problem (offline, port not allowed), never "no device".</param>
public sealed record RouteProbe(Sites.DeviceRoute Route, Func<CancellationToken, Task<DeviceProbeResult?>> Probe);
