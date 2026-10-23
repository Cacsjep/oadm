using System.Collections.Concurrent;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.AddDevices;

namespace Oadm.Server.Discovery;

/// <summary>
/// Who started each discovery session of the add page, and how long the session lives (docs/spec/device-scope.md,
/// Decisions "Add and discovery sessions"; docs/spec/add-devices.md): a session belongs to that user; Watch, Retry, Commit
/// and Stop take only the user's own sessions (another user's session id answers like an unknown one). A session nobody
/// registered (the server's own work) belongs to no user.
/// <para>A session lives while a watch of it is open (<see cref="Hold"/>: the add page may stay open for hours), then
/// <see cref="Lifetime"/> after its last use; after that it is stopped on the server and its owner entry and its logins
/// (typed passwords, the credential list permission) are forgotten, so a client that crashed or lost its connection leaves
/// nothing behind. A user's sessions stop at once when the user's login session ends (logout, expiry, a new password, a
/// disabled or deleted user), when the user's devices or role change, and when the rules of the user's group change or the
/// group is deleted: a session never keeps the reach it was started with. The change is recorded in
/// <see cref="AccessEpochs"/> before the sessions are looked at, so a session registered by a call that began before the
/// change is refused (<see cref="Register"/>).</para>
/// </summary>
public sealed partial class DiscoverySessionOwners : IDisposable
{
    /// <summary>How long a session without an open watch is kept after its last use.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>How often idle sessions are looked for.</summary>
    public static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _owners = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly IDiscoveryBackend? _sessions;
    private readonly DiscoveryAuthenticator? _authenticator;
    private readonly AccessEpochs _epochs;
    private readonly UserStore? _users;
    private readonly AuthTokenStore? _tokens;
    private readonly DeviceGroupStore? _groups;
    private readonly ILogger<DiscoverySessionOwners>? _logger;
    private readonly ITimer _prune;

    public DiscoverySessionOwners(
        TimeProvider time,
        IDiscoveryBackend? sessions = null,
        DiscoveryAuthenticator? authenticator = null,
        AccessEpochs? epochs = null,
        UserStore? users = null,
        AuthTokenStore? tokens = null,
        DeviceGroupStore? groups = null,
        ILogger<DiscoverySessionOwners>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _sessions = sessions;
        _authenticator = authenticator;
        _epochs = epochs ?? new AccessEpochs();
        _users = users;
        _tokens = tokens;
        _groups = groups;
        _logger = logger;

        // The timer never carries the caller of whoever made this.
        _prune = CallerContext.Detached(() => time.CreateTimer(_ => PruneIdle(), null, PruneEvery, PruneEvery));
        if (_users is not null)
        {
            _users.UserDeviceAccessChanged += OnUserChanged;
            _users.UserRoleChanged += OnUserChanged;
        }

        if (_tokens is not null)
        {
            _tokens.SessionsEnded += OnSessionsEnded;
        }

        if (_groups is not null)
        {
            _groups.Changed += OnGroupChanged;
            _groups.Deleted += OnGroupDeleted;
        }
    }

    /// <summary>The entries known now (tests).</summary>
    internal int Count => _owners.Count;

    /// <summary>
    /// The session was started by <paramref name="caller"/>. False when the caller's access, login session or group changed
    /// after the call began: the session is not kept (the caller stops it with <see cref="StopAsync"/>).
    /// </summary>
    public bool Register(string sessionId, AuthenticatedCaller caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(caller);
        var group = caller.HasAllDevices ? null : caller.DeviceGroupId;
        var entry = new Entry(caller.UserId, caller.SessionId, group, _time.GetUtcNow().UtcTicks);
        _owners[sessionId] = entry;

        // Registered first, then checked: a change either came before (refused here) or comes after and finds the entry.
        if (_epochs.ChangedSince(caller.AccessEpoch, caller.UserId, caller.SessionId, group?.ToString()))
        {
            _owners.TryRemove(new KeyValuePair<string, Entry>(sessionId, entry));
            return false;
        }

        return true;
    }

    /// <summary>True when <paramref name="caller"/> started the session (and it is still known).</summary>
    public bool Owns(string sessionId, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(sessionId) || !_owners.TryGetValue(sessionId, out var entry) || entry.UserId != caller.UserId)
        {
            return false;
        }

