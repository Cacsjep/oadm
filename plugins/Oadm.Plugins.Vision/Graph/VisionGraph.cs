using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Graph;

/// <summary>
/// The graph check of docs/spec/vision-center.md rule 8 and "Graph": one camera, one input per node, no loops, fitting
/// kinds, Track and Line only while Watching, a Line after a Track, models that exist and fit, settings within their
/// fields, unique names, the limits. The server's save, the runtime and the page's editor use this one check.
/// </summary>
public static class VisionGraph
{
    /// <summary>Checks <paramref name="pipeline"/>; <paramref name="findModel"/> finds a stored model by id (null: none).</summary>
    public static GraphCheck Check(Pipeline pipeline, Func<string?, VisionModel?> findModel) =>
        throw new NotImplementedException("Filled in by the graph stage.");

    /// <summary>The value of a node's setting <paramref name="key"/>, or its field's default.</summary>
    public static System.Text.Json.Nodes.JsonNode? Setting(PipelineNode node, string key) =>
        throw new NotImplementedException("Filled in by the graph stage.");
}
