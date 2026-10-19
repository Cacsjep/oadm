using System.Net;

namespace Oadm.Core.Sites;

/// <summary>
/// The node's network calls for the server (site_node.proto: Resolve, ReverseLookup, Ping, ProbeTcp, LocalAddressFor,
/// Browse, RangeScan) and what the node reaches. Discovery at sites (work stream 5) and <c>IDeviceNetwork</c> (stream 6)
/// use it; the server's <c>SiteManager</c> implements it over the control link. Every call throws <see cref="SiteException"/>
/// when the site cannot carry it (offline, not bound, the node lacks the feature), never a device error.
/// </summary>
public interface ISiteNetwork
{
    /// <summary>True when <paramref name="address"/> is on a network card the node reaches or in one of its extra networks (from Watch).</summary>
    bool Reaches(Guid siteId, IPAddress address);

    /// <summary>The addresses of <paramref name="name"/> as the node's DNS resolves it; empty when unknown.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(Guid siteId, string name, CancellationToken ct);

    /// <summary>
    /// The verified DNS name of <paramref name="address"/> on the node's side (a full name, not ".local" or ".arpa",
    /// resolving back to the same address: the rules of <c>SystemDnsNameLookup</c>); null when none.
    /// </summary>
    Task<string?> ReverseLookupAsync(Guid siteId, IPAddress address, CancellationToken ct);

    /// <summary>True when <paramref name="address"/> answers a ping from the node within <paramref name="timeout"/>.</summary>
    Task<bool> PingAsync(Guid siteId, string address, TimeSpan timeout, CancellationToken ct);

    /// <summary>True when TCP <paramref name="port"/> at <paramref name="address"/> accepts a connection from the node.</summary>
    Task<bool> ProbeTcpAsync(Guid siteId, string address, int port, TimeSpan timeout, CancellationToken ct);

    /// <summary>The node's own address towards <paramref name="address"/>; null when it has none.</summary>
    Task<IPAddress?> LocalAddressForAsync(Guid siteId, string address, CancellationToken ct);

    /// <summary>mDNS <c>_axis-video._tcp</c> on the node's reached cards for <paramref name="duration"/>, each answer as it comes.</summary>
    IAsyncEnumerable<SiteBrowseResult> BrowseAsync(Guid siteId, TimeSpan duration, CancellationToken ct);

    /// <summary>The first contact of a range scan on the node: every address of the range with port 443 and / or 80 open.</summary>
    IAsyncEnumerable<SiteRangeScanResult> RangeScanAsync(Guid siteId, IPAddress first, IPAddress last, int parallelism, TimeSpan timeout, CancellationToken ct);
}

/// <summary>One mDNS answer at a site.</summary>
/// <param name="Serial">The MAC address of the TXT record.</param>
/// <param name="InstanceName">"&lt;friendly name&gt; - &lt;serial&gt;".</param>
/// <param name="Addresses">The announced addresses.</param>
/// <param name="Port">The SRV port.</param>
/// <param name="HostName">"axis-&lt;serial&gt;.local".</param>
public sealed record SiteBrowseResult(string Serial, string InstanceName, IReadOnlyList<IPAddress> Addresses, int Port, string HostName);

/// <summary>Progress of a range scan at a site; <see cref="Address"/> is null on a progress-only message.</summary>
/// <param name="Address">An address with an open port, or null.</param>
/// <param name="OpenPorts">443 and / or 80.</param>
/// <param name="Tried">Addresses tried so far.</param>
/// <param name="Total">Addresses in the range.</param>
/// <param name="Finished">The last message: every address was tried.</param>
public sealed record SiteRangeScanResult(IPAddress? Address, IReadOnlyList<int> OpenPorts, int Tried, int Total, bool Finished);
