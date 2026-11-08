using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>Builds the evaluator of a checked pipeline (docs/spec/vision-center.md "Running" and "Events").</summary>
public static class PipelineEvaluator
{
    /// <summary>
    /// The evaluator of <paramref name="pipeline"/> (checked: <see cref="Graph.VisionGraph.Check"/> passed). Nodes after an
    /// error never run. <paramref name="mode"/> decides Objects found / No objects found against the stateful kinds.
    /// </summary>
    public static IPipelineEvaluator Create(Pipeline pipeline, Func<string?, VisionModel?> findModel, IInference inference, PipelineMode mode) =>
        throw new NotImplementedException("Filled in by the graph stage.");
}
