using System.Diagnostics;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>Which model files a set uses.</summary>
/// <param name="Detection">The text finder.</param>
/// <param name="Recognition">The reader.</param>
/// <param name="Characters">The reader's character list.</param>
internal sealed record ModelChoice(string Detection, string Recognition, string Characters)
{
    /// <summary>The mobile models (the CPU).</summary>
    public static ModelChoice Mobile { get; } = new(ModelFiles.DetectionMobile, ModelFiles.RecognitionLatin, ModelFiles.RecognitionLatinCharacters);

    /// <summary>The accurate models (a graphics card, or Accurate on the CPU).</summary>
    public static ModelChoice Server { get; } = new(ModelFiles.DetectionServer, ModelFiles.RecognitionLatin, ModelFiles.RecognitionLatinCharacters);
}

/// <summary>
/// One set of sessions: text finder, angle model and reader with its character list. A CPU worker owns its set; the graphics
/// card's set is shared and runs one model call at a time.
/// </summary>
internal sealed class ModelSet : IDisposable
{
    /// <summary>The reader's input height.</summary>
    public const int LineHeight = 48;

    /// <summary>The narrowest reader input (PaddleOCR pads shorter lines to it).</summary>
    public const int MinLineWidth = 320;

    /// <summary>The widest reader input; longer lines are squeezed.</summary>
    public const int MaxLineWidth = 3200;

    /// <summary>The angle model's input.</summary>
    public const int OrientationWidth = 160;

    /// <summary>The angle model's input.</summary>
    public const int OrientationHeight = 80;

    /// <summary>On DirectML the reader's widths in steps (each width is a session of its own there).</summary>
    private static readonly int[] DirectMlWidths = [320, 640, 1280, MaxLineWidth];

    /// <summary>On DirectML the batches of lines (angle model and reader) in steps.</summary>
    private static readonly int[] DirectMlBatches = [8, 32];

    private readonly ShapeSessions _detection;
    private readonly ShapeSessions _recognition;
    private readonly ShapeSessions _orientation;
    private readonly SemaphoreSlim? _serial;
    private readonly BusyMeter? _busy;
    private readonly GpuSessionBudget? _ownBudget;

    /// <param name="busy">On a graphics card: counts the time each call holds the card (the recognition load); the CPU's
    /// sets are measured by their worker slots.</param>
    /// <param name="budget">On DirectML: the sessions on the card of all three models together; null: a budget of its own.</param>
    public ModelSet(string folder, ModelChoice choice, ExecutionTarget target, int threads, BusyMeter? busy = null, GpuSessionBudget? budget = null)
    {
        _busy = target == ExecutionTarget.Cpu ? null : busy;
        Target = target;
        Characters = CharacterList.Load(Path.Combine(folder, choice.Characters));
        if (target == ExecutionTarget.DirectMl && budget is null)
        {
            budget = _ownBudget = new GpuSessionBudget();
        }

        Budget = target == ExecutionTarget.DirectMl ? budget : null;
        _detection = new ShapeSessions(Path.Combine(folder, choice.Detection), target, threads, Budget, GpuSessionBudget.MaxDetectionSessions);
        _recognition = new ShapeSessions(Path.Combine(folder, choice.Recognition), target, threads, Budget);
        _orientation = new ShapeSessions(Path.Combine(folder, ModelFiles.Orientation), target, threads, Budget);
        _serial = target == ExecutionTarget.Cpu ? null : new SemaphoreSlim(1, 1);
    }

    /// <summary>On DirectML: the set's sessions on the card; else null.</summary>
    public GpuSessionBudget? Budget { get; }

    /// <summary>Where it runs.</summary>
    public ExecutionTarget Target { get; }

    /// <summary>The reader's character list.</summary>
    public CharacterList Characters { get; }

    /// <summary>Lines per reader call.</summary>
    public int RecognitionBatch => Target == ExecutionTarget.Cpu ? 8 : 32;

    /// <summary>On DirectML every call has a fixed batch and the widths come in steps: few input sizes, each fast.</summary>
    private bool FixedSizes => Target == ExecutionTarget.DirectMl;

