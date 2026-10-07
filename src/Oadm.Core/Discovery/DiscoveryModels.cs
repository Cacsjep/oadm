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
public sealed record DeviceProbeResult(
    IPAddress Address,
    string Serial,
    string Scheme,
    string? Model,
    string? ProductName,
    string? FirmwareVersion,
    DiscoveredDeviceStatus Status,
    bool AnonymousFullAccess);

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
    DateTimeOffset LastSeenUtc);

/// <summary>Kind of discovery session.</summary>
public enum DiscoverySessionKind
{
    ZeroConf,
    RangeScan,
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
public sealed record DiscoveryEvent(DiscoveredDevice? Device, int ProgressPercent, bool Finished);
