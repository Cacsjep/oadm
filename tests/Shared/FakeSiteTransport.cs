using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Oadm.Core.Sites;

namespace Oadm.Tests.Shared;

/// <summary>
/// Sites without a node, for tests of everything above <see cref="ISiteTransport"/> (docs/sites.md, work stream 0). Each
/// site has a name, online or not, its allowed Connect to ports and a map of (host, port) to a real loopback endpoint (a
/// FakeCamera), so two sites can both have 192.168.0.20 pointing to different cameras. An address that is not mapped
/// answers like a dead device (host unreachable); an address outside the site's networks or a port it does not allow is
/// a <see cref="SiteException"/>, like on a real node.
/// </summary>
internal sealed class FakeSiteTransport : ISiteTransport, ISiteStates, ISiteNetwork
{
    private readonly ConcurrentDictionary<Guid, FakeSite> _sites = new();

    public event EventHandler<SiteStatesChangedEventArgs>? Changed;

    /// <summary>Every dial, newest last: "site-name host:port Short".</summary>
    public ConcurrentQueue<string> Dials { get; } = new();

    public FakeSite AddSite(string name, bool online = true, Guid? id = null)
    {
        var site = new FakeSite(id ?? Guid.NewGuid(), name) { IsOnline = online };
        _sites[site.Id] = site;
        RaiseChanged(site.Id);
        return site;
    }

    public void RemoveSite(Guid siteId)
    {
        _sites.TryRemove(siteId, out _);
        RaiseChanged(siteId);
    }

    public void SetOnline(Guid siteId, bool online)
    {
        _sites[siteId].IsOnline = online;
        RaiseChanged(siteId);
    }

    public string? NameOf(Guid siteId) => _sites.TryGetValue(siteId, out var site) ? site.Name : null;

    public bool IsOnline(Guid siteId) => _sites.TryGetValue(siteId, out var site) && site.IsOnline && !site.NotBound;

    public bool IsStarting(Guid siteId) => _sites.TryGetValue(siteId, out var site) && site.Starting;

    /// <summary>Ends the start of a site (after a server start): online or offline now.</summary>
    public void Settle(Guid siteId, bool online)
    {
        _sites[siteId].Starting = false;
        _sites[siteId].IsOnline = online;
        RaiseChanged(siteId);
    }

    public TimeSpan RoundTrip(Guid siteId) => _sites.TryGetValue(siteId, out var site) ? site.RoundTrip : TimeSpan.Zero;

