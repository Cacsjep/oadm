using System.Collections.Concurrent;

using Oadm.Core.Auth;

namespace Oadm.Server.Discovery;

/// <summary>
/// Who started each discovery session of the add page (docs/spec/device-scope.md, Decisions "Add and discovery sessions"):
/// a session belongs to that user; Watch, Retry, Commit and Stop take only the user's own sessions (another user's session
/// id answers like an unknown one). A session nobody registered (the server's own work) belongs to no user. An entry is
/// kept while the session exists (<see cref="IDiscoveryBackend.IsActive"/>) or a watch of it is open (<see cref="Hold"/>):
/// the add page may stay open for hours. Once neither holds, it is forgotten <see cref="Lifetime"/> after its last use (the
/// login results of a session live as long).
/// </summary>
public sealed class DiscoverySessionOwners(TimeProvider time, IDiscoveryBackend? sessions = null)
{
    /// <summary>How long an entry is kept after its last use.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _owners = new(StringComparer.Ordinal);
    private long _lastPruneTicks;

    /// <summary>The entries known now (tests).</summary>
    internal int Count => _owners.Count;

    /// <summary>The session was started by <paramref name="caller"/>.</summary>
    public void Register(string sessionId, AuthenticatedCaller caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(caller);
        var now = time.GetUtcNow();
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last >= PruneEvery.Ticks && Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) == last)
        {
            // Now and then, not on every start: an import starts one session per line.
            foreach (var (id, entry) in _owners)
            {
                if (entry.Holds == 0 && now.UtcTicks - entry.TouchedTicks > Lifetime.Ticks && sessions?.IsActive(id) != true)
                {
                    _owners.TryRemove(new KeyValuePair<string, Entry>(id, entry));
                }
            }
        }

        _owners[sessionId] = new Entry(caller.UserId, now.UtcTicks);
    }

    /// <summary>True when <paramref name="caller"/> started the session (and it is still known).</summary>
    public bool Owns(string sessionId, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(sessionId) || !_owners.TryGetValue(sessionId, out var entry) || entry.UserId != caller.UserId)
        {
            return false;
        }

        entry.Touch(time.GetUtcNow());
        return true;
    }

    /// <summary>
    /// Keeps the entry of a session the caller owns while the returned scope is open (an open watch of the add page); its
    /// lifetime starts again when the scope ends. Null when <paramref name="caller"/> does not own the session.
    /// </summary>
    public IDisposable? Hold(string sessionId, AuthenticatedCaller caller)
    {
        if (!Owns(sessionId, caller) || !_owners.TryGetValue(sessionId, out var entry))
        {
            return null;
        }

        entry.Hold();
        return new Holding(entry, time);
    }

    /// <summary>The sessions of <paramref name="sessionIds"/> that <paramref name="caller"/> started.</summary>
    public IReadOnlyList<string> Own(IEnumerable<string> sessionIds, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        return [.. sessionIds.Where(id => Owns(id, caller)).Distinct(StringComparer.Ordinal)];
    }

    private sealed class Entry(Guid userId, long touchedTicks)
    {
        private long _touchedTicks = touchedTicks;
        private int _holds;

        public Guid UserId { get; } = userId;

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
