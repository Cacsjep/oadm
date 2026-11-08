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

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The real parts: Models/ModelLibrary, Inference/OnnxInference, the shared frame source (plugins/Shared/Vision).</summary>
    public static VisionParts Real(TimeProvider? time = null) =>
        throw new NotImplementedException("Filled in by the models and inference stage.");
}
