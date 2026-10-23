using System.Collections.Concurrent;

namespace Oadm.Core.Auth;

/// <summary>
/// When a user's access (role, devices), a session or a device group's rules last changed, as a counter
/// (docs/spec/device-scope.md, Plugins: jobs). A call captures <see cref="Capture"/> before it reads the session
/// (<see cref="AuthenticatedCaller.AccessEpoch"/>); work registered later for that call (a plugin job, an add page session)
/// is refused when the user's access, the session or the group's rules changed since then (<see cref="ChangedSince"/>), so
/// a call that started with the old access never leaves work behind that the change missed. One entry per user, session
/// and group that changed, at most <see cref="MaxEntries"/>: beyond that the older half is dropped and their epochs become
/// a floor, below which every capture counts as changed (fail closed: a call that began before the dropped changes is
/// refused like one that saw them, never allowed).
/// </summary>
public sealed class AccessEpochs
{
    /// <summary>Entries kept before the older half is dropped (each ended session adds one).</summary>
    public const int MaxEntries = 4_096;

    private readonly ConcurrentDictionary<string, long> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _pruning = new();
    private long _now;
    private long _floor;

    /// <summary>The entries kept now (tests).</summary>
    internal int Count => _changed.Count;

    /// <summary>The epoch now: a change after this call has a larger one.</summary>
    public long Capture() => Interlocked.Read(ref _now);

    /// <summary>The user's role or devices changed, or every session of the user ended.</summary>
    public void UserChanged(Guid userId) => Record("u:" + userId);

    /// <summary>The session ended (<see cref="AuthenticatedCaller.SessionId"/>).</summary>
    public void SessionEnded(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Record("s:" + sessionId);
    }

    /// <summary>The rules of the scope's group changed or the group was deleted.</summary>
    public void ScopeChanged(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        Record("g:" + scopeKey);
    }

    /// <summary>True when the user's access, the session or the scope's rules changed after <paramref name="epoch"/> was captured.</summary>
    /// <remarks>
    /// The entries are looked up first and the floor is read last. <see cref="Prune"/> raises the floor before it removes
    /// an entry, and it removes only entries at or below the new floor: an entry this check misses because a prune removed
    /// it meanwhile had an epoch at or below a floor that was already raised, so the floor read afterwards catches it. With
    /// the floor read first, a prune between the two reads could hide the change (old floor, entry already gone).
    /// </remarks>
    public bool ChangedSince(long epoch, Guid userId, string? sessionId, string? scopeKey) =>
        After(epoch, "u:" + userId)
        || (sessionId is not null && After(epoch, "s:" + sessionId))
        || (scopeKey is not null && After(epoch, "g:" + scopeKey))
        || epoch < Interlocked.Read(ref _floor);

    private bool After(long epoch, string key) => _changed.TryGetValue(key, out var at) && at > epoch;

    private void Record(string key)
    {
        _changed[key] = Interlocked.Increment(ref _now);
        if (_changed.Count > MaxEntries)
        {
            Prune();
        }
    }

    /// <summary>Drops the older half; the floor rises first, so a dropped change is never missed by a check meanwhile.</summary>
    private void Prune()
    {
        lock (_pruning)
        {
            if (_changed.Count <= MaxEntries)
            {
                return;
            }

            var epochs = _changed.Values.ToArray();
            Array.Sort(epochs);
            var cut = epochs[epochs.Length / 2];
            if (cut > Interlocked.Read(ref _floor))
            {
                Interlocked.Exchange(ref _floor, cut);
            }

            foreach (var entry in _changed)
            {
                if (entry.Value <= cut)
                {
                    _changed.TryRemove(entry);
                }
            }
        }
    }
}
