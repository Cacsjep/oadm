using System.Collections.Concurrent;

namespace Oadm.Core.Auth;

/// <summary>
/// When a user's access (role, devices), a session or a device group's rules last changed, as a counter
/// (docs/spec/device-scope.md, Plugins: jobs). A call captures <see cref="Capture"/> before it reads the session
/// (<see cref="AuthenticatedCaller.AccessEpoch"/>); work registered later for that call (a plugin job) is refused when the
/// user's access, the session or the group's rules changed since then (<see cref="ChangedSince"/>), so a call that started
/// with the old access never leaves a job behind that the change missed. One entry per user, session and group that changed.
/// </summary>
public sealed class AccessEpochs
{
    private readonly ConcurrentDictionary<string, long> _changed = new(StringComparer.OrdinalIgnoreCase);
    private long _now;

    /// <summary>The epoch now: a change after this call has a larger one.</summary>
    public long Capture() => Interlocked.Read(ref _now);

    /// <summary>The user's role or devices changed, or every session of the user ended.</summary>
    public void UserChanged(Guid userId) => _changed["u:" + userId] = Interlocked.Increment(ref _now);

    /// <summary>The session ended (<see cref="AuthenticatedCaller.SessionId"/>).</summary>
    public void SessionEnded(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _changed["s:" + sessionId] = Interlocked.Increment(ref _now);
    }

    /// <summary>The rules of the scope's group changed or the group was deleted.</summary>
    public void ScopeChanged(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        _changed["g:" + scopeKey] = Interlocked.Increment(ref _now);
    }

    /// <summary>True when the user's access, the session or the scope's rules changed after <paramref name="epoch"/> was captured.</summary>
    public bool ChangedSince(long epoch, Guid userId, string? sessionId, string? scopeKey) =>
        After(epoch, "u:" + userId)
        || (sessionId is not null && After(epoch, "s:" + sessionId))
        || (scopeKey is not null && After(epoch, "g:" + scopeKey));

    private bool After(long epoch, string key) => _changed.TryGetValue(key, out var at) && at > epoch;
}
