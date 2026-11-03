using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>Runs the three models for one reading: on the CPU through the worker gate, on a graphics card through its shared set.</summary>
internal interface IModelRunner
{
    /// <summary>The reader's character list.</summary>
    CharacterList Characters { get; }

    /// <summary>True when all tiles of a picture go in one call (a graphics card); else one call per tile.</summary>
    bool BatchTiles { get; }

    /// <summary>The side of the text finder's input for an area of <paramref name=pixels/> (a multiple of 32, on DirectML in a few steps).</summary>
    int DetectionSide(int pixels);

    /// <summary>The batch of a text finder call for <paramref name="count"/> tiles (DirectML: in steps, the rest blank).</summary>
    int TileBatch(int count) => count;

    /// <summary>The text finder's maps of <paramref name="count"/> pictures of one size ([count, height, width]), rented: the caller disposes them.</summary>
    Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct);

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
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<(TSet Set, long Since)> _idle = [];
    private readonly List<(TSet Set, long Since)> _idleAccurate = [];
    private readonly ITimer _trim;
    private bool _disposed;

    /// <param name="slots">Model calls at once.</param>
    /// <param name="accurateSets">Calls with the accurate (large) models at once, within <paramref name="slots"/>.</param>
    /// <param name="create">Makes a session set: the accurate models when true.</param>
    /// <param name="time">The clock of the idle trimming (tests: a fake one).</param>
    public CpuWorkers(int slots, int accurateSets, Func<bool, TSet> create, TimeProvider? time = null)
    {
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
                return await Task.Run(() => work(taken), ct).ConfigureAwait(false);
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

/// <summary>The CPU runner: each call through <see cref="CpuWorkers"/>; lines in pairs of similar width, the pairs at once.</summary>
internal sealed class CpuRunner(CpuWorkers<ModelSet> workers, CharacterList characters, bool accurate) : IModelRunner
{
    public CharacterList Characters => characters;

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

/// <summary>The graphics card runner: one shared set, model calls one at a time; lines batched across readers.</summary>
internal sealed class GpuRunner(ModelSet set, LineBatcher lines) : IModelRunner
{
    public CharacterList Characters => set.Characters;

    public bool BatchTiles => true;

    /// <summary>On DirectML each input size is a session of its own: areas and tiles in four steps.</summary>
    public int DetectionSide(int pixels) =>
        set.Target == ExecutionTarget.DirectMl ? Steps.FirstOrDefault(s => s >= pixels, OnnxTextRecognizer.RoundUp32(pixels)) : OnnxTextRecognizer.RoundUp32(pixels);

    /// <summary>On DirectML the tiles of a call in steps.</summary>
    public int TileBatch(int count) =>
        set.Target == ExecutionTarget.DirectMl ? TileBatches.FirstOrDefault(b => b >= count, count) : count;

    private static readonly int[] Steps = [256, 512, 768, 1024];

    private static readonly int[] TileBatches = [1, 2, 4, 6];

    public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) =>
        set.DetectAsync(input, count, height, width, ct);

    public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> list, CancellationToken ct) => set.UpsideDownAsync(list, ct);

    public Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> list, CancellationToken ct) => lines.RecognizeAsync(list, ct);
}
