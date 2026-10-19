using Google.Protobuf.WellKnownTypes;

using Microsoft.EntityFrameworkCore;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Auth;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Persistence;
using Oadm.Core.Settings;
using Oadm.Core.Sites;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Sites;

/// <summary>
/// The server side of the sites (docs/sites.md, Server): the bound sites and their node sessions, the nodes waiting to be
/// bound, the site listener (Accept site nodes), and the one way the server opens a connection through a node
/// (<see cref="ISiteTransport"/>). Online and offline live in memory only, with the waiting times of
/// <see cref="SiteOptions"/>: offline after 30 s without an answer, online again after 60 s stable, and after a server
/// start "starting" until the first connection settles. Everything a page shows comes from <see cref="SnapshotAsync"/>.
/// </summary>
public sealed partial class SiteManager : ISiteTransport, ISiteStates, ISiteLargeTransfers, ISiteNetwork, IAsyncDisposable
{
    private readonly IDbContextFactory<OadmDbContext> _db;
    private readonly ServerSettingsStore _settings;
    private readonly SiteIdentityStore _identity;
    private readonly AuditLog _audit;
    private readonly IMdnsBrowser? _mdns;
    private readonly SiteOptions _options;
    private readonly ILogger<SiteManager> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, SiteEntry> _sites = [];
    private readonly Dictionary<string, WaitingNode> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _newConnections = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _acceptLock = new(1, 1);
    private SiteListener? _listener;
    private ITimer? _tick;
    private bool _accept;
    private string _serverName = Environment.MachineName;

    public SiteManager(
        IDbContextFactory<OadmDbContext> db,
        ServerSettingsStore settings,
        SiteIdentityStore identity,
        AuditLog audit,
        SiteOptions options,
        TimeProvider time,
        ILogger<SiteManager> logger,
        IMdnsBrowser? mdns = null)
    {
        _db = db;
        _settings = settings;
        _identity = identity;
        _audit = audit;
        _options = options;
        Time = time;
        _logger = logger;
        _mdns = mdns;
    }

    public TimeProvider Time { get; }

    /// <summary>Node sessions started so far (tests: one session per identity).</summary>
    internal int SessionsStarted => Volatile.Read(ref _sessionsStarted);

    /// <summary>Second control connections refused while the current one answers (tests).</summary>
    internal int SessionsRefused => Volatile.Read(ref _sessionsRefused);

    private int _sessionsStarted;
    private int _disposed;
    private int _sessionsRefused;

    /// <summary>A site was added, renamed, removed, or went online or offline (after the waiting times).</summary>
    public event EventHandler<SiteStatesChangedEventArgs>? Changed;

    /// <summary>Anything the Sites page shows changed (state, counts, waiting nodes, Accept).</summary>
    public event EventHandler? ListChanged;

    /// <summary>The site listener's port while Accept site nodes is on (0 otherwise).</summary>
    public int ListenPort => _listener?.Port ?? 0;

    public bool Accept
    {
        get
        {
            lock (_lock)
            {
                return _accept;
            }
        }
    }

