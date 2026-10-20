using Google.Protobuf.WellKnownTypes;

using Microsoft.EntityFrameworkCore;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Settings;
using Oadm.Core.Sites;
using Oadm.Sites.Transport;

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
    private readonly SiteOptions _options;
    private readonly ILogger<SiteManager> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, SiteEntry> _sites = [];
    private readonly Dictionary<string, WaitingNode> _waiting = new(StringComparer.OrdinalIgnoreCase); // by node key fingerprint, never by the id a node reports
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
        ILogger<SiteManager> logger)
    {
        _db = db;
        _settings = settings;
        _identity = identity;
        _audit = audit;
        _options = options;
        Time = time;
        _logger = logger;
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

    /// <summary>Loads the identity and the sites (each "starting"), opens the listener when Accept is on.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _serverName = (await _settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName;
        await _identity.LoadOrCreateAsync(_serverName, ct).ConfigureAwait(false);
        List<Site> rows;
        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            rows = await db.Sites.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        }

        var now = Time.GetTimestamp();
        lock (_lock)
        {
            foreach (var row in rows)
            {
                _sites[row.Id] = new SiteEntry(row, _options.MaxLargeTransfers)
                {
                    State = row.NotBound ? SiteState.Offline : SiteState.Starting,
                    StartingSince = now,
                    LastAnswer = now,
                };
            }
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

    /// <summary>How long ago the site's node last answered (tests: advance a fake clock only as fast as the node answers).</summary>
    internal TimeSpan AnswerAge(Guid siteId)
    {
        lock (_lock)
        {
            return _sites.TryGetValue(siteId, out var entry)
                ? Time.GetElapsedTime(entry.Session is { } session ? Math.Max(entry.LastAnswer, session.Started) : entry.LastAnswer)
                : TimeSpan.MaxValue;
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

    /// <summary>A Watch answer of the node: allowed ports, counts, version; or the notice that the node was reset.</summary>
    internal void OnStatus(SiteSession session, NodeStatus status)
    {
        var now = Time.GetTimestamp();
        var changed = false;
        var reset = false;
        var edges = new List<SiteEdge>();
        lock (_lock)
        {
            if (!_sites.TryGetValue(session.SiteId, out var entry) || entry.Session != session)
            {
                return;
            }

            if (status.Reset)
            {
                // Reset in the node app: the site is not bound any more (the node returns with a new identity).
                entry.LastAnswer = now;
                reset = !entry.Row.NotBound;
                entry.Row.NotBound = true;
            }
            else
            {
                changed = Apply(entry, status, now, Time.GetUtcNow(), edges);
            }
        }

        WriteEdges(edges);
        if (reset)
        {
            Observe(MarkNotBoundAsync(session.SiteId), "mark the site not bound");
            return;
        }

        if (changed)
        {
            Raise([session.SiteId]);
        }

        RaiseList();
    }

    // Under the lock.
    private bool Apply(SiteEntry entry, NodeStatus status, long now, DateTimeOffset wall, List<SiteEdge> edges)
    {
        entry.Status = status;
        entry.Networks = SiteNetworks.From(status);
        entry.LastAnswer = now;
        entry.LastSeen = wall;
        entry.ConnectedSince ??= now;
        foreach (var port in entry.Requested.Where(p => Allows(status, p)).ToList())
        {
            entry.Requested.Remove(port);
        }

        return Evaluate(entry, now, edges);
    }

    internal void OnHeartbeat(SiteSession session, HelloReply hello, TimeSpan roundTrip)
    {
        var now = Time.GetTimestamp();
        Site? notBound = null;
        lock (_lock)
        {
            if (!_sites.TryGetValue(session.SiteId, out var entry) || entry.Session != session)
            {
                return;
            }

            entry.LastAnswer = now;
            entry.LastSeen = Time.GetUtcNow();
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

            return Time.GetElapsedTime(Math.Max(entry.LastAnswer, session.Started)) >= _options.OfflineAfter;
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
        var edges = new List<SiteEdge>();
        lock (_lock)
        {
            if (_sites.TryGetValue(session.SiteId, out var entry) && entry.Session == session)
            {
                entry.Session = null;
                entry.ConnectedSince = null;
                row = entry.Row;
                changed = Evaluate(entry, Time.GetTimestamp(), edges);
            }
        }

        WriteEdges(edges);
        if (row is not null)
        {
            LogSessionEnded(row.Name);
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

            session = new SiteSession(this, siteId, control, _options);
            var now = Time.GetTimestamp();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sites: could not {What}")]
    private partial void LogBackgroundFailed(string what, Exception ex);

    // ---------- the waiting times ----------

    private void Tick()
    {
        var now = Time.GetTimestamp();
        var changed = new List<Guid>();
        var expired = new List<WaitingNode>();
        var seen = new List<(Guid Id, DateTime At)>();
        var edges = new List<SiteEdge>();
        var silent = new List<(SiteSession Session, string Name)>();
        lock (_lock)
        {
            foreach (var entry in _sites.Values)
            {
                // A node that is connected but has not answered for the offline time: its session ends now (the heartbeat
                // would only notice after its own call timed out), so the site goes offline 30 s after the last answer.
                if (entry.Session is { IsEnded: false } session && Time.GetElapsedTime(Math.Max(entry.LastAnswer, session.Started), now) >= _options.OfflineAfter)
                {
                    entry.Session = null;
                    entry.ConnectedSince = null;
                    silent.Add((session, entry.Row.Name));
                }

                if (Evaluate(entry, now, edges))
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

            foreach (var waiting in _waiting.Values.Where(w => Time.GetElapsedTime(w.SinceTimestamp, now) >= _options.WaitingMaxAge).ToList())
            {
                _waiting.Remove(waiting.Key);
                expired.Add(waiting);
            }
        }

        WriteEdges(edges);
        foreach (var (session, name) in silent)
        {
            LogNotAnswering(name);
            Observe(session.EndAsync(), "end the session of a node that does not answer");
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

        if (changed.Count > 0 || expired.Count > 0 || silent.Count > 0)
        {
            RaiseList();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The site node of {Site} stopped answering: its connection is closed")]
    private partial void LogNotAnswering(string site);

    /// <summary>
    /// Moves the site between starting, online and offline (under the lock); true when it changed. The online and offline
    /// edges are added to <paramref name="edges"/>: the caller writes them with <see cref="WriteEdges"/> after the lock (an
    /// audit write is a database write, and SQLite writes synchronously). <paramref name="now"/> is a
    /// <see cref="TimeProvider.GetTimestamp"/>: the waiting times never follow the wall clock.
    /// </summary>
    private bool Evaluate(SiteEntry entry, long now, List<SiteEdge> edges)
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
                else if (Time.GetElapsedTime(entry.StartingSince, now) >= _options.StartWindow || entry.Row.NotBound)
                {
                    entry.State = SiteState.Offline;
                }

                break;
            case SiteState.Online:
                if (entry.Row.NotBound || (!connected && Time.GetElapsedTime(entry.LastAnswer, now) >= _options.OfflineAfter))
                {
                    entry.State = SiteState.Offline;
                }

                break;
            case SiteState.Offline:
                if (connected && !entry.Row.NotBound && entry.ConnectedSince is { } since && Time.GetElapsedTime(since, now) >= _options.OnlineAfter)
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
            edges.Add(new SiteEdge(entry.Row.Name, entry.State == SiteState.Online));
        }

        return true;
    }

    /// <summary>A site that went online or offline (written after the lock).</summary>
    private readonly record struct SiteEdge(string Name, bool Online);

    /// <summary>Logs and audits the edges <see cref="Evaluate"/> collected; never under the lock.</summary>
    private void WriteEdges(List<SiteEdge> edges)
    {
        foreach (var edge in edges)
        {
            if (edge.Online)
            {
                LogOnline(edge.Name);
            }
            else
            {
                LogOffline(edge.Name);
            }

            Observe(_audit.WriteAsync("system", string.Empty, edge.Online ? SiteAudit.Online : SiteAudit.Offline, edge.Name, null, CancellationToken.None), "write the audit entry");
        }
    }

    // ---------- listener ----------

    /// <summary>Accept site nodes: opens or closes the site listener without a restart. Off closes every node connection.</summary>
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
        var listener = new SiteListener(url, () => _identity.Current, _options.MaxHandshakes, link => _ = AcceptLinkAsync(link), _logger,
            new AddressRateLimiter(_options.ConnectionsPerMinute, 0, TimeSpan.FromMinutes(1), Time));
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
            waiting = [.. _waiting.Values];
            foreach (var node in waiting)
            {
                _waiting.Remove(node.Key);
            }

            sessions = [.. _sites.Values.Select(e => e.Session).OfType<SiteSession>()];
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
            NodeId = row.NodeId,
            NodeAddress = row.Address ?? string.Empty,
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
            await db.Sites.Where(s => s.Id == siteId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastSeenUtc, now)
                .SetProperty(s => s.Version, version)
                .SetProperty(s => s.Address, link.RemoteHost), _stop.Token).ConfigureAwait(false);

            lock (_lock)
            {
                if (_sites.TryGetValue(siteId, out var entry))
                {
                    entry.Row.LastSeenUtc = now;
                    entry.Row.Version = version;
                    entry.Row.Address = link.RemoteHost;
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
        var edges = new List<SiteEdge>();
        lock (_lock)
        {
            session = _sites.GetValueOrDefault(siteId)?.Session;
            if (_sites.TryGetValue(siteId, out var entry))
            {
                entry.Row.NotBound = true;
                Evaluate(entry, Time.GetTimestamp(), edges);
            }
        }

        WriteEdges(edges);
        var name = NameOf(siteId) ?? siteId.ToString();
        LogNotBound(name);
        await _audit.WriteAsync("system", string.Empty, SiteAudit.NodeReset, name, null, CancellationToken.None).ConfigureAwait(false);
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
        lock (_lock)
        {
            sessions = [.. _sites.Values.Select(e => e.Session).OfType<SiteSession>()];
            waiting = [.. _waiting.Values];
            _waiting.Clear();
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
        _bindLock.Dispose();
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

        /// <summary>The last answer of the node (a <see cref="TimeProvider.GetTimestamp"/>, never the wall clock).</summary>
        public long LastAnswer { get; set; }

        /// <summary>Since when the node is connected and answering (timestamp).</summary>
        public long? ConnectedSince { get; set; }

        /// <summary>When the start window began (timestamp).</summary>
        public long StartingSince { get; set; }

        /// <summary>The wall clock time of the last answer (shown and stored only).</summary>
        public DateTimeOffset? LastSeen { get; set; }

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
    public const string Replaced = "Replaced site";
    public const string Removed = "Removed site";
    public const string NodeReset = "Site node was reset";
    public const string Offline = "Site offline";
    public const string Online = "Site online";
}
