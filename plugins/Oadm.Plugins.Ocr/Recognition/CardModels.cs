using System.Diagnostics;
using Oadm.Plugins.Ocr.Model;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>Which models run on the graphics card: those that pass a test call and are not clearly slower than the CPU.</summary>
/// <param name="Finder">The text finder.</param>
/// <param name="Reader">The reader.</param>
/// <param name="Angle">The angle model.</param>
internal readonly record struct CardRoute(bool Finder, bool Reader, bool Angle)
{
    /// <summary>A card may take this many times the CPU's time and still run the model: the CPU stays free.</summary>
    public const double MostSlower = 1.5;

    /// <summary>Every model.</summary>
    public static CardRoute All { get; } = new(true, true, true);

    /// <summary>At least one model runs on the card.</summary>
    public bool Any => Finder || Reader || Angle;

    /// <summary>The card's test call in <paramref name="cardMs"/> runs the model there (null: the call failed).</summary>
    public static bool Worth(double? cardMs, double cpuMs) => cardMs is { } ms && ms <= cpuMs * MostSlower;
}

/// <summary>A graphics card that can fail during a run (<see cref="CardRunner"/>).</summary>
internal interface ICardFailure
{
    /// <summary>A call failed during a run: the readings go back to the CPU.</summary>
    bool Failed { get; }

    /// <summary>The first failure ends the card's use; true for the caller that tells the log.</summary>
    bool MarkFailed();
}

/// <summary>
/// The models on the graphics card (docs/spec/ocr.md "GPU"). Two lanes run one call at a time each, the text finder and angle
/// model on one and the reader on the other (measured: one lane is busy 89 % of the time with the card busy 48 %); what waits
/// meanwhile goes into the next call, so the lines and pictures of several readers share calls and the card's time. On DirectML
/// each input size is a session of its own (<see cref="GpuSessionBudget"/>), so calls come in few sizes: lines padded to
/// <see cref="LineWidths"/> in calls of <see cref="LineBatches"/> lines, the text finder at each area's size, 1 or
/// <see cref="FinderBatch"/> pictures. CUDA takes the same calls in one session.
/// </summary>
internal sealed class CardModels : ITextFinder, ICardFailure, IDisposable
{
    /// <summary>
    /// The line widths of the reader's calls, about 1.25 apart: a line loses readings when padded much wider (moving labels with
    /// 4 px blur: steps of 2 read 19 of 25 values, 1.4 read 24, 1.25 all 25 like one call per line on the CPU).
    /// </summary>
    internal static readonly int[] LineWidths = [128, 160, 200, 256, 320, 400, 512, 640, 800, 1024, 1280, 1600, ModelSet.MaxLineWidth];

    /// <summary>Lines per reader call (the rest of a call stays blank, which changes no reading): few sizes, few sessions.</summary>
    internal static readonly int[] LineBatches = [4, 16];

    /// <summary>Lines per angle call.</summary>
    internal static readonly int[] AngleBatches = [1, 16];

    /// <summary>Pictures per text finder call when more than one waits: 4 take twice the time of 1 (measured).</summary>
    internal const int FinderBatch = 4;

    /// <summary>Lines waiting at most for <see cref="HasRoom"/>: more than one full call.</summary>
    private const int RoomLines = 32;

    private static readonly int[] Steps = [256, 512, 768, 1024];

    private readonly ShapeSessions _finder;
    private readonly ShapeSessions _serverFinder;
    private readonly ShapeSessions _reader;
    private readonly ShapeSessions _angle;
    private readonly BusyMeter _finderBusy;
    private readonly BusyMeter _readerBusy;
    private readonly ExecutionTarget _target;
    private readonly long _maxCallBytes;
    private readonly GpuSessionBudget? _budget;
    private readonly object _lock = new();
    private readonly List<LineJob> _lines = [];
    private readonly List<AngleJob> _angles = [];
    private readonly List<FinderJob> _finders = [];
    private readonly SemaphoreSlim _wakeFinder = new(0);
    private readonly SemaphoreSlim _wakeReader = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _finderThread;
    private readonly Thread _readerThread;
    private long _order;
    private volatile bool _failed;

