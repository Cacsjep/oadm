using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Sites;
using Oadm.Sites.Transport;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Sites;

/// <summary>The links of <see cref="SiteManager"/>: connections nodes open, nodes the server reaches, waiting nodes.</summary>
public sealed partial class SiteManager
{
    /// <summary>
    /// A connection a node opened to the site port, through TLS: Hello tells the node and the link's role. A bound site's
    /// key makes it that site's control or worker link; an unknown key is a node waiting to be bound (limits per address
    /// and in all).
    /// </summary>
    private async Task AcceptLinkAsync(NodeLink link)
    {
        try
        {
            var hello = await HelloAsync(link).ConfigureAwait(false);
            if (hello is null)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Guid? siteId;
            lock (_lock)
            {
                siteId = _sites.Values.FirstOrDefault(e => string.Equals(e.Row.NodeKeyFingerprint, link.NodeFingerprintHex, StringComparison.OrdinalIgnoreCase))?.Row.Id;
            }

            if (siteId is { } id && hello.State == NodeState.Bound)
            {
                if (hello.LinkRole == LinkRole.Worker)
                {
                    SiteSession? session;
                    lock (_lock)
                    {
                        session = _sites.GetValueOrDefault(id)?.Session;
                    }

                    if (session is null || session.IsEnded)
                    {
                        await link.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        session.AddWorker(link);
                        RaiseList();
                    }

                    return;
                }

                await SwitchToOutboundAsync(id).ConfigureAwait(false);
                if (!await AttachControlAsync(id, link, fresh: false).ConfigureAwait(false))
                {
                    await link.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }

            // Not one of the sites (or a copy of one that is not bound): waits to be bound, within the limits.
            if (!CountNewConnection(link.RemoteHost))
            {
                LogTooManyConnections(link.RemoteHost);
                await link.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (!AddWaiting(link, hello, SiteMode.Outbound, null))
            {
                await link.DisposeAsync().ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A failing node connection never stops the listener.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLinkFailed(link.RemoteHost, ex.Message);
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<HelloReply?> HelloAsync(NodeLink link)
    {
        try
        {
            var hello = await link.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: _stop.Token).ConfigureAwait(false);
            link.Hello = hello;
            return hello;
        }
        catch (RpcException e)
        {
            LogLinkFailed(link.RemoteHost, e.Status.Detail);
            return null;
        }
    }

    private bool CountNewConnection(string address)
    {
        var now = Time.GetUtcNow();
        lock (_lock)
        {
            if (!_newConnections.TryGetValue(address, out var times))
            {
                times = new Queue<DateTimeOffset>();
                _newConnections[address] = times;
            }

            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromMinutes(1))
            {
                times.Dequeue();
            }

            if (times.Count >= _options.NewConnectionsPerMinute)
            {
                return false;
            }

            times.Enqueue(now);
            if (_newConnections.Count > 10_000)
            {
                foreach (var stale in _newConnections.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
                {
                    _newConnections.Remove(stale);
                }
            }

            return true;
        }
    }

    /// <summary>A node waiting to be bound (or found greyed). A newer link of the same node replaces the older one.</summary>
    private bool AddWaiting(NodeLink link, HelloReply hello, SiteMode mode, (string Host, int Port)? address)
    {
        if (string.IsNullOrEmpty(hello.NodeId))
        {
            return false;
        }

        string? copyOf;
        WaitingNode? replaced;
        var node = new WaitingNode(link, hello, mode, address, Time.GetUtcNow());
        lock (_lock)
        {
            copyOf = _sites.Values.FirstOrDefault(e =>
                string.Equals(e.Row.NodeId, hello.NodeId, StringComparison.Ordinal)
                || string.Equals(e.Row.NodeKeyFingerprint, link.NodeFingerprintHex, StringComparison.OrdinalIgnoreCase))?.Row.Name;
            node.CopyOf = copyOf;
            _waiting.TryGetValue(hello.NodeId, out replaced);
            if (replaced is null && _waiting.Count >= _options.MaxWaiting)
            {
                LogTooManyWaiting(link.RemoteHost);
                return false;
            }

            _waiting[hello.NodeId] = node;
        }

        if (replaced is not null && replaced.Link != link)
        {
            _ = replaced.Link.DisposeAsync().AsTask();
        }

        _ = link.Closed.ContinueWith(_ =>
        {
            bool removed;
            lock (_lock)
            {
                removed = _waiting.TryGetValue(hello.NodeId, out var current) && current == node && _waiting.Remove(hello.NodeId);
            }

            if (removed)
            {
                LogWaitingGone(node.Name, link.RemoteHost);
                RaiseList();
            }
        }, TaskScheduler.Default);
        LogWaiting(hello.HostName, link.RemoteHost);
        RaiseList();
        return true;
    }

    private async Task SwitchToOutboundAsync(Guid siteId)
    {
        CancellationTokenSource? connector = null;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var entry) || entry.Row.Mode == SiteMode.Outbound)
            {
                return;
            }

            entry.Row.Mode = SiteMode.Outbound;
            connector = entry.Connector;
            entry.Connector = null;
        }

        if (connector is not null)
        {
            await connector.CancelAsync().ConfigureAwait(false);
        }

        await using var db = await _db.CreateDbContextAsync(_stop.Token).ConfigureAwait(false);
        await db.Sites.Where(s => s.Id == siteId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Mode, SiteMode.Outbound), _stop.Token).ConfigureAwait(false);
    }

