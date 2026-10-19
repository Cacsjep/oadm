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
internal sealed class FakeSiteTransport : ISiteTransport, ISiteStates
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