    /// <param name="finder">The text finder of fixed-place areas (mobile).</param>
    /// <param name="serverFinder">The text finder of Search (server).</param>
    /// <param name="reader">The reader.</param>
    /// <param name="angle">The angle model.</param>
    /// <param name="target">The provider.</param>
    /// <param name="finderBusy">Counts the time of the text finder and angle lane.</param>
    /// <param name="readerBusy">Counts the time of the reader lane.</param>
    /// <param name="budget">DirectML: the card's sessions (required there); CUDA: unused.</param>
    public CardModels(string finder, string serverFinder, string reader, string angle, ExecutionTarget target, BusyMeter finderBusy, BusyMeter readerBusy, GpuSessionBudget? budget)
    {
        _target = target;
        _finderBusy = finderBusy;
        _readerBusy = readerBusy;
        _maxCallBytes = (budget?.Max ?? GpuSessionBudget.MaxBytes) / 2;
        _finder = new ShapeSessions(finder, target, OcrLimits.ThreadsPerWorker, budget, GpuSessionBudget.EstimateFinder);
        _serverFinder = new ShapeSessions(serverFinder, target, OcrLimits.ThreadsPerWorker, budget);
        _reader = new ShapeSessions(reader, target, OcrLimits.ThreadsPerWorker, budget, GpuSessionBudget.EstimateReader);
        _angle = new ShapeSessions(angle, target, OcrLimits.ThreadsPerWorker, budget, GpuSessionBudget.EstimateReader);
        _budget = budget;
        _finderThread = new Thread(() => Run(_wakeFinder, NextFinder)) { IsBackground = true, Name = "Text recognition card: text finder" };
        _readerThread = new Thread(() => Run(_wakeReader, NextLines)) { IsBackground = true, Name = "Text recognition card: reader" };
        _finderThread.Start();
        _readerThread.Start();
    }

    /// <summary>A call failed during a run (a driver reset): the readings go back to the CPU.</summary>
    public bool Failed => _failed;

    /// <summary>The first failure ends the card's use; true for the caller that tells the log.</summary>
    public bool MarkFailed()
    {
        var first = !_failed;
        _failed = true;
        return first;
    }

    /// <summary>The card can take another picture now: no text finder call and at most a call of lines wait.</summary>
    public bool HasRoom
    {
        get
        {
            lock (_lock)
            {
                return _finders.Count < 2 && _lines.Count <= RoomLines;
            }
        }
    }

    /// <summary>
    /// A text finder size has its session or room for one: a reader with many area sizes never makes the card build sessions at
    /// every picture; a size beyond that reads on the CPU.
    /// </summary>
    public bool FinderFits(int height, int width) => _budget is null || _budget.Fits(_finder, $"1x3x{height}x{width}", GpuSessionBudget.EstimateFinder((long)height * width));

    /// <summary>Tiles of Search go in calls of their own size.</summary>
    public bool BatchTiles => true;

    /// <summary>Search's tiles on DirectML: each input size is a session of its own, sides in four steps; a tile holds picture to its edges.</summary>
    public int DetectionSide(int pixels) => _target == ExecutionTarget.DirectMl ? SteppedSide(pixels) : OnnxTextRecognizer.RoundUp32(pixels);

    /// <summary>The DirectML side for <paramref name="pixels"/>: 256, 512, 768 or 1024 (tiles are at most 960).</summary>
    internal static int SteppedSide(int pixels) => Steps.FirstOrDefault(s => s >= pixels, OnnxTextRecognizer.RoundUp32(pixels));

    public int TilesPerCall(int tiles, int tileWidth, int tileHeight) => GpuSessionBudget.TilesPerCall(tiles, tileWidth, tileHeight, _maxCallBytes);

