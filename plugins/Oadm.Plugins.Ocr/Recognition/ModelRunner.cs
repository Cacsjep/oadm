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

/// <summary>Runs the three models for one reading: the CPU workers, the text finder of Search on the graphics card when there is one.</summary>
internal interface IModelRunner : ITextFinder
{
    /// <summary>The reader's character list.</summary>
    CharacterList Characters { get; }

    /// <summary>The text finder of an area read in tiles (Search over a large area): the graphics card's when it is used.</summary>
    ITextFinder Search { get; }

    /// <summary>True when <see cref="Search"/> is the graphics card (an area's reading on the card is never reused on the CPU).</summary>
    bool SearchOnCard { get; }

    /// <summary>The probability that each line is upside down.</summary>
    Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct);

    /// <summary>The reader's output of each line.</summary>
    Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct);
}

/// <summary>
/// The CPU workers (docs/spec/ocr.md rule 4): every model call takes one of <see cref="Model.OcrLimits.Workers"/> slots and a
/// session set of its own (<see cref="Model.OcrLimits.ThreadsPerWorker"/> threads). The tiles of one picture and its batches
/// of lines are separate calls, so an otherwise idle server reads one picture on several workers at once, and many readers
/// share the same slots fairly. A set unused for <see cref="IdleTimeout"/> is disposed (its memory goes back); a set still in
/// use when the workers are disposed is disposed when its call ends, never while it runs.
/// </summary>
internal sealed class CpuWorkers<TSet> : IDisposable
    where TSet : class, IDisposable
{
    /// <summary>How long an unused session set is kept.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _accurate;
    private readonly Func<bool, TSet> _create;
    private readonly BusyMeter? _busy;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<(TSet Set, long Since)> _idle = [];
    private readonly List<(TSet Set, long Since)> _idleAccurate = [];
    private readonly ITimer _trim;
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

    /// <summary>Session sets kept for the next calls (tests: the idle trimming).</summary>
    public int Idle
    {
        get
        {
            lock (_lock)
            {
                return _idle.Count + _idleAccurate.Count;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on a free slot with a set of its own.</summary>
    /// <exception cref="ObjectDisposedException">The workers were disposed.</exception>
    public async Task<T> RunAsync<T>(bool accurate, Func<TSet, Task<T>> work, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (accurate)
            {
                await _accurate.WaitAsync(ct).ConfigureAwait(false);
            }

            TSet? set = null;
            try
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    // The most recently used set: the others age out.
                    var idle = accurate ? _idleAccurate : _idle;
                    if (idle.Count > 0)
                    {
                        set = idle[^1].Set;
                        idle.RemoveAt(idle.Count - 1);
                    }
                }

                set ??= _create(accurate);

                // Off the caller's thread: the model call blocks its thread for the length of the call.
                var taken = set;
                var started = _busy?.Start() ?? 0;
                try
                {
                    return await Task.Run(() => work(taken), ct).ConfigureAwait(false);
                }
                finally
                {
                    _busy?.Stop(started);
                }
            }
            finally
            {
                if (set is not null)
                {
                    Return(set, accurate);
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

    /// <summary>Disposes the sets unused for <see cref="IdleTimeout"/>.</summary>
    internal void TrimIdle()
    {
        var now = _time.GetTimestamp();
        var old = new List<TSet>();
        lock (_lock)
        {
            foreach (var idle in (List<(TSet Set, long Since)>[])[_idle, _idleAccurate])
            {
                old.AddRange(idle.Where(e => _time.GetElapsedTime(e.Since, now) >= IdleTimeout).Select(e => e.Set));
                idle.RemoveAll(e => _time.GetElapsedTime(e.Since, now) >= IdleTimeout);
            }
        }

        foreach (var set in old)
        {
            set.Dispose();
        }
    }

    private void Return(TSet set, bool accurate)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                (accurate ? _idleAccurate : _idle).Add((set, _time.GetTimestamp()));
                return;
            }
        }

        set.Dispose();
    }

    /// <summary>
    /// Disposes the idle sets now and every set in use when its call ends. The semaphores stay (a call in flight still releases
    /// its slot); calls that get a slot afterwards end with <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        List<TSet> sets;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sets = [.. _idle.Select(e => e.Set), .. _idleAccurate.Select(e => e.Set)];
            _idle.Clear();
            _idleAccurate.Clear();
        }

        _trim.Dispose();
        foreach (var set in sets)
        {
            set.Dispose();
        }
    }
}

