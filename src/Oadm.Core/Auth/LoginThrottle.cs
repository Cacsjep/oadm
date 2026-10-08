namespace Oadm.Core.Auth;

/// <summary>
/// Failed-login throttling per user name (case-insensitive, also for names that do not exist): 5 failures within
/// 5 minutes lock the name for 5 minutes. A successful login clears the failures. Server memory only; at most
/// <see cref="MaxNames"/> names are tracked (idle ones are dropped first).
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public const int MaxNames = 10_000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, State> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the name is locked: the time it is free again; else null.</summary>
    public DateTimeOffset? LockedUntil(string userName)
    {
        var now = time.GetUtcNow();
        lock (_names)
        {
            return _names.TryGetValue(Key(userName), out var state) && state.LockedUntil > now ? state.LockedUntil : null;
        }
    }

    /// <summary>Records a failure; returns true when this failure locked the name.</summary>
    public bool RecordFailure(string userName)
    {
        var now = time.GetUtcNow();
        lock (_names)
        {
            var key = Key(userName);
            if (!_names.TryGetValue(key, out var state))
            {
                if (_names.Count >= MaxNames)
                {
                    Prune(now);
                }

                state = new State();
                _names[key] = state;
            }

            state.Failures.RemoveAll(t => now - t >= Window);
            state.Failures.Add(now);
            state.LastUse = now;
            if (state.Failures.Count >= MaxFailures)
            {
                state.Failures.Clear();
                state.LockedUntil = now + LockDuration;
                return true;
            }

            return false;
        }
    }

    public void RecordSuccess(string userName)
    {
        lock (_names)
        {
            _names.Remove(Key(userName));
        }
    }

    private static string Key(string userName) => (userName ?? string.Empty).Trim();

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _names.Where(p => p.Value.LockedUntil <= now && now - p.Value.LastUse >= Window).Select(p => p.Key).ToList())
        {
            _names.Remove(key);
        }

        // Still full (an attack with many names): drop the oldest half.
        if (_names.Count >= MaxNames)
        {
            foreach (var key in _names.OrderBy(p => p.Value.LastUse).Take(_names.Count / 2).Select(p => p.Key).ToList())
            {
                _names.Remove(key);
            }
        }
    }

    private sealed class State
    {
        public List<DateTimeOffset> Failures { get; } = [];

        public DateTimeOffset LockedUntil { get; set; }

        public DateTimeOffset LastUse { get; set; }
    }
}