    /// <summary>Loads the identity and the sites (each "starting"), opens the listener when Accept is on, starts reaching inbound sites.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _serverName = (await _settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName;
        await _identity.LoadOrCreateAsync(_serverName, ct).ConfigureAwait(false);
        List<Site> rows;
        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            rows = await db.Sites.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        }

        var now = Time.GetUtcNow();
        lock (_lock)
        {
            foreach (var row in rows)
            {
                _sites[row.Id] = new SiteEntry(row, _options.MaxLargeTransfers)
                {
                    State = row.NotBound ? SiteState.Offline : SiteState.Starting,
                    StartingUntil = now + _options.StartWindow,
                    LastAnswer = now,
                };
            }
        }

        foreach (var row in rows.Where(r => r.Mode == SiteMode.Inbound && !r.NotBound))
        {
            StartConnector(row.Id);
        }

        if (await _settings.GetAsync<bool>(SiteSettings.Accept, ct).ConfigureAwait(false))
        {
            await OpenListenerAsync(ct).ConfigureAwait(false);
        }

        _tick = Time.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        if (rows.Count > 0)
        {
            Raise(rows.Select(r => r.Id).ToList());
        }
    }

    // ---------- ISiteStates ----------

    public string? NameOf(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) ? entry.Row.Name : null;
        }
    }

    public bool IsOnline(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) && entry.State == SiteState.Online && !entry.Row.NotBound;
        }
    }

    public bool IsStarting(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) && entry.State == SiteState.Starting;
        }
    }

    /// <summary>True while the site's node is connected and answering (before the waiting times; tests).</summary>
    internal bool HasSession(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) && entry.Session is { IsEnded: false, Ready: true };
        }
    }

    public TimeSpan RoundTrip(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) ? entry.RoundTrip : TimeSpan.Zero;
        }
    }

    // ---------- ISiteTransport ----------

    public async ValueTask<Stream> OpenStreamAsync(Guid siteId, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct)
    {
        SiteSession? session;
        string name;
        bool request = false;
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var entry))
            {
                throw SiteException.For(siteId, siteId.ToString(), SiteProblem.Offline);
            }

            name = entry.Row.Name;
            if (entry.Row.NotBound)
            {
                throw SiteException.For(siteId, name, SiteProblem.NotBound);
            }

            session = entry.Session;
            if (entry.State != SiteState.Online || session is null || session.IsEnded || !session.Ready)
            {
                throw SiteException.For(siteId, name, SiteProblem.Offline);
            }

            // The node's allowed ports come with Watch: a port it does not allow is not dialed, and asked for once.
            if (!Allows(entry.Status, port))
            {
                request = entry.Requested.Add(port);
                if (!request)
                {
                    throw SiteException.For(siteId, name, SiteProblem.PortNotAllowed, port);
                }
            }
        }

        if (request)
        {
            LogPortRequested(name, port, host);
            if (!await session.RequestPortAsync(port, "device " + host, ct).ConfigureAwait(false))
            {
                // Not delivered (the caller gave up, the node did not answer): asked again with the next dial.
                lock (_lock)
                {
                    _sites.GetValueOrDefault(siteId)?.Requested.Remove(port);
                }
            }

            RaiseList();
            throw SiteException.For(siteId, name, SiteProblem.PortNotAllowed, port);
        }

        return await session.DialAsync(name, host, port, kind, timeout, ct).ConfigureAwait(false);
    }

    private static bool Allows(NodeStatus? status, int port) =>
        status is not null && status.Allowed.Any(r => r.Direction == PortDirection.Connect && r.Protocol == Protocol.Tcp && r.Port == port);

    // ---------- ISiteLargeTransfers ----------

    public async ValueTask<IDisposable> EnterLargeTransferAsync(Guid siteId, CancellationToken ct)
    {
        SiteEntry? entry;
        lock (_lock)
        {
            entry = _sites.GetValueOrDefault(siteId);
        }

        if (entry is null)
        {
            return NoSlot.Instance;
        }

        await entry.Large.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref entry.LargeRunning);
        RaiseList();
        return new Slot(this, entry);
    }

    /// <summary>Large transfers waiting for a slot of the site now (tests: the third one waits).</summary>
    public int LargeTransfersRunning(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry) ? Volatile.Read(ref entry.LargeRunning) : 0;
        }
    }

    private sealed class Slot(SiteManager manager, SiteEntry entry) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref entry.LargeRunning);
                entry.Large.Release();
                manager.RaiseList();
            }
        }
    }

    private sealed class NoSlot : IDisposable
    {
        public static readonly NoSlot Instance = new();

        public void Dispose()
        {
        }
    }

    // ---------- sessions ----------

    /// <summary>A Watch answer of the node: allowed ports, counts, version.</summary>
    internal void OnStatus(SiteSession session, NodeStatus status)
    {
        var now = Time.GetUtcNow();
        bool changed;
        lock (_lock)
        {
            if (!_sites.TryGetValue(session.SiteId, out var entry) || entry.Session != session)
            {
                return;
            }

            entry.Status = status;
            entry.Networks = SiteNetworks.From(status);
            entry.LastAnswer = now;
            entry.LastSeen = now;
            entry.ConnectedSince ??= now;
            foreach (var port in entry.Requested.Where(p => Allows(status, p)).ToList())
            {
                entry.Requested.Remove(port);
            }

            changed = Evaluate(entry, now);
        }

        if (changed)
        {
            Raise([session.SiteId]);
        }

        RaiseList();
    }

    internal void OnHeartbeat(SiteSession session, HelloReply hello, TimeSpan roundTrip)
    {
        var now = Time.GetUtcNow();
        Site? notBound = null;
        lock (_lock)
        {
            if (!_sites.TryGetValue(session.SiteId, out var entry) || entry.Session != session)
            {
                return;
            }

            entry.LastAnswer = now;
            entry.LastSeen = now;
            entry.RoundTrip = roundTrip;
            entry.Version = hello.Version;
            if (hello.State != NodeState.Bound && !entry.Row.NotBound)
            {
                entry.Row.NotBound = true;
                notBound = entry.Row;
            }
        }

        if (notBound is not null)
        {
            Observe(MarkNotBoundAsync(notBound.Id), "mark the site not bound");
        }

        RaiseList();
    }

    /// <summary>
    /// True when the node of <paramref name="session"/> has not answered for longer than the offline time, counted from
    /// its last answer or the session's start (a reconnecting session is never judged by the old session's answers).
    /// </summary>
    internal bool AnsweredLongAgo(SiteSession session)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(session.SiteId, out var entry))
            {
                return true;
            }

            var last = entry.LastAnswer > session.Started ? entry.LastAnswer : session.Started;
            return Time.GetUtcNow() - last >= _options.OfflineAfter;
        }
    }

    /// <summary>
    /// The node of the site answers now (<see cref="ISiteStates.LinkAnswersAsync"/>): false without a running session;
    /// otherwise one Hello on the control link, shared by everyone who asks while it runs.
    /// </summary>
    public async ValueTask<bool> LinkAnswersAsync(Guid siteId, CancellationToken ct)
    {
        Task<bool> check;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1 || !_sites.TryGetValue(siteId, out var entry) || entry.Session is not { IsEnded: false, Ready: true } session)
            {
                return false;
            }

            if (entry.LinkCheck is not { IsCompleted: false } running || entry.LinkCheckSession != session)
            {
                running = session.AnswersAsync(_stop.Token);
                entry.LinkCheck = running;
                entry.LinkCheckSession = session;
            }

            check = running;
        }

        return await check.WaitAsync(ct).ConfigureAwait(false);
    }

    internal void SessionChanged(Guid siteId) => RaiseList();

    /// <summary>Ends the site's session as a dropped link would (tests).</summary>
    internal async Task DropSessionAsync(Guid siteId)
    {
        SiteSession? session;
        lock (_lock)
        {
            session = _sites.GetValueOrDefault(siteId)?.Session;
        }

        if (session is not null)
        {
            await session.EndAsync().ConfigureAwait(false);
        }
    }

    internal void SessionEnded(SiteSession session)
    {
        bool changed = false;
        Site? row = null;
        lock (_lock)
        {
            if (_sites.TryGetValue(session.SiteId, out var entry) && entry.Session == session)
            {
                entry.Session = null;
                entry.ConnectedSince = null;
                row = entry.Row;
                changed = Evaluate(entry, Time.GetUtcNow());
            }
        }

        if (row is not null)
        {
            LogSessionEnded(row.Name);
            if (row.Mode == SiteMode.Inbound && !row.NotBound && !_stop.IsCancellationRequested)
            {
                StartConnector(row.Id);
            }
        }

        if (changed)
        {
            Raise([session.SiteId]);
        }

        RaiseList();
    }

    /// <summary>
    /// Makes <paramref name="control"/> the control link of the site. One session per node identity: while the current
    /// session still answers, a second control link with the same key is refused (a copied node); otherwise it replaces it.
    /// </summary>
    private async Task<bool> AttachControlAsync(Guid siteId, NodeLink control, bool fresh)
    {
        SiteEntry? attaching;
        lock (_lock)
        {
            attaching = _sites.GetValueOrDefault(siteId);
        }

        if (attaching is null)
        {
            return false;
        }

        // One attach at a time per site: two control links attaching at once would both start a session, and the loser
        // would keep running beside the winner. The second one sees the first one's session and is judged against it.
        await attaching.Attaching.WaitAsync(_stop.Token).ConfigureAwait(false);
        Site row;
        SiteSession session;
        try
        {
            SiteSession? old;
            lock (_lock)
            {
                if (!_sites.TryGetValue(siteId, out var entry) || entry != attaching)
                {
                    return false;
                }

                old = entry.Session;
            }

            if (old is not null && !old.IsEnded && !fresh)
            {
                try
                {
                    await old.Control.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow + TimeSpan.FromSeconds(5), cancellationToken: _stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _sessionsRefused);
                    LogSecondSession(NameOf(siteId) ?? siteId.ToString(), control.RemoteHost, old.Control.RemoteHost);
                    return false;
                }
                catch (Exception e) when (e is Grpc.Core.RpcException or ObjectDisposedException or InvalidOperationException)
                {
                    // The old link is dead: the new one takes over.
                }
            }

            session = new SiteSession(this, siteId, control, _options, control.OpenedByNode ? null : ct => DialWorkerAsync(siteId, ct));
            var now = Time.GetUtcNow();
            lock (_lock)
            {
                if (!_sites.TryGetValue(siteId, out var entry) || entry != attaching)
                {
                    _ = session.DisposeAsync().AsTask();
                    return false;
                }

                entry.Session = session;
                entry.ConnectedSince = null;
                entry.Version = control.Hello?.Version ?? entry.Version;
                entry.Requested.Clear(); // a new connection asks again for the ports the node has not allowed
                if (fresh)
                {
                    entry.State = SiteState.Online;
                    entry.LastAnswer = now;
                }

                row = entry.Row;
            }

            if (old is not null)
            {
                await old.EndAsync().ConfigureAwait(false);
            }

            session.Start();
            Interlocked.Increment(ref _sessionsStarted);
        }
        finally
        {
            attaching.Attaching.Release();
        }

        await RecordSeenAsync(row.Id, control).ConfigureAwait(false);
        LogSessionStarted(row.Name, control.RemoteHost);
        RaiseList();
        return true;
    }

    /// <summary>Logs a background task's failure instead of leaving it unobserved.</summary>
    private void Observe(Task task, string what) =>
        _ = task.ContinueWith(
            t => LogBackgroundFailed(what, t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// The inbound connector of a site (<see cref="ConnectLoopAsync"/>) under a guard: an unexpected error is logged and the
    /// loop starts again after 1, 2, 4 ... up to <see cref="SiteOptions.MaxBackoff"/>, so an inbound site is never left
    /// without a connector. Ends when <paramref name="ct"/> ends or the loop ends by itself (session running, site gone).
    /// </summary>
    private async Task RunConnectorAsync(Guid siteId, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectLoopAsync(siteId, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The connector never dies silently: logged, then tried again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBackgroundFailed("reach the site node of " + (NameOf(siteId) ?? siteId.ToString()), ex);
            }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sites: could not {What}")]
    private partial void LogBackgroundFailed(string what, Exception ex);

    // ---------- the waiting times ----------

    private void Tick()
    {
        var now = Time.GetUtcNow();
        var changed = new List<Guid>();
        var expired = new List<WaitingNode>();
        var seen = new List<(Guid Id, DateTime At)>();
        lock (_lock)
        {
            foreach (var entry in _sites.Values)
            {
                if (Evaluate(entry, now))
                {
                    changed.Add(entry.Row.Id);
                }

                // LastSeenUtc is written at most once a minute.
                if (entry.LastSeen is { } last && (entry.Row.LastSeenUtc is not { } written || last.UtcDateTime - written >= TimeSpan.FromMinutes(1)))
                {
                    entry.Row.LastSeenUtc = last.UtcDateTime;
                    seen.Add((entry.Row.Id, last.UtcDateTime));
                }
            }

            foreach (var waiting in _waiting.Values.Where(w => now - w.Since >= _options.WaitingMaxAge).ToList())
            {
                _waiting.Remove(waiting.NodeId);
                expired.Add(waiting);
            }
        }

        if (seen.Count > 0)
        {
            _ = WriteLastSeenAsync(seen);
        }

        foreach (var waiting in expired)
        {
            LogWaitingDropped(waiting.Name);
            _ = waiting.Link.DisposeAsync().AsTask();
        }

        if (changed.Count > 0)
        {
            Raise(changed);
        }

        if (changed.Count > 0 || expired.Count > 0)
        {
            RaiseList();
        }
    }

    /// <summary>Moves the site between starting, online and offline (under the lock); true when it changed. Audits the edges.</summary>
    private bool Evaluate(SiteEntry entry, DateTimeOffset now)
    {
        var connected = entry.Session is { IsEnded: false, Ready: true };
        var before = entry.State;
        switch (entry.State)
        {
            case SiteState.Starting:
                if (connected)
                {
                    entry.State = SiteState.Online; // after a start no waiting: nothing changed meanwhile
                }
                else if (now >= entry.StartingUntil || entry.FirstAttemptFailed || entry.Row.NotBound)
                {
                    entry.State = SiteState.Offline;
                }

                break;
            case SiteState.Online:
                if (entry.Row.NotBound || (!connected && now - entry.LastAnswer >= _options.OfflineAfter))
                {
                    entry.State = SiteState.Offline;
                }

                break;
            case SiteState.Offline:
                if (connected && !entry.Row.NotBound && entry.ConnectedSince is { } since && now - since >= _options.OnlineAfter)
                {
                    entry.State = SiteState.Online;
                }

                break;
        }

        if (entry.State == before)
        {
            return false;
        }

        if (before != SiteState.Starting || entry.State == SiteState.Offline)
        {
            var online = entry.State == SiteState.Online;
            var name = entry.Row.Name;
            _ = _audit.WriteAsync("system", string.Empty, online ? SiteAudit.Online : SiteAudit.Offline, name, null);
            if (online)
            {
                LogOnline(name);
            }
            else
            {
                LogOffline(name);
            }
        }

        return true;
    }

    // ---------- listener ----------

    /// <summary>Accept site nodes: opens or closes the site listener without a restart. Off closes every node connection (inbound sites stay).</summary>
    public async Task SetAcceptAsync(bool accept, CancellationToken ct)
    {
        await _acceptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _settings.SetAsync(SiteSettings.Accept, accept, ct).ConfigureAwait(false);
            if (accept)
            {
                await OpenListenerAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await CloseListenerAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _acceptLock.Release();
        }

        RaiseList();
    }

    private async Task OpenListenerAsync(CancellationToken ct)
    {
        if (_listener is not null)
        {
            return;
        }

        var url = _options.ListenUrl ?? await _settings.GetAsync<string>(SiteSettings.ListenUrl, ct).ConfigureAwait(false) ?? SiteSettings.DefaultListenUrl;
        var listener = new SiteListener(url, () => _identity.Current, _options.MaxHandshakes, link => _ = AcceptLinkAsync(link), _logger);
        listener.Start();
        _listener = listener;
        lock (_lock)
        {
            _accept = true;
        }

        LogListening(url, listener.Port);
    }

    private async Task CloseListenerAsync()
    {
        var listener = _listener;
        _listener = null;
        lock (_lock)
        {
            _accept = false;
        }

        if (listener is not null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        List<WaitingNode> waiting;
        List<SiteSession> sessions;
        lock (_lock)
        {
            waiting = [.. _waiting.Values.Where(w => w.Mode == SiteMode.Outbound)];
            foreach (var node in waiting)
            {
                _waiting.Remove(node.NodeId);
            }

            sessions = [.. _sites.Values.Select(e => e.Session).OfType<SiteSession>().Where(s => s.Control.OpenedByNode)];
        }

        foreach (var node in waiting)
        {
            await node.Link.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var session in sessions)
        {
            await session.EndAsync().ConfigureAwait(false);
        }

        LogListenerClosed();
    }

    // ---------- the list ----------

    /// <summary>The Sites page: bound sites by name, then the waiting nodes.</summary>
    public async Task<Proto.SiteList> SnapshotAsync(CancellationToken ct)
    {
        Dictionary<Guid, int> counts;
        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            counts = await db.Devices.AsNoTracking().Where(d => d.SiteId != null).GroupBy(d => d.SiteId!.Value)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
        }

        var list = new Proto.SiteList { Accept = Accept, ListenAddress = ListenAddress() };
        lock (_lock)
        {
            foreach (var entry in _sites.Values.OrderBy(e => e.Row.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                list.Sites.Add(ToProto(entry, counts.GetValueOrDefault(entry.Row.Id)));
            }

            foreach (var waiting in _waiting.Values.OrderBy(w => w.Since))
            {
                list.Sites.Add(waiting.ToProto());
            }
        }

        return list;
    }

    private Proto.Site ToProto(SiteEntry entry, int devices)
    {
        var row = entry.Row;
        var status = entry.Status;
        var site = new Proto.Site
        {
            Id = row.Id.ToString(),
            Name = row.Name,
            State = row.NotBound ? Proto.SiteState.NotBound : entry.State == SiteState.Online ? Proto.SiteState.Online : Proto.SiteState.Offline,
            Mode = row.Mode == SiteMode.Inbound ? Proto.SiteMode.Inbound : Proto.SiteMode.Outbound,
            NodeId = row.NodeId,
            NodeAddress = row.Address ?? string.Empty,
            NodePort = row.Port ?? 0,
            Version = entry.Version ?? row.Version ?? string.Empty,
            DeviceCount = devices,
            RoundTripMs = (int)Math.Ceiling(entry.RoundTrip.TotalMilliseconds),
            ShortConnections = status?.ShortConnections ?? 0,
            PermanentConnections = status?.PermanentConnections ?? 0,
            PortsWaiting = status?.Requested.Count ?? entry.Requested.Count,
            LargeTransfers = Volatile.Read(ref entry.LargeRunning),
            Bound = Timestamp.FromDateTime(DateTime.SpecifyKind(row.BoundUtc, DateTimeKind.Utc)),
            NodeFingerprint = row.NodeKeyFingerprint,
            Message = row.NotBound
                ? SiteTexts.NotBound
                : entry.State == SiteState.Starting ? SiteTexts.Connecting : entry.Message ?? string.Empty,
        };
        var lastSeen = entry.State == SiteState.Online ? Time.GetUtcNow() : entry.LastSeen ?? (row.LastSeenUtc is { } seen ? new DateTimeOffset(DateTime.SpecifyKind(seen, DateTimeKind.Utc)) : null);
        if (lastSeen is { } at)
        {
            site.LastSeen = Timestamp.FromDateTimeOffset(at);
        }

        return site;
    }

    /// <summary>What to type in the node app: the first address of this server's network cards and the site port.</summary>
    private string ListenAddress()
    {
        var port = _listener?.Port ?? 5081;
        var address = LocalAddresses.Local().FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return address is null ? "this server's address, port " + port : address + ":" + port;
    }

    // ---------- events ----------

    private void Raise(IReadOnlyCollection<Guid> siteIds)
    {
        Changed?.Invoke(this, new SiteStatesChangedEventArgs(siteIds));
        RaiseList();
    }

    internal void RaiseList() => ListChanged?.Invoke(this, EventArgs.Empty);

    private async Task RecordSeenAsync(Guid siteId, NodeLink link)
    {
        try
        {
            await using var db = await _db.CreateDbContextAsync(_stop.Token).ConfigureAwait(false);
            var now = Time.GetUtcNow().UtcDateTime;
            var version = link.Hello?.Version;
            if (link.OpenedByNode)
            {
                await db.Sites.Where(s => s.Id == siteId).ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.LastSeenUtc, now)
                    .SetProperty(s => s.Version, version)
                    .SetProperty(s => s.Address, link.RemoteHost), _stop.Token).ConfigureAwait(false);
            }
            else
            {
                await db.Sites.Where(s => s.Id == siteId).ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.LastSeenUtc, now)
                    .SetProperty(s => s.Version, version), _stop.Token).ConfigureAwait(false);
            }

            lock (_lock)
            {
                if (_sites.TryGetValue(siteId, out var entry))
                {
                    entry.Row.LastSeenUtc = now;
                    entry.Row.Version = version;
                    if (link.OpenedByNode)
                    {
                        entry.Row.Address = link.RemoteHost;
                    }
                }
            }
        }
#pragma warning disable CA1031 // The last seen time is information only.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogWriteFailed(ex);
        }
    }

    private async Task WriteLastSeenAsync(List<(Guid Id, DateTime At)> seen)
    {
        try
        {
            await using var db = await _db.CreateDbContextAsync(_stop.Token).ConfigureAwait(false);
            foreach (var (id, at) in seen)
            {
                await db.Sites.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenUtc, at), _stop.Token).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The last seen time is information only.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogWriteFailed(ex);
        }
    }

    /// <summary>
    /// The node was reset: the site is not bound (stored, and in memory even when the write fails) and its session ends.
    /// Never throws (it also runs unobserved from the heartbeat); a failed write is logged.
    /// </summary>
    private async Task MarkNotBoundAsync(Guid siteId)
    {
        try
        {
            await using var db = await _db.CreateDbContextAsync(_stop.Token).ConfigureAwait(false);
            await db.Sites.Where(s => s.Id == siteId).ExecuteUpdateAsync(u => u.SetProperty(s => s.NotBound, true), _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return; // the server stops
        }
#pragma warning disable CA1031 // A failed write is logged; the site is not bound in memory all the same.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteFailed(ex);
        }

        SiteSession? session;
        lock (_lock)
        {
            session = _sites.GetValueOrDefault(siteId)?.Session;
            if (_sites.TryGetValue(siteId, out var entry))
            {
                entry.Row.NotBound = true;
                Evaluate(entry, Time.GetUtcNow());
            }
        }

        LogNotBound(NameOf(siteId) ?? siteId.ToString());
        if (session is not null)
        {
            await session.EndAsync().ConfigureAwait(false);
        }

        Raise([siteId]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_tick is not null)
        {
            await _tick.DisposeAsync().ConfigureAwait(false);
        }

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        List<SiteSession> sessions;
        List<WaitingNode> waiting;
        List<CancellationTokenSource> connectors;
        lock (_lock)
        {
            sessions = [.. _sites.Values.Select(e => e.Session).OfType<SiteSession>()];
            connectors = [.. _sites.Values.Select(e => e.Connector).OfType<CancellationTokenSource>()];
            waiting = [.. _waiting.Values];
            _waiting.Clear();
        }

        foreach (var connector in connectors)
        {
            await connector.CancelAsync().ConfigureAwait(false);
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var node in waiting)
        {
            await node.Link.DisposeAsync().ConfigureAwait(false);
        }

        _acceptLock.Dispose();
        _stop.Dispose();
    }

    /// <summary>Starting after a server start; online and offline after the waiting times.</summary>
    internal enum SiteState
    {
        Starting,
        Online,
        Offline,
    }

    /// <summary>One bound site in memory (under the lock).</summary>
    internal sealed class SiteEntry(Site row, int largeTransfers)
    {
        public Site Row { get; } = row;

        public SiteState State { get; set; }

        public SiteSession? Session { get; set; }

        public DateTimeOffset LastAnswer { get; set; }

        public DateTimeOffset? ConnectedSince { get; set; }

        public DateTimeOffset StartingUntil { get; set; }

        public DateTimeOffset? LastSeen { get; set; }

        public bool FirstAttemptFailed { get; set; }

        public TimeSpan RoundTrip { get; set; }

        public string? Version { get; set; }

        public string? Message { get; set; }

        public NodeStatus? Status { get; set; }

        /// <summary>The networks the node reaches (from <see cref="Status"/>): reached cards, all cards, extra subnets.</summary>
        public SiteNetworks Networks { get; set; } = SiteNetworks.None;

        /// <summary>Ports asked for with RequestPorts and not allowed yet (asked once each).</summary>
        public HashSet<int> Requested { get; } = [];

        public SemaphoreSlim Large { get; } = new(largeTransfers, largeTransfers);

        public int LargeRunning;

        /// <summary>Inbound: the loop that reaches the node.</summary>
        public CancellationTokenSource? Connector { get; set; }

        /// <summary>One control link attaches at a time (two at once would both start a session).</summary>
        public SemaphoreSlim Attaching { get; } = new(1, 1);

        /// <summary>The Hello of <see cref="LinkAnswersAsync"/> while it runs, shared by everyone who asks.</summary>
        public Task<bool>? LinkCheck { get; set; }

        /// <summary>The session <see cref="LinkCheck"/> asked.</summary>
        public SiteSession? LinkCheckSession { get; set; }
    }
}

/// <summary>Audit actions of the sites (docs/sites.md, Roles, audit and secrets).</summary>
public static class SiteAudit
{
    public const string AcceptOn = "Turned on site nodes";
    public const string AcceptOff = "Turned off site nodes";
    public const string Bound = "Bound site";
    public const string WrongCode = "Wrong pairing code";
    public const string Renamed = "Renamed site";
    public const string AddressChanged = "Changed site address";
    public const string Replaced = "Replaced site";
    public const string Removed = "Removed site";
    public const string Offline = "Site offline";
    public const string Online = "Site online";
}
