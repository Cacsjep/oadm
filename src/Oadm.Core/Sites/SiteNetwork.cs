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
    /// <summary>
    /// The sites whose node serves the server now (online, bound), by name: where discovery looks besides the server's
    /// own network (work stream 5). Default: none.
    /// </summary>
    IReadOnlyList<Guid> OnlineSites() => [];

    /// <summary>
    /// The bound site named <paramref name="nameOrId"/> (case-insensitive, online or not; also its id): the import's Site
    /// column. Null when there is none. Default: an id only.
    /// </summary>
    Guid? FindSite(string nameOrId) => Guid.TryParse(nameOrId, out var id) ? id : null;

    /// <summary>True when <paramref name="address"/> is on a network card the node reaches or in one of its extra networks (from Watch).</summary>
    bool Reaches(Guid siteId, IPAddress address);

    /// <summary>
    /// True when the node reaches at least one address of the IPv4 range <paramref name="first"/>..<paramref name="last"/>
    /// (a range scan runs there). Implementations compare the range with each network's bounds; the default asks
    /// <see cref="Reaches"/> for every address of the range.
    /// </summary>
    bool ReachesRange(Guid siteId, IPAddress first, IPAddress last)
    {
        var from = Ipv4Number(first);
        var to = Ipv4Number(last);
        for (var value = (ulong)Math.Min(from, to); value <= Math.Max(from, to); value++)
        {
            if (Reaches(siteId, Ipv4Address((uint)value)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An IPv4 address as a number (192.168.0.1 = 0xC0A80001).</summary>
    static uint Ipv4Number(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();
        return bytes.Length == 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) : throw new ArgumentException("Not an IPv4 address.", nameof(address));
    }

    /// <summary>The IPv4 address of a number (0xC0A80001 = 192.168.0.1).</summary>
    static IPAddress Ipv4Address(uint value)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    /// <summary>The addresses of <paramref name="name"/> as the node's DNS resolves it; empty when unknown.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(Guid siteId, string name, CancellationToken ct);

    /// <summary>
    /// The verified DNS name of <paramref name="address"/> on the node's side (a full name, not ".local" or ".arpa",
    /// resolving back to the same address: the rules of <c>SystemDnsNameLookup</c>); null when none.
    /// </summary>
    Task<string?> ReverseLookupAsync(Guid siteId, IPAddress address, CancellationToken ct);

    /// <summary>True when <paramref name="address"/> answers a ping from the node within <paramref name="timeout"/>.</summary>
    Task<bool> PingAsync(Guid siteId, string address, TimeSpan timeout, CancellationToken ct);

    /// <summary>True when a host answers on TCP <paramref name="port"/> at <paramref name="address"/> from the node: it accepts or refuses the connection.</summary>
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