    /// <summary>
    /// The text finder's probability maps of <paramref name="count"/> pictures of one size, [count, height, width], in a rented
    /// buffer the caller disposes.
    /// </summary>
    public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct) =>
        RunAsync(
            () => _detection.Run(input.Array, [count, 3, height, width], static (output, _) =>
            {
                var map = PooledFloats.Rent(output.Length, clear: false);
                output.CopyTo(map.Span);
                return map;
            }),
            Stage.DetectModel,
            ct);

    /// <summary>The probability that each line is upside down (0 to 1).</summary>
    public async Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        var angleStart = Stopwatch.GetTimestamp();
        var result = new float[lines.Count];
        const int Batch = 32;
        for (var start = 0; start < lines.Count; start += Batch)
        {
            var count = Math.Min(Batch, lines.Count - start);
            var size = FixedSizes ? DirectMlBatches.First(b => b >= count) : count;
            using var input = PooledFloats.Rent(size * 3 * OrientationHeight * OrientationWidth, clear: size > count);
            for (var i = 0; i < count; i++)
            {
                using var small = lines[start + i].Resize(new SKImageInfo(OrientationWidth, OrientationHeight, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
                Tensors.Write(small, input.Array, i, OrientationHeight, OrientationWidth, Tensors.ImageNet);
            }

            var first = start;
            await RunAsync(
                () => _orientation.Run(input.Array, [size, 3, OrientationHeight, OrientationWidth], (output, _) =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        result[first + i] = output[i * 2 + 1];
                    }

                    return true;
                }),
                null,
                ct).ConfigureAwait(false);
        }

        StageClock.Add(Stage.Angle, angleStart);
        return result;
    }

    /// <summary>The reader's output of each line (bitmaps <see cref="LineHeight"/> high), in the order given.</summary>
    public async Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        var result = new LineScores[lines.Count];
        var order = Enumerable.Range(0, lines.Count).OrderBy(i => lines[i].Width).ToArray();
        var batch = RecognitionBatch;
        for (var start = 0; start < order.Length; start += batch)
        {
            var count = Math.Min(batch, order.Length - start);
            var widest = 0;
            for (var i = 0; i < count; i++)
            {
                widest = Math.Max(widest, lines[order[start + i]].Width);
            }

            var inputStart = Stopwatch.GetTimestamp();
            var width = FixedSizes ? DirectMlWidths.First(w => w >= Math.Min(widest, MaxLineWidth)) : Math.Max(MinLineWidth, (widest + 31) / 32 * 32);
            var size = FixedSizes ? DirectMlBatches.First(b => b >= count) : count;
            using var input = PooledFloats.Rent(size * 3 * LineHeight * width, clear: true);
            for (var i = 0; i < count; i++)
            {
                Tensors.Write(lines[order[start + i]], input.Array, i, LineHeight, width, Tensors.Symmetric);
            }

            StageClock.Add(Stage.RecognizeInput, inputStart);
            var first = start;
            await RunAsync(
                () => _recognition.Run(input.Array, [size, 3, LineHeight, width], (output, shape) =>
                {
                    var frames = (int)shape[1];
                    var classes = (int)shape[2];
                    for (var i = 0; i < count; i++)
                    {
                        // Only the frames of the line itself (and a little after it); the padding's frames are blank.
                        var line = order[first + i];
                        var own = Math.Min(frames, (lines[line].Width + 7) / 8 + 2);
                        result[line] = new LineScores(output.Slice(i * frames * classes, own * classes).ToArray(), own, classes);
                    }

                    return true;
                }),
                Stage.RecognizeModel,
                ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<T> RunAsync<T>(Func<T> run, Stage? stage, CancellationToken ct)
    {
        if (_serial is null)
        {
            ct.ThrowIfCancellationRequested();
            return Timed(run, stage);
        }

        await _serial.WaitAsync(ct).ConfigureAwait(false);
        var started = _busy?.Start() ?? 0;
        try
        {
            return Timed(run, stage);
        }
        finally
        {
            _busy?.Stop(started);
            _serial.Release();
        }
    }

    private static T Timed<T>(Func<T> run, Stage? stage)
    {
        var start = Stopwatch.GetTimestamp();
        var result = run();
        if (stage is { } s)
        {
            StageClock.Add(s, start);
        }

        return result;
    }

    public void Dispose()
    {
        _detection.Dispose();
        _recognition.Dispose();
        _orientation.Dispose();
        _ownBudget?.Dispose();
        _serial?.Dispose();
    }
}