    public async ValueTask<Stream> OpenStreamAsync(Guid siteId, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct)
    {
        if (!_sites.TryGetValue(siteId, out var site))
            throw new SiteException(siteId, SiteProblem.NotBound, "Unknown site " + siteId);
        Dials.Enqueue($"{site.Name} {host}:{port} {kind}");
        if (site.NotBound)
            throw SiteException.For(siteId, site.Name, SiteProblem.NotBound);
        if (!site.IsOnline)
            throw SiteException.For(siteId, site.Name, SiteProblem.Offline);
        if (site.Busy)
            throw SiteException.For(siteId, site.Name, SiteProblem.Busy);
        if (!site.ConnectPorts.Contains(port))
            throw SiteException.For(siteId, site.Name, SiteProblem.PortNotAllowed, port);
        if (site.Reaches is { } reaches && !reaches(host))
            throw SiteException.For(siteId, site.Name, SiteProblem.OutsideNetworks);
        if (!site.Targets.TryGetValue((host.ToUpperInvariant(), port), out var target))
            throw new SocketException((int)SocketError.HostUnreachable);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var client = new TcpClient(target.AddressFamily);
        try
        {
            await client.ConnectAsync(target, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new SocketException((int)SocketError.TimedOut);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client.GetStream();
    }

    // ISiteNetwork: answers from the site's tables below, site problems like OpenStreamAsync.

    public IReadOnlyList<Guid> OnlineSites() =>
        [.. _sites.Values.Where(s => s.IsOnline && !s.NotBound && !s.Starting).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => s.Id)];

    public Guid? FindSite(string nameOrId) =>
        Guid.TryParse(nameOrId, out var id) ? (_sites.ContainsKey(id) ? id : null)
        : _sites.Values.FirstOrDefault(s => string.Equals(s.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    public bool Reaches(Guid siteId, IPAddress address) =>
        _sites.TryGetValue(siteId, out var site) && (site.Reaches is null || site.Reaches(address.ToString()));

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(Guid siteId, string name, CancellationToken ct)
    {
        var site = Usable(siteId);
        return Task.FromResult<IReadOnlyList<IPAddress>>(site.Names.TryGetValue(name.ToUpperInvariant(), out var addresses) ? addresses : []);
    }

    public Task<string?> ReverseLookupAsync(Guid siteId, IPAddress address, CancellationToken ct)
    {
        var site = Usable(siteId);
        return Task.FromResult(site.ReverseNames.TryGetValue(address, out var name) ? name : null);
    }

    public Task<bool> PingAsync(Guid siteId, string address, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(Usable(siteId).Answering.Contains(address));

    public Task<bool> ProbeTcpAsync(Guid siteId, string address, int port, TimeSpan timeout, CancellationToken ct)
    {
        var site = Usable(siteId);
        return Task.FromResult(site.Answering.Contains(address) || site.Targets.ContainsKey((address.ToUpperInvariant(), port)));
    }

    public Task<IPAddress?> LocalAddressForAsync(Guid siteId, string address, CancellationToken ct) =>
        Task.FromResult(Usable(siteId).LocalAddress);

    public async IAsyncEnumerable<SiteBrowseResult> BrowseAsync(Guid siteId, TimeSpan duration, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var site = Usable(siteId);
        foreach (var result in site.Announced)
        {
            ct.ThrowIfCancellationRequested();
            yield return result;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SiteRangeScanResult> RangeScanAsync(Guid siteId, IPAddress first, IPAddress last, int parallelism, TimeSpan timeout, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var site = Usable(siteId);
        var start = ToNumber(first);
        var end = ToNumber(last);
        var total = (int)(end - start + 1);
        var tried = 0;
        for (var n = start; n <= end; n++)
        {
            ct.ThrowIfCancellationRequested();
            tried++;
            var address = FromNumber(n);
            var open = RangePorts.Where(port => site.Targets.ContainsKey((address.ToString().ToUpperInvariant(), port))).ToArray();
            if (open.Length > 0)
                yield return new SiteRangeScanResult(address, open, tried, total, false);
        }
        yield return new SiteRangeScanResult(null, [], tried, total, true);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static readonly int[] RangePorts = [443, 80];

    private FakeSite Usable(Guid siteId)
    {
        if (!_sites.TryGetValue(siteId, out var site))
            throw new SiteException(siteId, SiteProblem.NotBound, "Unknown site " + siteId);
        if (site.NotBound)
            throw SiteException.For(siteId, site.Name, SiteProblem.NotBound);
        if (!site.IsOnline || site.Starting)
            throw SiteException.For(siteId, site.Name, SiteProblem.Offline);
        return site;
    }

    private static uint ToNumber(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    private static IPAddress FromNumber(uint n) => new([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);

    private void RaiseChanged(Guid siteId) => Changed?.Invoke(this, new SiteStatesChangedEventArgs([siteId]));

    internal sealed class FakeSite(Guid id, string name)
    {
        public Guid Id { get; } = id;
        public string Name { get; set; } = name;
        public bool IsOnline { get; set; }
        public bool NotBound { get; set; }

        /// <summary>After a server start, before the first connection settled (counts as not online).</summary>
        public bool Starting { get; set; }
        public bool Busy { get; set; }
        public TimeSpan RoundTrip { get; set; }

        /// <summary>Allowed Connect to ports (80, 443, 554 by default).</summary>
        public HashSet<int> ConnectPorts { get; } = [80, 443, 554];

        /// <summary>The site's networks; null = every address.</summary>
        public Func<string, bool>? Reaches { get; set; }

        /// <summary>Upper case host name -> its addresses on the node's DNS.</summary>
        public ConcurrentDictionary<string, IReadOnlyList<IPAddress>> Names { get; } = new();

        /// <summary>Address -> its verified DNS name on the node's side.</summary>
        public ConcurrentDictionary<IPAddress, string> ReverseNames { get; } = new();

        /// <summary>Addresses that answer ping (and any TCP probe).</summary>
        public HashSet<string> Answering { get; } = [];

        /// <summary>The node's address towards the devices.</summary>
        public IPAddress? LocalAddress { get; set; }

        /// <summary>What a Browse at the site finds.</summary>
        public List<SiteBrowseResult> Announced { get; } = [];

        /// <summary>(upper case host, port) -> where the fake device listens.</summary>
        public ConcurrentDictionary<(string Host, int Port), IPEndPoint> Targets { get; } = new();

        /// <summary>Points <paramref name="host"/>:<paramref name="port"/> at this site to <paramref name="target"/>.</summary>
        public FakeSite Map(string host, int port, IPEndPoint target)
        {
            Targets[(host.ToUpperInvariant(), port)] = target;
            return this;
        }
    }
}
