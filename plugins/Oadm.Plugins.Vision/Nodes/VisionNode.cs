using System.Globalization;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>
/// One node of a pipeline at work (docs/spec/vision-center.md "Parts and contract"): its kind, its input and what it does
/// with one frame. The contract an SDK may open later; internal for now.
/// </summary>
internal interface IVisionNode
{
    PipelineNode Node { get; }

    NodeKindInfo Kind { get; }

    /// <summary>The kind its input carries (null for the Camera).</summary>
    PortKind? Input { get; }

    /// <summary>Works on one frame; <paramref name="input"/> is the output of the node before it (the Camera: none).</summary>
    ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame);
}

/// <summary>What every node of one frame shares.</summary>
internal sealed class FrameContext(IVisionFrame frame, DateTimeOffset at, IInference inference, TimeProvider time, CancellationToken ct)
{
    public IVisionFrame Frame { get; } = frame;

    public DateTimeOffset At { get; } = at;

    public IInference Inference { get; } = inference;

    /// <summary>Measures the model calls only.</summary>
    public TimeProvider Time { get; } = time;

    public CancellationToken Ct { get; } = ct;

    public List<Emission> Emissions { get; } = [];
}

/// <summary>A model call failed: the frame ends (<see cref="FrameResult.ModelFailure"/>).</summary>
internal sealed class ModelFailedException(string message, Exception inner) : Exception(message, inner);

internal abstract class VisionNode(PipelineNode node, NodeKindInfo kind, PortKind? input) : IVisionNode
{
    public PipelineNode Node { get; } = node;

    public NodeKindInfo Kind { get; } = kind;

    public PortKind? Input { get; } = input;

    public string Id => Node.Id;

    public abstract ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame);

    /// <summary>Runs a model call, measures it and turns a failure into <see cref="ModelFailedException"/>.</summary>
    protected static async Task<(T Result, double Ms)> MeasureAsync<T>(FrameContext frame, VisionModel model, Func<Task<T>> call)
    {
        var start = frame.Time.GetTimestamp();
        try
        {
            var result = await call().ConfigureAwait(false);
            return (result, frame.Time.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (frame.Ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure of a model ends the frame with a problem, never the pipeline.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ModelFailedException($"The model {model.Name} failed: {ex.Message}", ex);
        }
    }
}

/// <summary>The live lines of the node cards (docs/spec/vision-center.md "Page").</summary>
internal static class LiveText
{
    /// <summary>"1 object", "5 objects".</summary>
    public static string Objects(int count) => count == 1 ? "1 object" : $"{Number(count)} objects";

    /// <summary>" · 18 ms".</summary>
    public static string Ms(double ms) => string.Create(CultureInfo.InvariantCulture, $" · {Math.Round(ms):0} ms");

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"Fired now", "Fired 3 s ago", "Fired 2 min ago", "Fired 1 h ago".</summary>
    public static string FiredAgo(DateTimeOffset fired, DateTimeOffset now)
    {
        var s = Math.Max(0, (now - fired).TotalSeconds);
        return s switch
        {
            < 1 => "Fired now",
            < 60 => $"Fired {(int)s} s ago",
            < 3600 => $"Fired {(int)(s / 60)} min ago",
            _ => $"Fired {(int)(s / 3600)} h ago",
        };
    }

    /// <summary>"92 %".</summary>
    public static string Percent(double score) => $"{Score(score)} %";

    /// <summary>A score 0 to 1 as 0 to 100, rounded (the event field Score).</summary>
    public static string Score(double score) =>
        ((int)Math.Round(Math.Clamp(score, 0, 1) * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Seconds with at most one decimal (the event field Time inside).</summary>
    public static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.#", CultureInfo.InvariantCulture);
}
