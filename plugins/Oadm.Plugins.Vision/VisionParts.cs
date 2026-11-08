using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision;

/// <summary>
/// The parts the runtime works with (docs/spec/vision-center.md "Parts and contract"). The plugin builds them once per
/// start; tests hand fakes.
/// </summary>
public sealed record VisionParts
{
    /// <summary>The stored models of the plugin's records and data folder (<see cref="ICorePluginContext.DataDirectory"/>).</summary>
    public required Func<ICorePluginContext, IModelLibrary> Models { get; init; }

    /// <summary>The inference over the models (the plugin hands "Use GPU" next; off must not load DirectML).</summary>
    public required Func<ICorePluginContext, IInference> Inference { get; init; }

    /// <summary>The cameras' pictures through the plugin's full device access.</summary>
    public required Func<ICorePluginContext, IFrameSource> Frames { get; init; }

    /// <summary>The graph check (<see cref="Graph.VisionGraph.Check"/>); tests may hand a fake.</summary>
    public Func<Pipeline, Func<string?, VisionModel?>, GraphCheck> Check { get; init; } = Graph.VisionGraph.Check;

    /// <summary>Builds a checked pipeline's evaluator (<see cref="Nodes.PipelineEvaluator.Create"/>); tests may hand a fake.</summary>
    public Func<Pipeline, Func<string?, VisionModel?>, IInference, PipelineMode, IPipelineEvaluator> Evaluator { get; init; } = Nodes.PipelineEvaluator.Create;

    /// <summary>
    /// Builds the evaluator of a page's preview: like <see cref="Evaluator"/>, with every node's output for the page (also of
    /// branches nothing after needs). Null: <see cref="Evaluator"/>.
    /// </summary>
    public Func<Pipeline, Func<string?, VisionModel?>, IInference, PipelineMode, IPipelineEvaluator>? PreviewEvaluator { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The processor count the watching limits follow (<see cref="VisionLimits.MaxWatchingFor"/>).</summary>
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    /// <summary>The real parts: Models/ModelLibrary, Inference/OnnxInference, the shared frame source (plugins/Shared/Vision).</summary>
    public static VisionParts Real(TimeProvider? time = null) =>
        throw new NotImplementedException("Filled in by the models and inference stage.");
}