    // ---------- inbound: the server reaches the node ----------

    /// <summary>Reaches an inbound site's node until the session runs; after a failure again in 1, 2, 4 ... 60 s.</summary>
    private void StartConnector(Guid siteId)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancellationTokenSource? old;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var entry))
            {
                cancel.Dispose();
                return;
            }

            old = entry.Connector;
            entry.Connector = cancel;
        }

        old?.Cancel();
        _ = Task.Run(() => ConnectLoopAsync(siteId, cancel.Token));
    }

    private async Task ConnectLoopAsync(Guid siteId, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            string? address;
            int port;
            string pinned;
            lock (_lock)
            {
                if (!_sites.TryGetValue(siteId, out var entry) || entry.Row.Mode != SiteMode.Inbound || entry.Row.NotBound)
                {
                    return;
                }

                if (entry.Session is { IsEnded: false })
                {
                    return;
                }

                address = entry.Row.Address;
                port = entry.Row.Port ?? 5081;
                pinned = entry.Row.NodeKeyFingerprint;
            }

            string? problem = null;
            if (!string.IsNullOrEmpty(address))
            {
                try
                {
                    var link = await ConnectToNodeAsync(address, port, ct).ConfigureAwait(false);
                    var hello = await HelloAsync(link).ConfigureAwait(false);
                    if (hello is null)
                    {
                        await link.DisposeAsync().ConfigureAwait(false);
                        problem = SiteTexts.NodeNotAnswering;
                    }
                    else if (!string.Equals(link.NodeFingerprintHex, pinned, StringComparison.OrdinalIgnoreCase))
                    {
                        await link.DisposeAsync().ConfigureAwait(false);
                        if (hello.State != NodeState.Bound && !string.Equals(hello.NodeId, NodeIdOf(siteId), StringComparison.Ordinal))
                        {
                            // A new key and unbound: the node was reset.
                            await MarkNotBoundAsync(siteId).ConfigureAwait(false);
                            return;
                        }

                        problem = SiteTexts.OtherNode;
                    }
                    else if (hello.State != NodeState.Bound)
                    {
                        await link.DisposeAsync().ConfigureAwait(false);
                        await MarkNotBoundAsync(siteId).ConfigureAwait(false);
                        return;
                    }
                    else if (await AttachControlAsync(siteId, link, fresh: false).ConfigureAwait(false))
                    {
                        SetMessage(siteId, null);
                        return; // the session runs; it starts this loop again when it ends
                    }
                    else
                    {
                        await link.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is SocketException or IOException or System.Security.Authentication.AuthenticationException || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    problem = SiteTexts.NodeNotAnswering;
                }
            }

            FirstAttemptDone(siteId, problem);
            try
            {
                await Task.Delay(backoff, Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
        }
    }

    private string? NodeIdOf(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) ? entry.Row.NodeId : null;
        }
    }

    private void SetMessage(Guid siteId, string? message)
    {
        lock (_lock)
        {
            if (_sites.TryGetValue(siteId, out var entry))
            {
                entry.Message = message;
            }
        }
    }

    private void FirstAttemptDone(Guid siteId, string? problem)
    {
        bool changed;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var entry))
            {
                return;
            }

            entry.Message = problem;
            entry.FirstAttemptFailed = true;
            changed = Evaluate(entry, Time.GetUtcNow());
        }

        if (changed)
        {
            Raise([siteId]);
        }

        RaiseList();
    }

    /// <summary>A new worker link to an inbound node (pinned key).</summary>
    private async Task<NodeLink> DialWorkerAsync(Guid siteId, CancellationToken ct)
    {
        string address;
        int port;
        string pinned;
        lock (_lock)
        {
            var row = _sites[siteId].Row;
            address = row.Address ?? throw new IOException("The site has no address.");
            port = row.Port ?? 5081;
            pinned = row.NodeKeyFingerprint;
        }

        var link = await ConnectToNodeAsync(address, port, ct).ConfigureAwait(false);
        if (!string.Equals(link.NodeFingerprintHex, pinned, StringComparison.OrdinalIgnoreCase))
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw new IOException(SiteTexts.OtherNode);
        }

        return link;
    }

    /// <summary>TCP + TLS to a node that waits for the server (any node key: the caller checks it).</summary>
    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The site transport: the server connects to site nodes that wait for it, never to devices.")]
    internal async Task<NodeLink> ConnectToNodeAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        client.Client.DualMode = true;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(_options.CallTimeout);
                await client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false);
            }

            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(SiteTls.HandshakeTimeout);
                await tls.AuthenticateAsClientAsync(SiteTls.ClientOptions(_identity.Current, fingerprint => fingerprint is not null), handshake.Token).ConfigureAwait(false);
            }
            catch
            {
                await tls.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new NodeLink(tls, remote, openedByNode: false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---------- Search and Add manually ----------

    /// <summary>
    /// Nodes that wait for the server: at one address (Add manually; host or IP, optional port, default 5081), or by mDNS
    /// for <paramref name="duration"/>. Each answering node is listed as waiting (or greyed when bound elsewhere or closed).
    /// </summary>
    public async IAsyncEnumerable<Proto.Site> FindNodesAsync(string? address, TimeSpan duration, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            var target = ParseNodeAddress(address) ?? throw new ArgumentException("Enter an address such as 192.168.0.5 or node.example.com:5081.", nameof(address));
            yield return await FindAtAsync(target.Host, target.Port, ct).ConfigureAwait(false);
            yield break;
        }

        if (_mdns is null)
        {
            yield break;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = System.Threading.Channels.Channel.CreateUnbounded<Proto.Site>();
        var browse = Task.Run(async () =>
        {
            var probes = new List<Task>();
            try
            {
                await foreach (var instance in _mdns.BrowseAsync(new MdnsBrowseOptions { ServiceType = NodeService }, stop.Token).ConfigureAwait(false))
                {
                    var ip = instance.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? (instance.Addresses.Count > 0 ? instance.Addresses[0] : null);
                    if (ip is null || !seen.Add(ip + ":" + instance.Port))
                    {
                        continue;
                    }

                    var host = ip.ToString();
                    int port = instance.Port;
                    probes.Add(Task.Run(async () => found.Writer.TryWrite(await FindAtAsync(host, port, ct).ConfigureAwait(false)), CancellationToken.None));
                }
            }
            catch (OperationCanceledException)
            {
            }

            await Task.WhenAll(probes).ConfigureAwait(false);
            found.Writer.TryComplete();
        }, CancellationToken.None);
        await foreach (var site in found.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return site;
        }

        await browse.ConfigureAwait(false);
    }

    /// <summary>The mDNS service of nodes that wait for the server.</summary>
    public const string NodeService = "_oadm-site._tcp.local";

    private async Task<Proto.Site> FindAtAsync(string host, int port, CancellationToken ct)
    {
        var shown = port == 5081 ? host : host + ":" + port.ToString(CultureInfo.InvariantCulture);
        NodeLink link;
        try
        {
            link = await ConnectToNodeAsync(host, port, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException or System.Security.Authentication.AuthenticationException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new Proto.Site { Name = shown, NodeAddress = host, NodePort = port, State = Proto.SiteState.Offline, Message = SiteTexts.NodeNotAnswering, Mode = Proto.SiteMode.Inbound };
        }

        var hello = await HelloAsync(link).ConfigureAwait(false);
        if (hello is null)
        {
            await link.DisposeAsync().ConfigureAwait(false);
            return new Proto.Site { Name = shown, NodeAddress = host, NodePort = port, State = Proto.SiteState.Offline, Message = SiteTexts.NodeNotAnswering, Mode = Proto.SiteMode.Inbound };
        }

        var node = new WaitingNode(link, hello, SiteMode.Inbound, (host, port), Time.GetUtcNow());
        if (node.State == Proto.SiteState.Waiting && AddWaiting(link, hello, SiteMode.Inbound, (host, port)))
        {
            lock (_lock)
            {
                return (_waiting.GetValueOrDefault(hello.NodeId) ?? node).ToProto();
            }
        }

        await link.DisposeAsync().ConfigureAwait(false);
        return node.ToProto();
    }

    /// <summary>"192.168.0.5", "node.example.com:5081", "[fd00::5]:5081"; null when it is not an address.</summary>
    public static (string Host, int Port)? ParseNodeAddress(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length is 0 or > 260 || value.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
        {
            return null;
        }

        var port = 5081;
        var host = value;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            host = value[1..end];
            var rest = value[(end + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port)))
            {
                return null;
            }
        }
        else if (value.Count(c => c == ':') == 1)
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            host = value[..colon];
            if (!int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                return null;
            }
        }

        if (port is < 1 or > 65535 || host.Length == 0)
        {
            return null;
        }

        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns ? (host, port) : null;
    }

    /// <summary>A node that waits to be bound (outbound: its connection; inbound: the server's), or found greyed.</summary>
    private sealed class WaitingNode(NodeLink link, HelloReply hello, SiteMode mode, (string Host, int Port)? address, DateTimeOffset since)
    {
        public NodeLink Link { get; } = link;

        public HelloReply Hello { get; } = hello;

        public SiteMode Mode { get; } = mode;

        public (string Host, int Port)? Address { get; } = address;

        public DateTimeOffset Since { get; } = since;

        public string NodeId => Hello.NodeId;

        public string Name => string.IsNullOrWhiteSpace(Hello.HostName) ? Link.RemoteHost : Hello.HostName;

        /// <summary>A bound site with the same node id or key.</summary>
        public string? CopyOf { get; set; }

        public Proto.SiteState State => Hello.State switch
        {
            NodeState.UnboundOpen => Proto.SiteState.Waiting,
            NodeState.UnboundClosed => Proto.SiteState.BindingClosed,
            _ => Proto.SiteState.BoundElsewhere,
        };

        public Proto.Site ToProto() => new()
        {
            Name = Name,
            State = State,
            Mode = Mode == SiteMode.Inbound ? Proto.SiteMode.Inbound : Proto.SiteMode.Outbound,
            NodeId = Hello.NodeId,
            NodeAddress = Address?.Host ?? Link.RemoteHost,
            NodePort = Address?.Port ?? 0,
            Version = Hello.Version,
            NodeFingerprint = Link.NodeFingerprintHex,
            LastSeen = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(Since),
            Message = State switch
            {
                Proto.SiteState.Waiting when CopyOf is not null => SiteTexts.CopyOf(CopyOf),
                Proto.SiteState.Waiting => SiteTexts.WaitingToBeBound,
                Proto.SiteState.BindingClosed => SiteTexts.BindingClosed,
                _ when !string.IsNullOrEmpty(Hello.BoundServerName) => SiteTexts.RemovedSite,
                _ => SiteTexts.BoundElsewhere,
            },
        };
    }
}
