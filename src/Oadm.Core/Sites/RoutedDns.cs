using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Oadm.Core.Sites;

/// <summary>
/// Names resolve on the device's route (docs/sites.md, Server: Routing): the server's DNS for its own network, the site
/// node's DNS for a site. The only place the server asks DNS about devices (typed host names of the add page, "Use DNS
/// names instead of IP addresses").
/// </summary>
/// <remarks>
/// Through a site the node resolves (<c>Resolve</c> and <c>ReverseLookup</c> of site_node.proto, <see cref="ISiteNetwork"/>).
/// Without a site network (tools, tests) a site route resolves nothing: a forward lookup is a <see cref="SiteException"/>
/// (<see cref="SiteProblem.NotSupported"/>), a reverse lookup finds no name. A connection to a host name through a site
/// needs no lookup here: the node resolves the name when it dials.
/// </remarks>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The routed DNS: the server's own network asks the server's DNS here.")]
public sealed class RoutedDns
{
    private readonly ISiteStates? _states;
    private readonly ISiteNetwork? _network;

    public RoutedDns(ISiteStates? states = null, ISiteNetwork? network = null)
    {
        _states = states;
        _network = network;
    }

    /// <summary>The server's own network only.</summary>
    public static RoutedDns Local { get; } = new();

    /// <summary>The addresses of <paramref name="host"/> on <paramref name="route"/>.</summary>
    /// <exception cref="System.Net.Sockets.SocketException">DNS has no answer.</exception>
    /// <exception cref="SiteException">The site cannot answer (offline, no site network).</exception>
    public async Task<IPAddress[]> ResolveAsync(DeviceRoute route, string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (route.SiteId is { } site)
        {
            if (_network is null)
            {
                throw SiteException.For(site, _states?.NameOf(site) ?? site.ToString(), SiteProblem.NotSupported);
            }

            if (IPAddress.TryParse(host, out var literal))
            {
                return [literal];
            }

            var found = await _network.ResolveAsync(site, host, ct).ConfigureAwait(false);
            return found.Count > 0 ? [.. found] : throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        }

        return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
    }

    /// <summary>The name DNS gives <paramref name="address"/> on <paramref name="route"/> (reverse lookup), null when none.</summary>
    /// <exception cref="System.Net.Sockets.SocketException">DNS has no answer.</exception>
    /// <exception cref="SiteException">The site cannot answer (offline).</exception>
    public async Task<string?> ReverseLookupAsync(DeviceRoute route, IPAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (route.SiteId is { } site)
        {
            return _network is null ? null : await _network.ReverseLookupAsync(site, address, ct).ConfigureAwait(false);
        }

        var entry = await Dns.GetHostEntryAsync(address.ToString(), ct).ConfigureAwait(false);
        return entry.HostName;
    }
}
