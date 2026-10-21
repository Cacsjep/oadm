using System.Net;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;

namespace Oadm.Core.Sites;

/// <summary>
/// The <see cref="IDeviceNetwork"/> of every route (docs/spec/sites.md, Plugins and SDK): the server's own network
/// (<see cref="LocalDeviceNetwork"/>) or a site through its node (<see cref="SiteDeviceNetwork"/> over
/// <see cref="ISiteNetwork"/>). Handed to task execution, task queries and core plugins.
/// </summary>
public sealed class DeviceNetworks
{
    private readonly ISiteNetwork? _sites;
    private readonly ISiteStates? _states;
    private readonly IDeviceNetwork _local;

    /// <param name="sites">The node's network calls; null = a server without them (a site route refuses every call).</param>
    /// <param name="states">The sites' names for the texts.</param>
    /// <param name="local">The server's own network; null = <see cref="LocalDeviceNetwork.Instance"/> (tests pass a fake).</param>
    public DeviceNetworks(ISiteNetwork? sites = null, ISiteStates? states = null, IDeviceNetwork? local = null)
    {
        _sites = sites;
        _states = states;
        _local = local ?? LocalDeviceNetwork.Instance;
    }

    /// <summary>The server's own network only.</summary>
    public static DeviceNetworks Local { get; } = new();

    /// <summary>The route of a site; null = the server's own network.</summary>
    public IDeviceNetwork For(Guid? siteId) => siteId is { } id ? new SiteDeviceNetwork(id, _sites, _states) : _local;

    /// <summary>The route of <paramref name="device"/>.</summary>
    public IDeviceNetwork For(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return For(device.SiteId);
    }
}

/// <summary>
/// <see cref="IDeviceNetwork"/> through a site's node (<see cref="ISiteNetwork"/>). A site that cannot carry a call
/// (offline, the node too old) throws <see cref="SiteException"/> with the plain text ("Site offline: Building B").
/// </summary>
public sealed class SiteDeviceNetwork : IDeviceNetwork
{
    private readonly Guid _siteId;
    private readonly ISiteNetwork? _network;
    private readonly ISiteStates? _states;

    public SiteDeviceNetwork(Guid siteId, ISiteNetwork? network, ISiteStates? states = null)
    {
        _siteId = siteId;
        _network = network;
        _states = states;
    }

    public Guid? SiteId => _siteId;

    public Task<bool> PingAsync(string address, TimeSpan timeout, CancellationToken ct) =>
        Network().PingAsync(_siteId, address, timeout, ct);

    public Task<bool> ProbeTcpAsync(string address, int port, TimeSpan timeout, CancellationToken ct) =>
        Network().ProbeTcpAsync(_siteId, address, port, timeout, ct);

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var text = name.Trim().Trim('[', ']');
        return IPAddress.TryParse(text, out var ip)
            ? Task.FromResult<IReadOnlyList<IPAddress>>([ip])
            : Network().ResolveAsync(_siteId, text, ct);
    }

    public Task<IPAddress?> LocalAddressTowardsAsync(string address, CancellationToken ct) =>
        Network().LocalAddressForAsync(_siteId, address, ct);

    public bool Reaches(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _network is not null && _network.Reaches(_siteId, address);
    }

    private ISiteNetwork Network() =>
        _network ?? throw SiteException.For(_siteId, _states?.NameOf(_siteId) ?? _siteId.ToString(), SiteProblem.NotSupported);
}

/// <summary>
/// The relays of the server into its sites (docs/spec/sites.md, "Relay"), implemented by the server's site manager over the
/// node's <c>Relay</c> call. Each relay keeps itself open: it opens again after the node reconnects and says why it does
/// not run (<see cref="IRelay.State"/>).
/// </summary>
public interface ISiteRelays
{
    /// <summary>Every bound site now (not the nodes waiting to be bound, not a site whose node was reset).</summary>
    IReadOnlyList<ISiteInfo> BoundSites { get; }

    /// <summary>A site was bound, renamed, removed, or went online or offline.</summary>
    event EventHandler? SitesChanged;

    /// <summary>A UDP relay at <paramref name="siteId"/>; <see cref="ArgumentException"/> for an unknown site.</summary>
    IUdpRelay OpenUdp(Guid siteId, int port, string reason);

    /// <summary>A TCP relay at <paramref name="siteId"/>; <see cref="ArgumentException"/> for an unknown site.</summary>
    ITcpRelay OpenTcp(Guid siteId, int port, string reason);
}

/// <summary>A site as plugins see it (<see cref="ISiteInfo"/>).</summary>
public sealed record SiteInfo(Guid Id, string Name, bool IsOnline) : ISiteInfo;