        entry.Touch(_time.GetUtcNow());
        return true;
    }

    /// <summary>
    /// Keeps the session of the caller while the returned scope is open (an open watch of the add page); its lifetime
    /// starts again when the scope ends. Null when <paramref name="caller"/> does not own the session.
    /// </summary>
    public IDisposable? Hold(string sessionId, AuthenticatedCaller caller)
    {
        if (!Owns(sessionId, caller) || !_owners.TryGetValue(sessionId, out var entry))
        {
            return null;
        }

        entry.Hold();
        return new Holding(entry, _time);
    }

    /// <summary>The sessions of <paramref name="sessionIds"/> that <paramref name="caller"/> started.</summary>
    public IReadOnlyList<string> Own(IEnumerable<string> sessionIds, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        return [.. sessionIds.Where(id => Owns(id, caller)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Stops the session on the server and forgets its owner and its logins (unknown ids: nothing happens).</summary>
    public async Task StopAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _owners.TryRemove(sessionId, out _);
        if (_sessions is not null)
        {
            await _sessions.StopAsync(sessionId).ConfigureAwait(false);
        }

        _authenticator?.Forget(sessionId); // after the stop: no new login of the session starts meanwhile
    }

    /// <summary>Stops every session of <paramref name="userId"/> (also used by tests).</summary>
    public Task StopUserAsync(Guid userId)
    {
        _epochs.UserChanged(userId); // first: a session registered meanwhile is either refused or found below
        return StopWhereAsync(e => e.UserId == userId);
    }

    /// <summary>Stops every session started in the login session <paramref name="sessionId"/> (<see cref="AuthenticatedCaller.SessionId"/>).</summary>
    public Task StopSessionAsync(string sessionId)
    {
        _epochs.SessionEnded(sessionId);
        return StopWhereAsync(e => string.Equals(e.LoginSession, sessionId, StringComparison.Ordinal));
    }

    /// <summary>Stops every session of the users of the group (its rules changed or it was deleted).</summary>
    public Task StopGroupAsync(Guid groupId)
    {
        _epochs.ScopeChanged(groupId.ToString());
        return StopWhereAsync(e => e.GroupId == groupId);
    }

    public void Dispose()
    {
        _prune.Dispose();
        if (_users is not null)
        {
            _users.UserDeviceAccessChanged -= OnUserChanged;
            _users.UserRoleChanged -= OnUserChanged;
        }

        if (_tokens is not null)
        {
            _tokens.SessionsEnded -= OnSessionsEnded;
        }

        if (_groups is not null)
        {
            _groups.Changed -= OnGroupChanged;
            _groups.Deleted -= OnGroupDeleted;
        }
    }

    /// <summary>Stops the sessions without an open watch that were not used for <see cref="Lifetime"/> (the timer, tests).</summary>
    internal Task PruneIdleAsync()
    {
        var now = _time.GetUtcNow().UtcTicks;
        return StopWhereAsync(e => e.Holds == 0 && now - e.TouchedTicks > Lifetime.Ticks);
    }

    private void PruneIdle() => Run(PruneIdleAsync());

    private void OnUserChanged(object? sender, Guid userId) => Run(StopUserAsync(userId));

    private void OnSessionsEnded(object? sender, EndedSessions e)
    {
        if (e.UserId is { } userId)
        {
            Run(StopUserAsync(userId));
        }

        if (e.TokenHash is { } hash)
        {
            Run(StopSessionAsync(SessionIds.Of(hash)));
        }
    }

    private void OnGroupChanged(object? sender, Guid groupId) => Run(StopGroupAsync(groupId));

    private void OnGroupDeleted(object? sender, DeviceGroupDeleteResult deleted) => Run(StopGroupAsync(deleted.Id));

    private async Task StopWhereAsync(Func<Entry, bool> which)
    {
        foreach (var (id, entry) in _owners)
        {
            if (which(entry) && _owners.TryRemove(new KeyValuePair<string, Entry>(id, entry)))
            {
                await StopAsync(id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>An event handler or the timer: a failure is logged, never thrown into the code that raised the event.</summary>
    private async void Run(Task stopping)
    {
        try
        {
            await stopping.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failed stop must not break the event's other handlers; the next prune tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (_logger is not null)
            {
                LogStopFailed(_logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping a discovery session of the add page failed")]
    private static partial void LogStopFailed(ILogger logger, Exception ex);

    private sealed class Entry(Guid userId, string loginSession, Guid? groupId, long touchedTicks)
    {
        private long _touchedTicks = touchedTicks;
        private int _holds;

        public Guid UserId { get; } = userId;

        public string LoginSession { get; } = loginSession;

        public Guid? GroupId { get; } = groupId;

        public long TouchedTicks => Interlocked.Read(ref _touchedTicks);

        public int Holds => Volatile.Read(ref _holds);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _touchedTicks, now.UtcTicks);

        public void Hold() => Interlocked.Increment(ref _holds);

        public void Release() => Interlocked.Decrement(ref _holds);
    }

    private sealed class Holding(Entry entry, TimeProvider time) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                entry.Touch(time.GetUtcNow());
                entry.Release();
            }
        }
    }
}
