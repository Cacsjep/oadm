using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Sites;

/// <summary>The links of <see cref="SiteManager"/>: connections nodes open and the nodes waiting to be bound.</summary>
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
            // Links that did not answer Hello yet are capped and get a short deadline: a flood of connections that pass
            // TLS and then say nothing cannot pile up.
            HelloReply? hello;
            if (Interlocked.Increment(ref _pendingLinks) > _options.MaxPendingLinks)
            {
                Interlocked.Decrement(ref _pendingLinks);
                LogTooManyConnections(link.RemoteHost);
                await link.DisposeAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                hello = await HelloAsync(link, _options.HelloTimeout).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingLinks);
            }

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
                link.Trust(); // a bound site's key: its answers may be large
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

                if (!await AttachControlAsync(id, link, fresh: false).ConfigureAwait(false))
                {
                    await link.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }

            // Not one of the sites (or a copy of one that is not bound): waits to be bound, within the limits.
            if (!UnknownLinks.TryAcquire(link.Remote?.Address))
            {
                LogTooManyConnections(link.RemoteHost);
                await link.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (!AddWaiting(link, hello))
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

    // Hello on a new link, within the deadline; a Hello that breaks HelloRules is refused whole (its texts are stored,
    // logged, audited and shown, so nothing unchecked from a stranger gets that far).
    private async Task<HelloReply?> HelloAsync(NodeLink link, TimeSpan? timeout = null)
    {
        try
        {
            var hello = await link.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow + (timeout ?? _options.CallTimeout), cancellationToken: _stop.Token).ConfigureAwait(false);
            if (HelloRules.Problem(hello) is { } problem)
            {
                LogBadHello(link.RemoteHost, problem);
                return null;
            }

            link.Hello = hello;
            return hello;
        }
        catch (RpcException e)
        {
            LogLinkFailed(link.RemoteHost, e.Status.Detail);
            return null;
        }
    }

    private int _pendingLinks;
    private AddressRateLimiter? _unknownLinks;

    // New links of keys the server does not know, per address (IPv6 per /64) and minute; old entries are dropped by age.
    private AddressRateLimiter UnknownLinks =>
        LazyInitializer.EnsureInitialized(ref _unknownLinks, () => new AddressRateLimiter(_options.NewConnectionsPerMinute, 0, TimeSpan.FromMinutes(1), Time));

    /// <summary>
    /// A node waiting to be bound, kept by its key fingerprint: a stranger that reports another node's id
    /// gets an entry of its own and never takes the other's place. A newer link with the same key replaces the older one.
    /// </summary>
    private bool AddWaiting(NodeLink link, HelloReply hello)
    {
        if (!HelloRules.IsNodeId(hello.NodeId) || link.NodeFingerprintHex.Length == 0)
        {
            return false;
        }

        var key = link.NodeFingerprintHex;
        WaitingNode? replaced;
        var node = new WaitingNode(link, hello, Time.GetUtcNow(), Time.GetTimestamp());
        lock (_lock)
        {
            var same = _sites.Values.FirstOrDefault(e =>
                string.Equals(e.Row.NodeId, hello.NodeId, StringComparison.Ordinal)
                || string.Equals(e.Row.NodeKeyFingerprint, key, StringComparison.OrdinalIgnoreCase));
            if (same is not null && IsRebind(same, node))
            {
                node.RebindOf = same.Row.Name;
            }
            else
            {
                node.CopyOf = same?.Row.Name;
            }

            _waiting.TryGetValue(key, out replaced);
            if (replaced is null && _waiting.Count >= _options.MaxWaiting)
            {
                LogTooManyWaiting(link.RemoteHost);
                return false;
            }

            _waiting[key] = node;
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
                removed = _waiting.TryGetValue(key, out var current) && current == node && _waiting.Remove(key);
            }

            if (removed)
            {
                LogWaitingGone(node.Name, link.RemoteHost);
                RaiseList();
            }
        }, TaskScheduler.Default);
        LogWaiting(node.Name, link.RemoteHost);
        RaiseList();
        return true;
    }

    // The node with a site's key and id offers to pair with this server: its administrator accepted this server's new
    // identity (the site identity was lost, e.g. with a replaced master key). Bind with the code binds the site again.
    private static bool IsRebind(SiteEntry site, WaitingNode node) =>
        node.Hello is { State: NodeState.UnboundOpen, NewIdentityAccepted: true }
        && string.Equals(site.Row.NodeKeyFingerprint, node.Key, StringComparison.OrdinalIgnoreCase)
        && string.Equals(site.Row.NodeId, node.NodeId, StringComparison.Ordinal)
        && site.Session is not { IsEnded: false };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused the site node connection from {Remote}: {Problem}")]
    private partial void LogBadHello(string remote, string problem);

    /// <summary>A node that waits to be bound: the connection it opened.</summary>
    private sealed class WaitingNode(NodeLink link, HelloReply hello, DateTimeOffset since, long sinceTimestamp)
    {
        public NodeLink Link { get; } = link;

        public HelloReply Hello { get; } = hello;

        /// <summary>When the node connected (wall clock, shown).</summary>
        public DateTimeOffset Since { get; } = since;

        /// <summary>When the node connected (<see cref="TimeProvider.GetTimestamp"/>: the 24 hours never follow the wall clock).</summary>
        public long SinceTimestamp { get; } = sinceTimestamp;

        public string NodeId => Hello.NodeId;

        /// <summary>The key of the waiting list: the fingerprint of the node's key on this link.</summary>
        public string Key => Link.NodeFingerprintHex;

        public string Name => string.IsNullOrWhiteSpace(Hello.HostName) ? Link.RemoteHost : Hello.HostName;

        /// <summary>A bound site with the same node id or key.</summary>
        public string? CopyOf { get; set; }

        /// <summary>The site this node belongs to, offering to bind again with this server's new identity.</summary>
        public string? RebindOf { get; set; }

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
            NodeId = Hello.NodeId,
            NodeAddress = Link.RemoteHost,
            Version = Hello.Version,
            NodeFingerprint = Link.NodeFingerprintHex,
            LastSeen = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(Since),
            Message = State switch
            {
                Proto.SiteState.Waiting when RebindOf is not null => SiteTexts.BindAgain(RebindOf),
                Proto.SiteState.Waiting when CopyOf is not null => SiteTexts.CopyOf(CopyOf),
                Proto.SiteState.Waiting => SiteTexts.WaitingToBeBound,
                Proto.SiteState.BindingClosed => SiteTexts.BindingClosed,
                _ when !string.IsNullOrEmpty(Hello.BoundServerName) => SiteTexts.RemovedSite,
                _ => SiteTexts.BoundElsewhere,
            },
        };
    }
}
