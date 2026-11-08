using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// How a node kind looks on the canvas, in the node list and on the video (docs/spec/vision-center.md "Page"): its icon and
/// its one colour, the same everywhere, from the theme's categorical palette (both appearances). A kind this version does not
/// know gets the neutral colour and the plugin icon.
/// </summary>
public static class NodeLook
{
    public static string IconKey(string kind) => kind switch
    {
        NodeKinds.Camera => "video",
        NodeKinds.Detect => "scan",
        NodeKinds.Classify => "layers",
        NodeKinds.Filter => "filter",
        NodeKinds.Track => "route",
        NodeKinds.Area => "polygon",
        NodeKinds.Line => "crossing",
        NodeKinds.Count => "hash",
        NodeKinds.Event => "zap",
        NodeKinds.Compare => "compare",
        NodeKinds.Timer => "clock",
        _ => "plugin",
    };

    /// <summary>The theme brush of the kind's colour.</summary>
    public static string BrushKey(string kind) => kind switch
    {
        NodeKinds.Camera => "Oadm.Tag.NeutralBrush",
        NodeKinds.Detect => "Oadm.Tag.TealBrush",
        NodeKinds.Classify => "Oadm.Tag.VioletBrush",
        NodeKinds.Filter => "Oadm.Tag.AmberBrush",
        NodeKinds.Track => "Oadm.Tag.BlueBrush",
        NodeKinds.Area => "Oadm.Tag.GreenBrush",
        NodeKinds.Line => "Oadm.Tag.PinkBrush",
        NodeKinds.Count => "Oadm.Tag.OrangeBrush",
        NodeKinds.Event => "Oadm.Tag.RedBrush",
        NodeKinds.Compare => "Oadm.AccentBrush",
        NodeKinds.Timer => "Oadm.TextSecondaryBrush",
        _ => "Oadm.Tag.NeutralBrush",
    };

    /// <summary>The kind's name in the node list ("Detect objects"), the id for a kind this version does not know.</summary>
    public static string Label(string kind) => NodeKinds.Find(kind)?.Label ?? kind;
}

/// <summary>
/// A node's settings as the page reads and writes them (docs/spec/vision-center.md "Nodes"): by field key, a field without a
/// value takes its default. The JSON readers are the server's (<see cref="Graph.NodeSettings"/>); points here are Avalonia
/// points relative to the picture.
/// </summary>
public static class NodeValues
{
    /// <summary>The value of <paramref name="field"/> in <paramref name="settings"/>, or its default; null without either.</summary>
    public static JsonNode? Get(JsonObject settings, NodeFieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(field);
        if (settings.TryGetPropertyValue(field.Key, out var value) && value is not null)
        {
            return value;
        }

        try
        {
            return field.Default is { Length: > 0 } text ? JsonNode.Parse(text) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static double? Number(JsonNode? value) => Graph.NodeSettings.Number(value);

    public static string? Text(JsonNode? value) => Graph.NodeSettings.Text(value);

    public static bool? Bool(JsonNode? value) => Graph.NodeSettings.Bool(value);

    public static IReadOnlyList<string> Strings(JsonNode? value) => Graph.NodeSettings.Texts(value) ?? [];

    /// <summary>The points of a shape field; empty without a shape.</summary>
    public static IReadOnlyList<Point> Points(JsonNode? value) =>
        Graph.NodeSettings.Points(value) is { } points ? [.. points.Select(p => new Point(p.X, p.Y))] : [];

    public static JsonArray ToJson(IEnumerable<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return Graph.NodeSettings.ToJson(points.Select(p => new Model.RelativePoint(Math.Round(p.X, 4), Math.Round(p.Y, 4))));
    }

    public static JsonArray ToJson(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }

    /// <summary>The shape field of a node kind (Area, Line, Classify on a picture), or null.</summary>
    public static NodeFieldInfo? ShapeField(NodeKindInfo? kind, PortKind? input) =>
        kind?.Fields.FirstOrDefault(f => f.Type == NodeFieldType.Shape && Graph.VisionGraph.Applies(f, input));

    /// <summary>A number as the form shows it ("50", "0.5").</summary>
    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