/// <summary>
/// The CPU runner: each call through <see cref="CpuWorkers"/>; lines in pairs of similar width, the pairs at once. With
/// <paramref name="search"/> the text finder of areas read in tiles runs there (the graphics card).
/// </summary>
internal sealed class CpuRunner(CpuWorkers<ModelSet> workers, CharacterList characters, bool accurate, CardTextFinder? search = null) : IModelRunner
{
    public CharacterList Characters => characters;

    public ITextFinder Search => search ?? (ITextFinder)this;

    public bool SearchOnCard => search is not null;

    public bool BatchTiles => false;

    public int DetectionSide(int pixels) => OnnxTextRecognizer.RoundUp32(pixels);

    public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) =>
        workers.RunAsync(accurate, set => set.DetectAsync(input, count, height, width, ct), ct);

    public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) =>
        workers.RunAsync(accurate, set => set.UpsideDownAsync(lines, ct), ct);

    public async Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        // Pairs of lines of similar width (little padding), each pair a call of its own: the lines of a picture spread over the
        // free workers (a reader with 4 areas: 147 to 87 ms on 24 cores).
        var order = Enumerable.Range(0, lines.Count).OrderBy(i => lines[i].Width).ToArray();
        const int Batch = 2;
        var calls = new List<Task<LineScores[]>>();
        for (var start = 0; start < order.Length; start += Batch)
        {
            var chunk = order.Skip(start).Take(Batch).Select(i => lines[i]).ToList();
            calls.Add(workers.RunAsync(accurate, set => set.RecognizeAsync(chunk, ct), ct));
        }

        var parts = await Task.WhenAll(calls).ConfigureAwait(false);
        var result = new LineScores[lines.Count];
        var k = 0;
        foreach (var part in parts)
        {
            foreach (var scores in part)
            {
                result[order[k++]] = scores;
            }
        }

        return result;
    }
}

/// <summary>
/// The graphics card's text finder (docs/spec/ocr.md "GPU"): the server text finder for Search, the areas read in tiles. One
/// call at a time (the card's time counted in its <see cref="BusyMeter"/>); on DirectML each input size is a session of its own
/// in the <see cref="GpuSessionBudget"/> (counted by its pixels, capped), the sides in steps and the tiles of an area in calls
/// of one size that fits <see cref="GpuSessionBudget.MaxCallBytes"/>. Reader and angle model run on the CPU workers.
/// </summary>
internal sealed class CardTextFinder : ITextFinder, IDisposable
{
    private static readonly int[] Steps = [256, 512, 768, 1024];

    private readonly ShapeSessions _sessions;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly BusyMeter _busy;
    private readonly ExecutionTarget _target;
    private readonly long _maxCallBytes;

    /// <param name="busy">Counts the time each call holds the card.</param>
    /// <param name="budget">DirectML: the card's sessions (required there); CUDA: unused.</param>
    public CardTextFinder(string modelPath, ExecutionTarget target, BusyMeter busy, GpuSessionBudget? budget)
    {
        _target = target;
        _busy = busy;
        _maxCallBytes = (budget?.Max ?? GpuSessionBudget.MaxBytes) / 2;
        _sessions = new ShapeSessions(modelPath, target, OcrLimits.ThreadsPerWorker, budget);
    }

    public bool BatchTiles => true;

    /// <summary>On DirectML each input size is a session of its own: sides in four steps.</summary>
    public int DetectionSide(int pixels) => _target == ExecutionTarget.DirectMl ? SteppedSide(pixels) : OnnxTextRecognizer.RoundUp32(pixels);

    /// <summary>The DirectML side for <paramref name="pixels"/>: 256, 512, 768 or 1024 (tiles are at most 960).</summary>
    internal static int SteppedSide(int pixels) => Steps.FirstOrDefault(s => s >= pixels, OnnxTextRecognizer.RoundUp32(pixels));

    public int TilesPerCall(int tiles, int tileWidth, int tileHeight) => GpuSessionBudget.TilesPerCall(tiles, tileWidth, tileHeight, _maxCallBytes);

    public async Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        var started = _busy.Start();
        try
        {
            return ModelSet.Timed(
                () => _sessions.Run(input.Array, [count, 3, height, width], static (output, _) =>
                {
                    var map = PooledFloats.Rent(output.Length, clear: false);
                    output.CopyTo(map.Span);
                    return map;
                }),
                Stage.DetectModel);
        }
        finally
        {
            _busy.Stop(started);
            _serial.Release();
        }
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _serial.Dispose();
    }
}
