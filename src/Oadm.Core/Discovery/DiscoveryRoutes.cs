using Oadm.Core.Sites;

namespace Oadm.Core.Discovery;

/// <summary>
/// Where a discovery session may look (docs/spec/device-scope.md, Decisions "New devices"): the server's own network and the
/// sites. A user with all devices and the server itself look everywhere (<see cref="All"/>); a user with a device group only
/// at the sites of the group, and on the server's own network only when the group has "This server".
/// </summary>
/// <param name="Local">The server's own network (zero-conf, the range scan and address probes there).</param>
/// <param name="Sites">The sites that may be asked; null = every online site.</param>
public sealed record DiscoveryRoutes(bool Local, IReadOnlySet<Guid>? Sites)
{
    /// <summary>Everywhere: the server's own network and every online site.</summary>
    public static DiscoveryRoutes All { get; } = new(true, null);

    /// <summary>Nowhere (a group of tags or single devices only).</summary>
    public static DiscoveryRoutes None { get; } = new(false, new HashSet<Guid>());

    /// <summary>True when a session may look anywhere at all.</summary>
    public bool Any => Local || Sites is null || Sites.Count > 0;

    /// <summary>True when the session may use <paramref name="route"/>.</summary>
    public bool Allows(DeviceRoute route) => route.SiteId is { } site ? Allows(site) : Local;

    /// <summary>True when the session may ask <paramref name="siteId"/>.</summary>
    public bool Allows(Guid siteId) => Sites is null || Sites.Contains(siteId);
}
