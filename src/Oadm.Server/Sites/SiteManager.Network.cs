using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Sites;

namespace Oadm.Server.Sites;

/// <summary>
/// <see cref="ISiteNetwork"/> over the control link of each site's node (site_node.proto Resolve, ReverseLookup, Ping,
/// ProbeTcp, LocalAddressFor, Browse, RangeScan), for discovery at sites and <c>IDeviceNetwork</c>. What the node reaches
/// comes from its Watch. A site that cannot carry a call is a <see cref="SiteException"/>: offline, not bound, an address
/// outside its networks, a port it does not allow, a node without the call.
/// </summary>
public sealed partial class SiteManager
{
    /// <summary>The bound sites whose node serves the server now, by name.</summary>
    public IReadOnlyList<Guid> OnlineSites()
    {
        lock (_lock)
        {
            return [.. _sites.Values
                .Where(e => e.State == SiteState.Online && !e.Row.NotBound && e.Session is { IsEnded: false, Ready: true })
                .OrderBy(e => e.Row.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Row.Id)];
        }
    }

    public Guid? FindSite(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return null;
        }

        var text = nameOrId.Trim();
        lock (_lock)
        {
            if (Guid.TryParse(text, out var id))
            {
                return _sites.ContainsKey(id) ? id : null;
            }

            return _sites.Values.FirstOrDefault(e => string.Equals(e.Row.Name, text, StringComparison.OrdinalIgnoreCase))?.Row.Id;
        }
    }

    public bool Reaches(Guid siteId, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) && entry.Networks.Contains(address);
        }
    }

    /// <summary>The range against each network's bounds: O(networks), never per address (a /16 x 100 sites).</summary>
    public bool ReachesRange(Guid siteId, IPAddress first, IPAddress last)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(last);
        SiteNetworks networks;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var entry))
            {
                return false;
            }

            networks = entry.Networks;
        }

        return networks.Intersects(first, last);
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(Guid siteId, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var (session, siteName) = Usable(siteId);
        var reply = await CallAsync(siteId, siteName, call => session.Control.Client.ResolveAsync(new ResolveRequest { Name = name.Trim() },
            deadline: Deadline(), cancellationToken: call), ct).ConfigureAwait(false);
        return [.. reply.Addresses.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>()];
    }

    public async Task<string?> ReverseLookupAsync(Guid siteId, IPAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        var (session, siteName) = Usable(siteId);
        var reply = await CallAsync(siteId, siteName, call => session.Control.Client.ReverseLookupAsync(new ReverseLookupRequest { Address = address.ToString() },
            deadline: Deadline(), cancellationToken: call), ct).ConfigureAwait(false);
        return reply.Name.Length == 0 ? null : reply.Name;
    }

    public async Task<bool> PingAsync(Guid siteId, string address, TimeSpan timeout, CancellationToken ct)
    {
        var ip = Reached(siteId, address, out var session, out var siteName);
        var reply = await CallAsync(siteId, siteName, call => session.Control.Client.PingAsync(new PingRequest { Address = ip.ToString(), TimeoutMs = Milliseconds(timeout) },
            deadline: Deadline(timeout), cancellationToken: call), ct).ConfigureAwait(false);
        return reply.Answered;
    }

    public async Task<bool> ProbeTcpAsync(Guid siteId, string address, int port, TimeSpan timeout, CancellationToken ct)
    {
        var ip = Reached(siteId, address, out var session, out var siteName);
        lock (_lock)
        {
            if (_sites.TryGetValue(siteId, out var entry) && !Allows(entry.Status, port))
            {
                throw SiteException.For(siteId, siteName, SiteProblem.PortNotAllowed, port);
            }
        }

        var reply = await CallAsync(siteId, siteName, call => session.Control.Client.ProbeTcpAsync(
            new ProbeTcpRequest { Address = ip.ToString(), Port = port, TimeoutMs = Milliseconds(timeout) },
            deadline: Deadline(timeout), cancellationToken: call), ct).ConfigureAwait(false);
        return reply.Open;
    }

    public async Task<IPAddress?> LocalAddressForAsync(Guid siteId, string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var (session, siteName) = Usable(siteId);
        var ip = Parse(siteId, siteName, address);
        var reply = await CallAsync(siteId, siteName, call => session.Control.Client.LocalAddressForAsync(new LocalAddressForRequest { Address = ip.ToString() },
            deadline: Deadline(), cancellationToken: call), ct).ConfigureAwait(false);
        return IPAddress.TryParse(reply.Address, out var local) ? local : null;
    }

    public async IAsyncEnumerable<SiteBrowseResult> BrowseAsync(Guid siteId, TimeSpan duration, [EnumeratorCancellation] CancellationToken ct)
    {
        var (session, siteName) = Usable(siteId);
        var seconds = (int)Math.Clamp(Math.Ceiling(duration.TotalSeconds), 1, 300);
        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Lifetime);
        using var call = session.Control.Client.Browse(new BrowseRequest { DurationSeconds = seconds },
            deadline: DateTime.UtcNow + TimeSpan.FromSeconds(seconds) + _options.CallTimeout, cancellationToken: life.Token);
        while (await Next(call.ResponseStream, siteId, siteName, ct).ConfigureAwait(false))
        {
            var found = call.ResponseStream.Current;
            yield return new SiteBrowseResult(found.Serial, found.InstanceName,
                [.. found.Addresses.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>()], found.Port, found.HostName);
        }
    }

    public async IAsyncEnumerable<SiteRangeScanResult> RangeScanAsync(Guid siteId, IPAddress first, IPAddress last, int parallelism, TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(last);
        var (session, siteName) = Usable(siteId);
        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Lifetime);
        using var call = session.Control.Client.RangeScan(new RangeScanRequest
        {
            From = first.ToString(),
            To = last.ToString(),
            Parallelism = parallelism,
            TimeoutMs = Milliseconds(timeout),
        }, cancellationToken: life.Token);
        var finished = false;
        while (!finished && await Next(call.ResponseStream, siteId, siteName, ct).ConfigureAwait(false))
        {
            var result = call.ResponseStream.Current;
            finished = result.Finished;
            IPAddress? address = IPAddress.TryParse(result.Address, out var ip) ? ip : null;
            yield return new SiteRangeScanResult(address, [.. result.OpenPorts], result.Tried, result.Total, result.Finished);
        }

        if (!finished)
        {
            // The node ended the stream without its last message: the link dropped.
            throw SiteException.For(siteId, siteName, SiteProblem.Offline);
        }
    }

    // ---------- helpers ----------

    /// <summary>The session of an online site, else the site's problem.</summary>
    private (SiteSession Session, string Name) Usable(Guid siteId)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var entry))
            {
                throw SiteException.For(siteId, siteId.ToString(), SiteProblem.Offline);
            }

            if (entry.Row.NotBound)
            {
                throw SiteException.For(siteId, entry.Row.Name, SiteProblem.NotBound);
            }

            var session = entry.Session;
            if (entry.State != SiteState.Online || session is null || session.IsEnded || !session.Ready)
            {
                throw SiteException.For(siteId, entry.Row.Name, SiteProblem.Offline);
            }

            return (session, entry.Row.Name);
        }
    }

    /// <summary>An address the node reaches (checked here first: the node refuses the others too).</summary>
    private IPAddress Reached(Guid siteId, string address, out SiteSession session, out string siteName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        (session, siteName) = Usable(siteId);
        var ip = Parse(siteId, siteName, address);
        if (!Reaches(siteId, ip))
        {
            throw SiteException.For(siteId, siteName, SiteProblem.OutsideNetworks);
        }

        return ip;
    }

    private static IPAddress Parse(Guid siteId, string siteName, string address) =>
        IPAddress.TryParse(address.Trim().Trim('[', ']'), out var ip)
            ? ip
            : throw new SiteException(siteId, SiteProblem.NotSupported, $"{address} is not an IP address. {siteName} checks IP addresses only.");

    private DateTime Deadline(TimeSpan? timeout = null) => DateTime.UtcNow + (timeout ?? TimeSpan.Zero) + _options.CallTimeout;

    private static int Milliseconds(TimeSpan timeout) => (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);

    /// <summary>One unary call on the control link; the node's refusals and a dropped link become site problems.</summary>
    private static async Task<T> CallAsync<T>(Guid siteId, string siteName, Func<CancellationToken, AsyncUnaryCall<T>> start, CancellationToken ct)
    {
        try
        {
            using var call = start(ct);
            return await call.ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException e) when (!ct.IsCancellationRequested)
        {
            throw ToSiteException(siteId, siteName, e);
        }
    }

    private static async Task<bool> Next<T>(IAsyncStreamReader<T> stream, Guid siteId, string siteName, CancellationToken ct)
    {
        try
        {
            return await stream.MoveNext(ct).ConfigureAwait(false);
        }
        catch (RpcException e) when (!ct.IsCancellationRequested)
        {
            throw ToSiteException(siteId, siteName, e);
        }
    }

    private static SiteException ToSiteException(Guid siteId, string siteName, RpcException e) => e.StatusCode switch
    {
        StatusCode.Unimplemented => SiteException.For(siteId, siteName, SiteProblem.NotSupported),
        StatusCode.FailedPrecondition or StatusCode.InvalidArgument => new SiteException(siteId, SiteProblem.NotSupported,
            string.IsNullOrWhiteSpace(e.Status.Detail) ? SiteException.For(siteId, siteName, SiteProblem.NotSupported).Message : e.Status.Detail),
        StatusCode.ResourceExhausted => SiteException.For(siteId, siteName, SiteProblem.Busy),
        _ => SiteException.For(siteId, siteName, SiteProblem.Offline),
    };
}

