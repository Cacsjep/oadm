using Oadm.Plugins.Ocr.Model;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>The text finder of a reading: on the CPU workers, or the graphics card's for Search (docs/spec/ocr.md "GPU").</summary>
internal interface ITextFinder
{
    /// <summary>True when the tiles of an area go in few calls of several tiles (a graphics card); else one call per tile.</summary>
    bool BatchTiles { get; }

    /// <summary>The side of the text finder's input for an area of <paramref name="pixels"/> (a multiple of 32, on DirectML in a few steps).</summary>
    int DetectionSide(int pixels);

    /// <summary>Tiles per call for an area of <paramref name="tiles"/> tiles of one size (only with <see cref="BatchTiles"/>).</summary>
    int TilesPerCall(int tiles, int tileWidth, int tileHeight) => 1;

    /// <summary>The text finder's maps of <paramref name="count"/> pictures of one size ([count, height, width]), rented: the caller disposes them.</summary>
    Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct);
}

/// <summary>Runs the three models for one reading: the CPU workers, or the graphics card (<see cref="CardRunner"/>).</summary>
internal interface IModelRunner : ITextFinder
{
    /// <summary>The reader's character list.</summary>
    CharacterList Characters { get; }

    /// <summary>The text finder of an area read in tiles (Search over a large area): the graphics card's when it is used.</summary>
    ITextFinder Search { get; }

    /// <summary>True when the text finder is the graphics card (an area's reading on the card is never reused on the CPU).</summary>
    bool SearchOnCard { get; }

    /// <summary>The probability that each line is upside down.</summary>
    Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct);

    /// <summary>The reader's output of each line.</summary>
    Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct);
}

