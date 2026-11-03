using Microsoft.ML.OnnxRuntime.Tensors;
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

    public ModelSet(string folder, ModelChoice choice, ExecutionTarget target, int threads)
    {
        Target = target;
        Characters = CharacterList.Load(Path.Combine(folder, choice.Characters));
        _detection = new ShapeSessions(Path.Combine(folder, choice.Detection), target, threads);
        _recognition = new ShapeSessions(Path.Combine(folder, choice.Recognition), target, threads);
        _orientation = new ShapeSessions(Path.Combine(folder, ModelFiles.Orientation), target, threads);
        _serial = target == ExecutionTarget.Cpu ? null : new SemaphoreSlim(1, 1);
    }

    /// <summary>Where it runs.</summary>
    public ExecutionTarget Target { get; }

    /// <summary>The reader's character list.</summary>
    public CharacterList Characters { get; }

    /// <summary>Lines per reader call.</summary>
    public int RecognitionBatch => Target == ExecutionTarget.Cpu ? 8 : 32;

    /// <summary>On DirectML every call has a fixed batch and the widths come in steps: few input sizes, each fast.</summary>
    private bool FixedSizes => Target == ExecutionTarget.DirectMl;

    /// <summary>The text finder's probability maps of <paramref name="count"/> pictures of one size, [count, height, width].</summary>
    public async Task<float[]> DetectAsync(float[] input, int count, int height, int width, CancellationToken ct)
    {
        var tensor = new DenseTensor<float>(input, [count, 3, height, width]);
        return await RunAsync(() => _detection.Run(tensor).Output, ct).ConfigureAwait(false);
    }

    /// <summary>The probability that each line is upside down (0 to 1).</summary>
    public async Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
    {
        var result = new float[lines.Count];
        const int Batch = 32;
        for (var start = 0; start < lines.Count; start += Batch)
        {
            var count = Math.Min(Batch, lines.Count - start);
            var size = FixedSizes ? DirectMlBatches.First(b => b >= count) : count;
            var input = new float[size * 3 * OrientationHeight * OrientationWidth];
            for (var i = 0; i < count; i++)
            {
                using var small = lines[start + i].Resize(new SKImageInfo(OrientationWidth, OrientationHeight, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
                Tensors.Write(small, input, i, OrientationHeight, OrientationWidth, Tensors.ImageNet);
            }

            var tensor = new DenseTensor<float>(input, [size, 3, OrientationHeight, OrientationWidth]);
            var probabilities = await RunAsync(() => _orientation.Run(tensor).Output, ct).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                result[start + i] = probabilities[i * 2 + 1];
            }
        }

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

            var width = FixedSizes ? DirectMlWidths.First(w => w >= Math.Min(widest, MaxLineWidth)) : Math.Max(MinLineWidth, (widest + 31) / 32 * 32);
            var size = FixedSizes ? DirectMlBatches.First(b => b >= count) : count;
            var input = new float[size * 3 * LineHeight * width];
            for (var i = 0; i < count; i++)
            {
                Tensors.Write(lines[order[start + i]], input, i, LineHeight, width, Tensors.Symmetric);
            }

            var tensor = new DenseTensor<float>(input, [size, 3, LineHeight, width]);
            var (output, dimensions) = await RunAsync(() => _recognition.Run(tensor), ct).ConfigureAwait(false);
            var frames = dimensions[1];
            var classes = dimensions[2];
            for (var i = 0; i < count; i++)
            {
                // Only the frames of the line itself (and a little after it); the padding's frames are blank.
                var own = Math.Min(frames, (lines[order[start + i]].Width + 7) / 8 + 2);
                var scores = new float[own * classes];
                Array.Copy(output, i * frames * classes, scores, 0, own * classes);
                result[order[start + i]] = new LineScores(scores, own, classes);
            }
        }

        return result;
    }

    private async Task<T> RunAsync<T>(Func<T> run, CancellationToken ct)
    {
        if (_serial is null)
        {
            ct.ThrowIfCancellationRequested();
            return run();
        }

        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return run();
        }
        finally
        {
            _serial.Release();
        }
    }

    public void Dispose()
    {
        _detection.Dispose();
        _recognition.Dispose();
        _orientation.Dispose();
        _serial?.Dispose();
    }
}
