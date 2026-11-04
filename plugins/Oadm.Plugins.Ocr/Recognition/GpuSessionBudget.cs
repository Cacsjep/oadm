namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>A call needs a session larger than the card's cap: that call reads on the CPU, the card stays in use.</summary>
internal sealed class SessionTooLargeException(long bytes, long max)
    : Exception($"A session of {bytes / (1024 * 1024)} MB does not fit the {max / (1024 * 1024)} MB of the graphics card");

/// <summary>
/// The graphics card's memory for the models (docs/spec/ocr.md "GPU", graphics card memory). On DirectML each
/// input size is a session of its own, and each session keeps its own copy of the model and its own buffers for that size on
/// the card until it is disposed. The budget counts each session by its input pixels (<see cref="Estimate"/>: measured
/// <see cref="BytesPerPixel"/> per pixel of a call plus <see cref="SessionBytes"/>) and keeps the sum at or below
/// the cap (<see cref="CapFor"/>): before a new session the least recently used ones go (one log line the first time). A session larger
/// than the cap is never made (<see cref="SessionTooLargeException"/>; the callers split a Search into calls of at most <see cref="MaxCallBytes"/>). Every session
/// unused for <see cref="IdleTimeout"/> is freed. A model call holds its session (<see cref="Hold"/>), so a session is never
/// disposed while it runs.
/// </summary>
internal sealed class GpuSessionBudget : IDisposable
{
    /// <summary>The estimated card memory of all sessions together on a card that does not tell its size: 4 GB.</summary>
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The share of the card's memory the sessions may take together; the rest stays with the desktop and other programs.</summary>
    public const double CardShare = 0.8;

    /// <summary>The cap for a card of <paramref name="cardBytes"/>; null (it does not tell): <see cref="MaxBytes"/>.</summary>
    public static long CapFor(long? cardBytes) => cardBytes is > 0 ? (long)(cardBytes.Value * CardShare) : MaxBytes;

    /// <summary>
    /// Card memory per input pixel of a text finder call (the pixels of all tiles of the call), measured on DirectML
    /// (2026-11-03, RTX 5070 Ti; docs/spec/ocr.md "GPU"), with <see cref="SessionBytes"/> never below a measured session.
    /// </summary>
    public const long BytesPerPixel = 1500;

    /// <summary>Card memory of a session apart from its input size (the model and its constant buffers), rounded up.</summary>
    public const long SessionBytes = 512L * 1024 * 1024;

    /// <summary>
    /// One call's session at most: half the cap, so two Search sizes (two cameras' resolutions) stay on the card together and
    /// never push each other out at every picture. A single tile of the largest size (1024 x 1024) always fits.
    /// </summary>
    public const long MaxCallBytes = MaxBytes / 2;

    /// <summary>How long an unused session is kept.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly long _max;
    private readonly TimeProvider _time;
    private readonly Action<long>? _capped;
    private readonly Dictionary<(object Owner, string Key), Entry> _entries = [];
    private readonly ITimer _trim;
    private long _clock;
    private long _bytes;
    private bool _cappedTold;
    private bool _disposed;

    /// <param name="maxBytes">Estimated bytes at most (<see cref="MaxBytes"/>; tests: less).</param>
    /// <param name="time">The clock of the idle release (tests: a fake one).</param>
    /// <param name="capped">Called once, the first time the cap frees a session (the log line), with the cap.</param>
    public GpuSessionBudget(long maxBytes = MaxBytes, TimeProvider? time = null, Action<long>? capped = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _max = maxBytes;
        _time = time ?? TimeProvider.System;
        _capped = capped;
        var period = IdleTimeout / 2;
        _trim = _time.CreateTimer(_ => TrimIdle(), null, period, period);
    }

    /// <summary>Card memory of a mobile text finder session by its pixels (measured 2026-11-04: 640 x 736 240 MB, 4 pictures 630 MB).</summary>
    public static long EstimateFinder(long inputPixels) => (128L * 1024 * 1024) + (inputPixels * 400);

    /// <summary>Card memory of a reader or angle session by its pixels (measured 2026-11-04: 1 x 48 x 320 120 MB, 32 x 48 x 1280 920 MB).</summary>
    public static long EstimateReader(long inputPixels) => (128L * 1024 * 1024) + (inputPixels * 300);

    /// <summary>The estimated card memory of a session whose calls carry <paramref name="inputPixels"/> pixels (all tiles).</summary>
    public static long Estimate(long inputPixels) => SessionBytes + (inputPixels * BytesPerPixel);

