using System.Collections.Concurrent;
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

    /// <summary>The text finder's maps of <paramref name="count"/> pictures of one size ([count, height, width]).</summary>
    Task<float[]> DetectAsync(float[] input, int count, int height, int width, CancellationToken ct);

    /// <summary>The probability that each line is upside down.</summary>
    Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct);

    /// <summary>The reader's output of each line.</summary>
    Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct);
}

/// <summary>
/// The CPU workers (docs/spec/ocr.md rule 4): every model call takes one of <see cref="Model.OcrLimits.Workers"/> slots and a
/// session set of its own (<see cref="Model.OcrLimits.ThreadsPerWorker"/> threads). The tiles of one picture and its batches
/// of lines are separate calls, so an otherwise idle server reads one picture on several workers at once, and many readers
/// share the same slots fairly.
/// </summary>
internal sealed class CpuWorkers<TSet> : IDisposable
    where TSet : class, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _accurate;
    private readonly Func<bool, TSet> _create;
    private readonly BusyMeter? _busy;
    private readonly ConcurrentBag<TSet> _idle = [];
    private readonly ConcurrentBag<TSet> _idleAccurate = [];

    /// <param name="slots">Model calls at once.</param>
    /// <param name="accurateSets">Calls with the accurate (large) models at once, within <paramref name="slots"/>.</param>
    /// <param name="create">Makes a session set: the accurate models when true.</param>
    /// <param name="busy">Counts the time each call holds its slot (the recognition load); null: not measured.</param>
    public CpuWorkers(int slots, int accurateSets, Func<bool, TSet> create, BusyMeter? busy = null)
    {
        _busy = busy;
        _slots = new SemaphoreSlim(slots, slots);
        _accurate = new SemaphoreSlim(accurateSets, accurateSets);
        _create = create;
        Slots = slots;
    }

    /// <summary>Model calls at once.</summary>
    public int Slots { get; }

    /// <summary>Slots in use now (tests: the gate holds).</summary>
    public int Busy => Slots - _slots.CurrentCount;

    /// <summary>Runs <paramref name="work"/> on a free slot with a set of its own.</summary>
    public async Task<T> RunAsync<T>(bool accurate, Func<TSet, Task<T>> work, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (accurate)
            {
                await _accurate.WaitAsync(ct).ConfigureAwait(false);
            }

            var idle = accurate ? _idleAccurate : _idle;
            TSet? set = null;
            try
            {
                if (!idle.TryTake(out set))
                {
                    set = _create(accurate);
                }

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
                    idle.Add(set);
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

    public void Dispose()
    {
        while (_idle.TryTake(out var set))
        {
            set.Dispose();
        }

        while (_idleAccurate.TryTake(out var set))
        {
            set.Dispose();
        }

        _slots.Dispose();
        _accurate.Dispose();
    }
}

/// <summary>The CPU runner: each call through <see cref="CpuWorkers"/>; lines in batches of the set's size, the batches at once.</summary>
internal sealed class CpuRunner(CpuWorkers<ModelSet> workers, CharacterList characters, bool accurate) : IModelRunner
{
    public CharacterList Characters => characters;

    public bool BatchTiles => false;

    public int DetectionSide(int pixels) => OnnxTextRecognizer.RoundUp32(pixels);

    public Task<float[]> DetectAsync(float[] input, int count, int height, int width, CancellationToken ct) =>
        workers.RunAsync(accurate, set => set.DetectAsync(input, count, height, width, ct), ct);

    public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) =>
        workers.RunAsync(accurate, set => set.UpsideDownAsync(lines, ct), ct);

    public async Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        // Batches of lines of similar width, each batch a call of its own (several workers when they are free).
        var order = Enumerable.Range(0, lines.Count).OrderBy(i => lines[i].Width).ToArray();
        const int Batch = 8;
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

    /// <summary>On DirectML each input size is a session of its own: areas in four steps.</summary>
    public int DetectionSide(int pixels) =>
        set.Target == ExecutionTarget.DirectMl ? Steps.FirstOrDefault(s => s >= pixels, OnnxTextRecognizer.RoundUp32(pixels)) : OnnxTextRecognizer.RoundUp32(pixels);

    private static readonly int[] Steps = [256, 512, 768, 1024];

    public Task<float[]> DetectAsync(float[] input, int count, int height, int width, CancellationToken ct) =>
        set.DetectAsync(input, count, height, width, ct);

    public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> list, CancellationToken ct) => set.UpsideDownAsync(list, ct);

    public Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> list, CancellationToken ct) => lines.RecognizeAsync(list, ct);
}
