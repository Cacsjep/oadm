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

    /// <summary>The accurate models (Accurate on the CPU; the graphics card's text finder of Search is its detection model).</summary>
    public static ModelChoice Server { get; } = new(ModelFiles.DetectionServer, ModelFiles.RecognitionLatin, ModelFiles.RecognitionLatinCharacters);
}

/// <summary>
/// One CPU worker's set of sessions: text finder, angle model and reader with its character list. The graphics card holds only
/// the text finder of Search (<see cref="CardTextFinder"/>, docs/spec/ocr.md "GPU").
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

    /// <summary>Lines per reader call.</summary>
    public const int RecognitionBatch = 8;

    private readonly ShapeSessions _detection;
    private readonly ShapeSessions _recognition;
    private readonly ShapeSessions _orientation;

    public ModelSet(string folder, ModelChoice choice, int threads)
    {
        Characters = CharacterList.Load(Path.Combine(folder, choice.Characters));
        _detection = new ShapeSessions(Path.Combine(folder, choice.Detection), ExecutionTarget.Cpu, threads);
        _recognition = new ShapeSessions(Path.Combine(folder, choice.Recognition), ExecutionTarget.Cpu, threads);
        _orientation = new ShapeSessions(Path.Combine(folder, ModelFiles.Orientation), ExecutionTarget.Cpu, threads);
    }

    /// <summary>The reader's character list.</summary>
    public CharacterList Characters { get; }

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
            var size = count;
            using var input = PooledFloats.Rent(size * 3 * OrientationHeight * OrientationWidth, clear: false);
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
            var width = Math.Max(MinLineWidth, (widest + 31) / 32 * 32);
            var size = count;
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

    private static Task<T> RunAsync<T>(Func<T> run, Stage? stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Timed(run, stage));
    }

    internal static T Timed<T>(Func<T> run, Stage? stage)
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
    }
}