/// <summary>
/// The networks a site's node reaches, from its Watch (docs/spec/sites.md "Reach"): the addresses of the reached cards (or
/// every card with "All cards") with their prefixes, and the extra subnets. <see cref="Contains"/> is O(networks).
/// </summary>
public sealed class SiteNetworks
{
    private readonly (byte[] Network, int Prefix, AddressFamily Family)[] _networks;

    private SiteNetworks((byte[], int, AddressFamily)[] networks)
    {
        _networks = networks;
    }

    public static SiteNetworks None { get; } = new([]);

    public int Count => _networks.Length;

    public static SiteNetworks From(NodeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var list = new List<(byte[], int, AddressFamily)>();
        foreach (var card in status.Cards.Where(c => c.Reached || status.AllCards))
        {
            foreach (var text in card.Addresses)
            {
                if (TryParse(text, out var network))
                {
                    list.Add(network);
                }
            }
        }

        foreach (var text in status.ExtraSubnets)
        {
            if (TryParse(text, out var network))
            {
                list.Add(network);
            }
        }

        return new SiteNetworks([.. list]);
    }

    /// <summary>"192.168.0.5/24", "fd00::/64" or a single address.</summary>
    public static bool TryParse(string? text, out (byte[] Network, int Prefix, AddressFamily Family) network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? text.Trim() : text[..slash].Trim();
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > bits))
        {
            return false;
        }

        network = (Mask(address.GetAddressBytes(), prefix), prefix, address.AddressFamily);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (_networks.Length == 0)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        foreach (var (network, prefix, family) in _networks)
        {
            if (family == address.AddressFamily && Mask((byte[])bytes.Clone(), prefix).AsSpan().SequenceEqual(network))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when an IPv4 network includes at least one address of <paramref name="first"/>..<paramref name="last"/>.</summary>
    public bool Intersects(IPAddress first, IPAddress last)
    {
        var a = ISiteNetwork.Ipv4Number(first);
        var b = ISiteNetwork.Ipv4Number(last);
        var from = Math.Min(a, b);
        var to = Math.Max(a, b);
        foreach (var (network, prefix, family) in _networks)
        {
            if (family != AddressFamily.InterNetwork)
            {
                continue;
            }

            var start = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(network);
            var end = prefix >= 32 ? start : start | (uint.MaxValue >> prefix);
            if (start <= to && end >= from)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Mask(byte[] bytes, int prefix)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return bytes;
    }
}
