namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// The graphics card's sessions of one set, all models together (docs/spec/ocr.md "GPU", graphics card memory). On DirectML
/// each input size is a session of its own, and each session keeps its own copy of the model and its own buffers for that
/// size on the card until it is disposed. So the set keeps at most <see cref="MaxSessions"/> sessions over all its models,
/// of them at most <see cref="MaxDetectionSessions"/> of the text finder (the least recently used goes, with one log line the
/// first time), and frees every session unused for
/// <see cref="IdleTimeout"/>. Every model call of the set runs inside <see cref="Gate"/>, so a session is never disposed
/// while it runs (the card's calls run one at a time anyway).
/// </summary>
internal sealed class GpuSessionBudget : IDisposable
{
    /// <summary>Sessions kept on the card at most, all models of the set together.</summary>
    public const int MaxSessions = 6;

    /// <summary>Sessions of the text finder at most, within <see cref="MaxSessions"/>: its large inputs take the most memory.</summary>
    public const int MaxDetectionSessions = 3;

    /// <summary>How long an unused session is kept.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly int _max;
    private readonly TimeProvider _time;
    private readonly Action<int>? _capped;
    private readonly Dictionary<(object Owner, string Key), Entry> _entries = [];
    private readonly ITimer _trim;
    private long _clock;
    private bool _cappedTold;
    private bool _disposed;

    /// <param name="max">Sessions at most (<see cref="MaxSessions"/>; tests: fewer).</param>
    /// <param name="time">The clock of the idle release (tests: a fake one).</param>
    /// <param name="capped">Called once, the first time the cap frees a session (the log line), with the cap.</param>
    public GpuSessionBudget(int max = MaxSessions, TimeProvider? time = null, Action<int>? capped = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        _max = max;
        _time = time ?? TimeProvider.System;
        _capped = capped;
        var period = IdleTimeout / 2;
        _trim = _time.CreateTimer(_ => TrimIdle(), null, period, period);
    }

    /// <summary>Held around every model call of the set: the session it uses is never freed meanwhile.</summary>
    public object Gate { get; } = new();

    /// <summary>Sessions kept now.</summary>
    public int Count
    {
        get
        {
            lock (Gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Sessions freed because the cap was reached (tests, the log).</summary>
    public int Evicted { get; private set; }

    /// <summary>
    /// The session of <paramref name="owner"/> (a model) for the input size <paramref name="key"/>; made with
    /// <paramref name="create"/> when there is none, after the least recently used session went: of the same model when it
    /// has <paramref name="ownerMax"/> already, else of any model when the set has <see cref="MaxSessions"/>.
    /// </summary>
    /// <param name="ownerMax">Sessions of this model at most (the text finder: <see cref="MaxDetectionSessions"/>, its large
    /// inputs take the most memory); null: only the set's cap.</param>
    /// <exception cref="ObjectDisposedException">The budget was disposed.</exception>
    public T Get<T>(object owner, string key, Func<T> create, int? ownerMax = null)
        where T : class, IDisposable
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _time.GetTimestamp();
            if (_entries.TryGetValue((owner, key), out var found))
            {
                found.Used = now;
                found.Order = ++_clock;
                return (T)found.Session;
            }

            while (ownerMax is { } most && _entries.Count(e => ReferenceEquals(e.Key.Owner, owner)) >= Math.Max(1, most))
            {
                Evict(_entries.Where(e => ReferenceEquals(e.Key.Owner, owner)).MinBy(e => e.Value.Order).Key);
            }

            while (_entries.Count >= _max)
            {
                Evict(_entries.MinBy(e => e.Value.Order).Key);
            }

            var session = create();
            _entries[(owner, key)] = new Entry(session) { Used = now, Order = ++_clock };
            return session;
        }
    }

    private void Evict((object Owner, string Key) key)
    {
        var entry = _entries[key];
        _entries.Remove(key);
        entry.Session.Dispose();
        Evicted++;
        if (!_cappedTold)
        {
            _cappedTold = true;
            _capped?.Invoke(_max);
        }
    }

    /// <summary>Frees the sessions unused for <see cref="IdleTimeout"/>.</summary>
    internal void TrimIdle()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetTimestamp();
            foreach (var (key, entry) in _entries.Where(e => _time.GetElapsedTime(e.Value.Used, now) >= IdleTimeout).ToList())
            {
                _entries.Remove(key);
                entry.Session.Dispose();
            }
        }
    }

    /// <summary>Frees every session of <paramref name="owner"/> (a model's sessions are disposed).</summary>
    public void Release(object owner)
    {
        lock (Gate)
        {
            foreach (var (key, entry) in _entries.Where(e => ReferenceEquals(e.Key.Owner, owner)).ToList())
            {
                _entries.Remove(key);
                entry.Session.Dispose();
            }
        }
    }

    /// <summary>Frees every session; later <see cref="Get"/> calls end with <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _trim.Dispose();
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Session.Dispose();
            }

            _entries.Clear();
        }
    }

    private sealed class Entry(IDisposable session)
    {
        public IDisposable Session { get; } = session;

        /// <summary>When it was last used (the idle release).</summary>
        public long Used { get; set; }

        /// <summary>The order of the last use (the least recently used goes first).</summary>
        public long Order { get; set; }
    }
}
