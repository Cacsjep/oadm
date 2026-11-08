using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
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

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// The real parts: <see cref="ModelLibrary"/> in the plugin's records and data folder, <see cref="OnnxInference"/> over its
    /// files (on the CPU until the plugin hands "Use GPU": off must not load DirectML), and the cameras' pictures through the
    /// plugin's full device access (<see cref="Inference.VapixFrameSource"/> over the frame source shared with Text recognition;
    /// the pipelines work without a user's call). Load the library (<see cref="ModelLibrary.LoadAsync"/>) before it is used.
    /// </summary>
    public static VisionParts Real(TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        return new VisionParts
        {
            Models = ctx => new ModelLibrary(RecordsOf(ctx), DataOf(ctx), clock, ctx.Logger),
            Inference = ctx =>
            {
                var data = DataOf(ctx);
                return new OnnxInference(new InferenceOptions { FileOf = id => FileOf(data, id), Time = clock }, ctx.Logger);
            },
            Frames = ctx => new Inference.VapixFrameSource(ctx.System.Vapix, clock, ctx.Logger),
            Time = clock,
        };
    }

    /// <summary>The file of a model in the data folder, null when it is not there (Missing).</summary>
    private static string? FileOf(string dataDirectory, string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
        {
            return null; // never a path from elsewhere
        }

        var path = ModelLibrary.PathOf(dataDirectory, id);
        return File.Exists(path) ? path : null;
    }

    private static IPluginRecords RecordsOf(ICorePluginContext ctx) =>
        ctx.Records ?? throw new InvalidOperationException("This OADM server cannot keep vision models. Update OADM.");

    private static string DataOf(ICorePluginContext ctx) =>
        ctx.DataDirectory ?? throw new InvalidOperationException("This OADM server has no data folder for vision models. Update OADM.");
}