    /// <summary>
    /// Tiles per text finder call for an area of <paramref name="tiles"/> tiles of <paramref name="tileWidth"/> x
    /// <paramref name="tileHeight"/>: the calls as few as <see cref="MaxCallBytes"/> allows (at least 1 tile each, a single tile
    /// always fits), the tiles spread evenly over them, so every call of the area has the same size (one session).
    /// </summary>
    public static int TilesPerCall(int tiles, int tileWidth, int tileHeight, long maxCallBytes = MaxCallBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tiles, 1);
        var pixels = (long)tileWidth * tileHeight;
        var most = (int)Math.Clamp((maxCallBytes - SessionBytes) / Math.Max(1, pixels * BytesPerPixel), 1, tiles);
        var calls = (tiles + most - 1) / most;
        return (tiles + calls - 1) / calls;
    }

    /// <summary>Guards the sessions.</summary>
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

    /// <summary>The estimated card memory of the sessions kept now.</summary>
    public long Bytes
    {
        get
        {
            lock (Gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>The cap of this budget.</summary>
    public long Max => _max;

    /// <summary>Sessions freed because the cap was reached (tests, the log).</summary>
    public int Evicted { get; private set; }

    /// <summary>
    /// The session of <paramref name="owner"/> (a model) for the input size <paramref name="key"/>; made with
    /// <paramref name="create"/> when there is none, after the least recently used sessions went until its
    /// <paramref name="bytes"/> fit under the cap.
    /// </summary>
    /// <exception cref="SessionTooLargeException"><paramref name="bytes"/> exceed the cap on their own (never made).</exception>
    /// <exception cref="ObjectDisposedException">The budget was disposed.</exception>
    public T Get<T>(object owner, string key, long bytes, Func<T> create)
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

            if (bytes > _max)
            {
                throw new SessionTooLargeException(bytes, _max);
            }

            while (_bytes + bytes > _max && _entries.Values.Any(e => e.Held == 0))
            {
                Evict(_entries.Where(e => e.Value.Held == 0).MinBy(e => e.Value.Order).Key);
            }

            var session = create();
            _entries[(owner, key)] = new Entry(session, bytes) { Used = now, Order = ++_clock };
            _bytes += bytes;
            return session;
        }
    }

    /// <summary>
    /// The session like <see cref="Get"/>, held for a call: it is not freed before <see cref="Free"/>. A new session that does not
    /// fit while the others are held waits for their calls.
    /// </summary>
    public T Hold<T>(object owner, string key, long bytes, Func<T> create)
        where T : class, IDisposable
    {
        lock (Gate)
        {
            // Over the cap on its own it never fits, whatever ends: Get says so at once.
            while (!_disposed && bytes <= _max && !_entries.ContainsKey((owner, key)) && _entries.Values.Where(e => e.Held > 0).Sum(e => e.Bytes) + bytes > _max && _entries.Values.Any(e => e.Held > 0))
            {
                Monitor.Wait(Gate);
            }

            var session = Get(owner, key, bytes, create);
            _entries[(owner, key)].Held++;
            return session;
        }
    }

    /// <summary>The call that held the session ended.</summary>
    public void Free(object owner, string key)
    {
        lock (Gate)
        {
            if (_entries.TryGetValue((owner, key), out var entry))
            {
                entry.Held--;
            }

            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>The session exists, or <paramref name="bytes"/> fit beside the others without pushing one out.</summary>
    public bool Fits(object owner, string key, long bytes)
    {
        lock (Gate)
        {
            return _entries.ContainsKey((owner, key)) || _bytes + bytes <= _max;
        }
    }

    private void Evict((object Owner, string Key) key)
    {
        Remove(key);
        Evicted++;
        if (!_cappedTold)
        {
            _cappedTold = true;
            _capped?.Invoke(_max);
        }
    }

    private void Remove((object Owner, string Key) key)
    {
        var entry = _entries[key];
        _entries.Remove(key);
        _bytes -= entry.Bytes;
        entry.Session.Dispose();
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
            foreach (var key in _entries.Where(e => e.Value.Held == 0 && _time.GetElapsedTime(e.Value.Used, now) >= IdleTimeout).Select(e => e.Key).ToList())
            {
                Remove(key);
            }
        }
    }

    /// <summary>Frees every session of <paramref name="owner"/> (a model's sessions are disposed); one a hung call holds is left to the process.</summary>
    public void Release(object owner)
    {
        lock (Gate)
        {
            foreach (var key in _entries.Where(e => ReferenceEquals(e.Key.Owner, owner) && e.Value.Held == 0).Select(e => e.Key).ToList())
            {
                Remove(key);
            }
        }
    }

    /// <summary>
    /// Frees every session but one a hung call still holds (left to the process: never freed under a call); later
    /// <see cref="Get"/> calls end with <see cref="ObjectDisposedException"/>.
    /// </summary>
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
            foreach (var entry in _entries.Values.Where(e => e.Held == 0))
            {
                entry.Session.Dispose();
            }

            _entries.Clear();
            _bytes = 0;
            Monitor.PulseAll(Gate);
        }
    }

    private sealed class Entry(IDisposable session, long bytes)
    {
        public IDisposable Session { get; } = session;

        /// <summary>Its estimated card memory.</summary>
        public long Bytes { get; } = bytes;

        /// <summary>When it was last used (the idle release).</summary>
        public long Used { get; set; }

        /// <summary>Calls that hold it now: it is not freed.</summary>
        public int Held { get; set; }

        /// <summary>The order of the last use (the least recently used goes first).</summary>
        public long Order { get; set; }
    }
}