    /// <summary>The server text finder (Search), one call as given.</summary>
    public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) =>
        Enqueue(new FinderJob(input, count, height, width, Server: true, ct));

    /// <summary>The mobile text finder of a fixed-place area; pictures of one size that wait share a call.</summary>
    public Task<PooledFloats> FindAsync(PooledFloats input, int height, int width, CancellationToken ct) =>
        Enqueue(new FinderJob(input, 1, height, width, Server: false, ct));

    private Task<PooledFloats> Enqueue(FinderJob job)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            job.Order = ++_order;
            _finders.Add(job);
        }

        _wakeFinder.Release();
        return job.Done.Task;
    }

    /// <summary>The reader's output of each line, in the order given; the lines stay valid until the task ends.</summary>
    public async Task<LineScores[]> ReadAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        var jobs = new LineJob[lines.Count];
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            for (var i = 0; i < jobs.Length; i++)
            {
                jobs[i] = new LineJob(lines[i], LineWidth(lines[i].Width), ct) { Order = ++_order };
                _lines.Add(jobs[i]);
            }
        }

        _wakeReader.Release();
        return await Task.WhenAll(jobs.Select(j => j.Done.Task)).ConfigureAwait(false);
    }

    /// <summary>The probability that each line is upside down (0 to 1).</summary>
    public async Task<float[]> AngleAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        var jobs = new AngleJob[lines.Count];
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            for (var i = 0; i < jobs.Length; i++)
            {
                jobs[i] = new AngleJob(lines[i], ct) { Order = ++_order };
                _angles.Add(jobs[i]);
            }
        }

        _wakeFinder.Release();
        return await Task.WhenAll(jobs.Select(j => j.Done.Task)).ConfigureAwait(false);
    }

    /// <summary>The call's width for a line of <paramref name="width"/> pixels.</summary>
    internal static int LineWidth(int width)
    {
        foreach (var candidate in LineWidths)
        {
            if (candidate >= width)
            {
                return candidate;
            }
        }

        return ModelSet.MaxLineWidth;
    }

    /// <summary>The smallest of <paramref name="batches"/> that holds <paramref name="count"/>; the largest when none does.</summary>
    internal static int BatchFor(int count, int[] batches)
    {
        foreach (var batch in batches)
        {
            if (batch >= count)
            {
                return batch;
            }
        }

        return batches[^1];
    }

    /// <summary>
    /// What the test call of each model says (docs/spec/ocr.md "GPU"): a model runs on the card when the call passes and is not
    /// clearly slower than the same work on the CPU. Real sessions: only at the card's start, never in a unit test.
    /// </summary>
    public CardRoute Probe(string finderPath, string readerPath, string anglePath, Action<string, string> failed)
    {
        double? Card(string model, Action call)
        {
            try
            {
                call();
                var watch = Stopwatch.StartNew();
                call();
                return watch.Elapsed.TotalMilliseconds;
            }
#pragma warning disable CA1031 // Any failure of a provider on a model leaves that model on the CPU.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failed(model, ex.Message);
                return null;
            }
        }

        static double Cpu(string path, float[] input, long[] shape, int times)
        {
            using var sessions = new ShapeSessions(path, ExecutionTarget.Cpu, OcrLimits.ThreadsPerWorker);
            sessions.Run(input, shape, static (_, _) => true);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < times; i++)
            {
                sessions.Run(input, shape, static (_, _) => true);
            }

            return watch.Elapsed.TotalMilliseconds;
        }

        var picture = new float[3 * 512 * 512];
        var lines = new float[8 * 3 * ModelSet.LineHeight * 320];
        var line = new float[3 * ModelSet.LineHeight * 320];
        var angles = new float[8 * 3 * ModelSet.OrientationHeight * ModelSet.OrientationWidth];
        return new CardRoute(
            CardRoute.Worth(Card("the text finder", () => _finder.Run(picture, [1, 3, 512, 512], static (_, _) => true)), Cpu(finderPath, picture, [1, 3, 512, 512], 1)),
            CardRoute.Worth(Card("the reader", () => _reader.Run(lines, [8, 3, ModelSet.LineHeight, 320], static (_, _) => true)), Cpu(readerPath, line, [1, 3, ModelSet.LineHeight, 320], 8)),
            CardRoute.Worth(Card("the angle model", () => _angle.Run(angles, [8, 3, ModelSet.OrientationHeight, ModelSet.OrientationWidth], static (_, _) => true)), Cpu(anglePath, angles, [8, 3, ModelSet.OrientationHeight, ModelSet.OrientationWidth], 1)));
    }

    private void Run(SemaphoreSlim wake, Func<Action?> next)
    {
        try
        {
            while (true)
            {
                wake.Wait(_stop.Token);
                while (next() is { } call)
                {
                    call();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: Dispose ends what still waits.
        }
    }

    /// <summary>The next call of the text finder's lane: the oldest work with what fits beside it.</summary>
    private Action? NextFinder()
    {
        lock (_lock)
        {
            if (_finders.Count == 0 && _angles.Count == 0)
            {
                return null;
            }

            if (_angles.Count == 0 || (_finders.Count > 0 && _finders[0].Order < _angles[0].Order))
            {
                var first = _finders[0];
                var group = first.Server
                    ? [first]
                    : _finders.Where(f => !f.Server && f.Height == first.Height && f.Width == first.Width).Take(FinderBatch).ToList();
                _finders.RemoveAll(group.Contains);
                return () => RunFinders(group);
            }

            var turned = _angles.Take(AngleBatches[^1]).ToList();
            _angles.RemoveAll(turned.Contains);
            return () => RunAngles(turned);
        }
    }

    /// <summary>The next reader call: the oldest line with the waiting lines of its width.</summary>
    private Action? NextLines()
    {
        lock (_lock)
        {
            if (_lines.Count == 0)
            {
                return null;
            }

            var width = _lines[0].Width;
            var group = _lines.Where(l => l.Width == width).Take(LineBatches[^1]).ToList();
            _lines.RemoveAll(group.Contains);
            return () => RunLines(group, width);
        }
    }

    private static T Timed<T>(BusyMeter busy, Func<T> call, Stage? stage, double filled = 1)
    {
        var started = busy.Start();
        try
        {
            return ModelSet.Timed(call, stage);
        }
        finally
        {
            busy.Stop(started, filled);
        }
    }

    private void RunFinders(List<FinderJob> jobs)
    {
        try
        {
            if (jobs is [{ Server: true } search])
            {
                search.Done.TrySetResult(Timed(
                    _finderBusy,
                    () => _serverFinder.Run(search.Input.Array, [search.Count, 3, search.Height, search.Width], static (output, _) =>
                    {
                        var map = PooledFloats.Rent(output.Length, clear: false);
                        output.CopyTo(map.Span);
                        return map;
                    }),
                    Stage.DetectModel));
                return;
            }

            var (height, width) = (jobs[0].Height, jobs[0].Width);
            var count = jobs.Count == 1 ? 1 : FinderBatch;
            var plane = 3 * height * width;
            using var together = PooledFloats.Rent(count * plane, clear: count > jobs.Count);
            for (var i = 0; i < jobs.Count; i++)
            {
                jobs[i].Input.Span[..plane].CopyTo(together.Span[(i * plane)..]);
            }

            var maps = Timed(
                    _finderBusy,
                () => _finder.Run(together.Array, [count, 3, height, width], (output, _) =>
                {
                    var size = height * width;
                    var result = new PooledFloats[jobs.Count];
                    for (var i = 0; i < result.Length; i++)
                    {
                        result[i] = PooledFloats.Rent(size, clear: false);
                        output.Slice(i * size, size).CopyTo(result[i].Span);
                    }

                    return result;
                }),
                Stage.DetectModel);
            for (var i = 0; i < jobs.Count; i++)
            {
                if (!jobs[i].Done.TrySetResult(maps[i]))
                {
                    maps[i].Dispose();
                }
            }
        }
#pragma warning disable CA1031 // The readings hear of it; their runner goes to the CPU.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            foreach (var job in jobs)
            {
                job.Done.TrySetException(ex);
            }
        }
    }

    private void RunLines(List<LineJob> jobs, int width)
    {
        try
        {
            var count = BatchFor(jobs.Count, LineBatches);
            var inputStart = Stopwatch.GetTimestamp();
            using var input = PooledFloats.Rent(count * 3 * ModelSet.LineHeight * width, clear: true);
            for (var i = 0; i < jobs.Count; i++)
            {
                if (!jobs[i].Token.IsCancellationRequested)
                {
                    Tensors.Write(jobs[i].Line, input.Array, i, ModelSet.LineHeight, width, Tensors.Symmetric);
                }
            }

            StageClock.Add(Stage.RecognizeInput, inputStart);
            var scores = Timed(
                _readerBusy,
                () => _reader.Run(input.Array, [count, 3, ModelSet.LineHeight, width], (output, shape) =>
                {
                    var frames = (int)shape[1];
                    var classes = (int)shape[2];
                    var result = new LineScores[jobs.Count];
                    for (var i = 0; i < result.Length; i++)
                    {
                        var own = Math.Min(frames, ((jobs[i].Line.Width + 7) / 8) + 2);
                        var kept = System.Buffers.ArrayPool<float>.Shared.Rent(own * classes);
                        output.Slice(i * frames * classes, own * classes).CopyTo(kept);
                        result[i] = new LineScores(kept, own, classes) { Rented = true };
                    }

                    return result;
                }),
                Stage.RecognizeModel,
                jobs.Count / (double)count);
            for (var i = 0; i < jobs.Count; i++)
            {
                jobs[i].Done.TrySetResult(scores[i]);
            }
        }
#pragma warning disable CA1031 // The readings hear of it; their runner goes to the CPU.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            foreach (var job in jobs)
            {
                job.Done.TrySetException(ex);
            }
        }
    }

    private void RunAngles(List<AngleJob> jobs)
    {
        try
        {
            var start = Stopwatch.GetTimestamp();
            var count = BatchFor(jobs.Count, AngleBatches);
            using var input = PooledFloats.Rent(count * 3 * ModelSet.OrientationHeight * ModelSet.OrientationWidth, clear: true);
            for (var i = 0; i < jobs.Count; i++)
            {
                using var small = jobs[i].Line.Resize(new SKImageInfo(ModelSet.OrientationWidth, ModelSet.OrientationHeight, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
                Tensors.Write(small, input.Array, i, ModelSet.OrientationHeight, ModelSet.OrientationWidth, Tensors.ImageNet);
            }

            var results = Timed(
                _finderBusy,
                () => _angle.Run(input.Array, [count, 3, ModelSet.OrientationHeight, ModelSet.OrientationWidth], (output, _) =>
                {
                    var upsideDown = new float[jobs.Count];
                    for (var i = 0; i < upsideDown.Length; i++)
                    {
                        upsideDown[i] = output[(i * 2) + 1];
                    }

                    return upsideDown;
                }),
                null);
            StageClock.Add(Stage.Angle, start);
            for (var i = 0; i < jobs.Count; i++)
            {
                jobs[i].Done.TrySetResult(results[i]);
            }
        }
#pragma warning disable CA1031 // The readings hear of it; their runner goes to the CPU.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            foreach (var job in jobs)
            {
                job.Done.TrySetException(ex);
            }
        }
    }

    /// <summary>Stops the card's thread after the call in work; what still waits ends disposed. Every reading is out by then.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
        }

        _finderThread.Join();
        _readerThread.Join();
        var gone = new ObjectDisposedException(nameof(CardModels));
        lock (_lock)
        {
            foreach (var job in _finders)
            {
                job.Done.TrySetException(gone);
            }

            foreach (var job in _lines)
            {
                job.Done.TrySetException(gone);
            }

            foreach (var job in _angles)
            {
                job.Done.TrySetException(gone);
            }
        }

        _finder.Dispose();
        _serverFinder.Dispose();
        _reader.Dispose();
        _angle.Dispose();
        _wakeFinder.Dispose();
        _wakeReader.Dispose();
        _stop.Dispose();
    }

    private sealed record FinderJob(PooledFloats Input, int Count, int Height, int Width, bool Server, CancellationToken Token)
    {
        public long Order { get; set; }

        public TaskCompletionSource<PooledFloats> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record LineJob(SKBitmap Line, int Width, CancellationToken Token)
    {
        public long Order { get; set; }

        public TaskCompletionSource<LineScores> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record AngleJob(SKBitmap Line, CancellationToken Token)
    {
        public long Order { get; set; }

        public TaskCompletionSource<float> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// The runner of a reading with a graphics card: each model on the card by its <see cref="CardRoute"/>, else on the CPU workers.
/// A card call that fails (a driver reset) ends the card's use for every reading: this call and the next run on the CPU.
/// </summary>
internal sealed class CardRunner(CpuRunner cpu, CardModels card, CardRoute route, Action<string> failed) : IModelRunner
{
    private readonly SearchFinder _search = new(cpu, card, route, failed);

    public CharacterList Characters => cpu.Characters;

    public ITextFinder Search => route.Finder ? _search : cpu;

    public bool SearchOnCard => route.Finder;

    public bool BatchTiles => false;

    /// <summary>
    /// A fixed-place area at the size the CPU reads it, a multiple of 32: a larger picture with the rest filled reads worse (small
    /// labels: 25 of 31 values at 768 x 768, 31 of 31 at 640 x 736), so each area size is a session of its own on DirectML.
    /// </summary>
    public int DetectionSide(int pixels) => cpu.DetectionSide(pixels);

    public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) => route.Finder && card.FinderFits(height, width)
        ? Guarded(() => card.FindAsync(input, height, width, ct), () => cpu.DetectAsync(input, count, height, width, ct), card, failed, ct)
        : cpu.DetectAsync(input, count, height, width, ct);

    public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) => route.Angle
        ? Guarded(() => card.AngleAsync(lines, ct), () => cpu.UpsideDownAsync(lines, ct), card, failed, ct)
        : cpu.UpsideDownAsync(lines, ct);

    public Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) => route.Reader
        ? Guarded(() => card.ReadAsync(lines, ct), () => cpu.RecognizeAsync(lines, ct), card, failed, ct)
        : cpu.RecognizeAsync(lines, ct);

    internal static async Task<T> Guarded<T>(Func<Task<T>> onCard, Func<Task<T>> onCpu, ICardFailure card, Action<string> failed, CancellationToken ct)
    {
        if (!card.Failed)
        {
            try
            {
                return await onCard().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (card.MarkFailed())
                {
                    failed(ex.Message);
                }
            }
        }

        return await onCpu().ConfigureAwait(false);
    }

    /// <summary>Search's text finder: on the card in calls of its own size, the same fallback.</summary>
    private sealed class SearchFinder(CpuRunner cpu, CardModels card, CardRoute route, Action<string> failed) : ITextFinder
    {
        public bool BatchTiles => route.Finder;

        public int DetectionSide(int pixels) => card.DetectionSide(pixels);

        public int TilesPerCall(int tiles, int tileWidth, int tileHeight) => card.TilesPerCall(tiles, tileWidth, tileHeight);

        public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) =>
            Guarded(() => card.DetectAsync(input, count, height, width, ct), () => cpu.DetectAsync(input, count, height, width, ct), card, failed, ct);
    }
}