/// <summary>
/// The CPU workers (docs/spec/ocr.md rule 4): every model call takes one of <see cref="Model.OcrLimits.Workers"/> slots and runs
/// on one thread (<see cref="Model.OcrLimits.ThreadsPerWorker"/>). All calls share one session set: a session runs calls at
/// once (the model's weights in memory once), and a call of one thread runs on its caller. The tiles of one picture and its
/// lines are separate calls, so an otherwise idle server reads one picture on several workers at once, and many readers share
/// the same slots fairly. A set unused for <see cref="IdleTimeout"/> is disposed (its memory goes back); a set still in use
/// when the workers are disposed is disposed when its last call ends, never while it runs.
/// </summary>
internal sealed class CpuWorkers<TSet> : IDisposable
    where TSet : class, IDisposable
{
    /// <summary>How long an unused session set is kept.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _accurate;
    private readonly SemaphoreSlim _large;
    private readonly Func<bool, TSet> _create;
    private readonly BusyMeter? _busy;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly object _creating = new();
    private readonly Shared?[] _sets = new Shared?[2];
    private readonly ITimer _trim;
    private int _waiting;
    private bool _disposed;

    /// <param name="slots">Model calls at once.</param>
    /// <param name="accurateSets">Calls with the accurate (large) models at once, within <paramref name="slots"/>.</param>
    /// <param name="create">Makes a session set: the accurate models when true.</param>
    /// <param name="busy">Counts the time each call holds its slot (the recognition load); null: not measured.</param>
    /// <param name="time">The clock of the idle trimming (tests: a fake one).</param>
    public CpuWorkers(int slots, int accurateSets, Func<bool, TSet> create, BusyMeter? busy = null, TimeProvider? time = null)
    {
        _busy = busy;
        _slots = new SemaphoreSlim(slots, slots);
        _accurate = new SemaphoreSlim(accurateSets, accurateSets);
        var large = Math.Clamp(Model.OcrLimits.LargeTextFinderCalls, 1, slots);
        _large = new SemaphoreSlim(large, large);
        _create = create;
        _time = time ?? TimeProvider.System;
        Slots = slots;
        var period = IdleTimeout / 2;
        _trim = _time.CreateTimer(_ => TrimIdle(), null, period, period);
    }

    /// <summary>Model calls at once.</summary>
    public int Slots { get; }

    /// <summary>Slots in use now (tests: the gate holds).</summary>
    public int Busy => Slots - _slots.CurrentCount;

    /// <summary>A slot is free and no call waits for one.</summary>
    public bool HasRoom => _slots.CurrentCount > 0 && Volatile.Read(ref _waiting) == 0 && _large.CurrentCount > 0;

    /// <summary>
    /// Runs a large text finder call: at most <see cref="Model.OcrLimits.LargeTextFinderCalls"/> at once, the others wait without
    /// holding a worker.
    /// </summary>
    public async Task<T> RunLargeAsync<T>(bool accurate, Func<TSet, Task<T>> work, CancellationToken ct)
    {
        await _large.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunAsync(accurate, work, ct).ConfigureAwait(false);
        }
        finally
        {
            _large.Release();
        }
    }

    /// <summary>Session sets kept for the next calls (tests: the idle trimming).</summary>
    public int Idle
    {
        get
        {
            lock (_lock)
            {
                return _sets.Count(s => s is { InUse: 0 });
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on a free slot with the shared set.</summary>
    /// <exception cref="ObjectDisposedException">The workers were disposed.</exception>
    public async Task<T> RunAsync<T>(bool accurate, Func<TSet, Task<T>> work, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        try
        {
            if (accurate)
            {
                await _accurate.WaitAsync(ct).ConfigureAwait(false);
            }

            Shared? held = null;
            try
            {
                held = Take(accurate);

                // Off the caller's thread: the model call blocks its thread for the length of the call.
                var started = _busy?.Start() ?? 0;
                try
                {
                    return await Task.Run(() => work(held.Set), ct).ConfigureAwait(false);
                }
                finally
                {
                    _busy?.Stop(started);
                }
            }
            finally
            {
                if (held is not null)
                {
                    Return(held);
                }

                if (accurate)
                {
                    _accurate.Release();
                }
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private Shared Take(bool accurate)
    {
        var index = accurate ? 1 : 0;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sets[index] is { } set)
            {
                set.InUse++;
                return set;
            }
        }

        // Made outside the lock (a second or so): the calls of the other set go on.
        lock (_creating)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sets[index] is { } set)
                {
                    set.InUse++;
                    return set;
                }
            }

            var made = new Shared(_create(accurate));
            lock (_lock)
            {
                if (_disposed)
                {
                    made.Set.Dispose();
                    throw new ObjectDisposedException(GetType().Name);
                }

                made.InUse = 1;
                _sets[index] = made;
                return made;
            }
        }
    }

    /// <summary>Disposes the sets unused for <see cref="IdleTimeout"/>.</summary>
    internal void TrimIdle()
    {
        var now = _time.GetTimestamp();
        var old = new List<TSet>();
        lock (_lock)
        {
            for (var i = 0; i < _sets.Length; i++)
            {
                if (_sets[i] is { InUse: 0 } set && _time.GetElapsedTime(set.Since, now) >= IdleTimeout)
                {
                    old.Add(set.Set);
                    _sets[i] = null;
                }
            }
        }

        foreach (var set in old)
        {
            set.Dispose();
        }
    }

    private void Return(Shared held)
    {
        bool last;
        lock (_lock)
        {
            held.InUse--;
            held.Since = _time.GetTimestamp();
            last = _disposed && held.InUse == 0;
        }

        if (last)
        {
            held.Set.Dispose();
        }
    }

    /// <summary>
    /// Disposes an idle set now and a set in use when its last call ends. The semaphores stay (a call in flight still releases
    /// its slot); calls that get a slot afterwards end with <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        List<TSet> idle = [];
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = 0; i < _sets.Length; i++)
            {
                if (_sets[i] is { InUse: 0 } set)
                {
                    idle.Add(set.Set);
                }

                _sets[i] = null;
            }
        }

        _trim.Dispose();
        foreach (var set in idle)
        {
            set.Dispose();
        }
    }

    /// <summary>A session set with the calls that use it now.</summary>
    private sealed class Shared(TSet set)
    {
        public TSet Set { get; } = set;

        public int InUse { get; set; }

        public long Since { get; set; }
    }
}

/// <summary>
/// The CPU runner: each call through <see cref="CpuWorkers"/>; every line a call of its own, the calls at once.
/// </summary>
internal sealed class CpuRunner(CpuWorkers<ModelSet> workers, CharacterList characters, bool accurate) : IModelRunner
{
    public CharacterList Characters => characters;

    public ITextFinder Search => this;

    public bool SearchOnCard => false;

    public bool BatchTiles => false;

    public int DetectionSide(int pixels) => OnnxTextRecognizer.RoundUp32(pixels);

    public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) =>
        height * width >= Model.OcrLimits.LargeTextFinderPixels
            ? workers.RunLargeAsync(accurate, set => set.DetectAsync(input, count, height, width, ct), ct)
            : workers.RunAsync(accurate, set => set.DetectAsync(input, count, height, width, ct), ct);

    public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) =>
        workers.RunAsync(accurate, set => set.UpsideDownAsync(lines, ct), ct);

    public async Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        // Every line a call of its own: no padding to a wider neighbour, and the lines of a picture spread over the free workers.
        var calls = new Task<LineScores[]>[lines.Count];
        for (var i = 0; i < calls.Length; i++)
        {
            IReadOnlyList<SKBitmap> line = [lines[i]];
            calls[i] = workers.RunAsync(accurate, set => set.RecognizeAsync(line, ct), ct);
        }

        var parts = await Task.WhenAll(calls).ConfigureAwait(false);
        var result = new LineScores[calls.Length];
        for (var i = 0; i < calls.Length; i++)
        {
            result[i] = parts[i][0];
        }

        return result;
    }
}
