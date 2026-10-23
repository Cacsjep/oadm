using System.Collections.Concurrent;

using Oadm.Core.Auth;

namespace Oadm.Server.Discovery;

/// <summary>
/// Who started each discovery session of the add page (docs/spec/device-scope.md, Decisions "Add and discovery sessions"):
/// a session belongs to that user; Watch, Retry, Commit and Stop take only the user's own sessions (another user's session
/// id answers like an unknown one). A session nobody registered (the server's own work) belongs to no user. Entries are
/// forgotten <see cref="Lifetime"/> after their last use (the login results of a session live as long).
/// </summary>
public sealed class DiscoverySessionOwners(TimeProvider time)
{
    /// <summary>How long an entry is kept after its last use.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _owners = new(StringComparer.Ordinal);
    private long _lastPruneTicks;

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
                if (now - entry.Touched > Lifetime)
                {
                    _owners.TryRemove(new KeyValuePair<string, Entry>(id, entry));
                }
            }
        }

        _owners[sessionId] = new Entry(caller.UserId, now);
    }

    /// <summary>True when <paramref name="caller"/> started the session (and it is still known).</summary>
    public bool Owns(string sessionId, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(sessionId) || !_owners.TryGetValue(sessionId, out var entry) || entry.UserId != caller.UserId)
        {
            return false;
        }

        _owners[sessionId] = entry with { Touched = time.GetUtcNow() };
        return true;
    }

    /// <summary>The sessions of <paramref name="sessionIds"/> that <paramref name="caller"/> started.</summary>
    public IReadOnlyList<string> Own(IEnumerable<string> sessionIds, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        return [.. sessionIds.Where(id => Owns(id, caller)).Distinct(StringComparer.Ordinal)];
    }

    private sealed record Entry(Guid UserId, DateTimeOffset Touched);
}
