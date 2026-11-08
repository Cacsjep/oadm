using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Nodes;
using Oadm.Plugins.Vision.Tests.Tracking;

namespace Oadm.Plugins.Vision.Tests.Nodes;

/// <summary>An evaluator over a fake inference, frames at given seconds from <see cref="TrackFixture.Start"/>.</summary>
internal sealed class EvaluatorHarness
{
    public static readonly DateTimeOffset T0 = TrackFixture.Start;

    public EvaluatorHarness(Pipeline pipeline, PipelineMode? mode = null, bool allNodes = false, TimeZoneInfo? zone = null, IEventStates? events = null)
    {
        Inference = new FakeInference { Clock = Clock };
        Evaluator = PipelineEvaluator.Create(pipeline, PipelineBuilder.Find, Inference, mode ?? pipeline.Mode,
            new EvaluatorOptions { AllNodes = allNodes, Time = Clock, TimeZone = zone ?? TimeZoneInfo.Utc, EventStates = events });
    }

    public FakeTimeProvider Clock { get; } = new();

    public FakeInference Inference { get; }

    public IPipelineEvaluator Evaluator { get; }

    /// <summary>Every emission so far, in order.</summary>
    public List<(double At, Emission Emission)> Emitted { get; } = [];

    public async Task<FrameResult> RunAsync(double seconds, params Detection[] detections)
    {
        Inference.Enqueue(detections);
        return await FrameAsync(seconds);
    }

    /// <summary>A frame with what the inference has queued (or its <see cref="FakeInference.Always"/>).</summary>
    public async Task<FrameResult> FrameAsync(double seconds)
    {
        using var frame = new FakeFrame();
        var result = await Evaluator.RunAsync(frame, T0 + TimeSpan.FromSeconds(seconds), CancellationToken.None);
        foreach (var e in result.Emissions)
        {
            Emitted.Add((seconds, e));
        }

        return result;
    }

    /// <summary>Runs a recorded sequence, one frame each.</summary>
    public async Task<FrameResult> RunAsync(TrackFixture fixture)
    {
        FrameResult? last = null;
        foreach (var f in fixture.Frames)
        {
            Inference.Enqueue([.. f.Plain]);
            using var frame = new FakeFrame();
            last = await Evaluator.RunAsync(frame, f.At, CancellationToken.None);
            foreach (var e in last.Emissions)
            {
                Emitted.Add(((f.At - T0).TotalSeconds, e));
            }
        }

        return last!;
    }

    public IEnumerable<Emission> Of(VisionEventKind kind) => Emitted.Select(e => e.Emission).Where(e => e.Kind == kind);
}
