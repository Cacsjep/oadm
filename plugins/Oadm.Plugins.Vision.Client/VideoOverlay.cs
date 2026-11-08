using System.Globalization;

using Avalonia;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>What the video overlay draws for a node's output.</summary>
public enum OverlayItemKind
{
    /// <summary>A box: <see cref="OverlayItem.Points"/> are its top left and bottom right.</summary>
    Box,

    /// <summary>A tracked object's short trail.</summary>
    Trail,

    /// <summary>A Count node's number in the picture's corner.</summary>
    Badge,
}

/// <summary>One thing drawn over the video, relative to the picture, in its node's colour.</summary>
/// <param name="Label">The tag on a box ("person 87 %", "#3"), a badge's name.</param>
/// <param name="Value">A badge's number.</param>
/// <param name="Dashed">A box Filter left out.</param>
/// <param name="Filled">An object inside an Area, filled lightly.</param>
public sealed record OverlayItem(
    OverlayItemKind Kind,
    string NodeId,
    string BrushKey,
    IReadOnlyList<Point> Points,
    string? Label = null,
    string? Value = null,
    bool Dashed = false,
    bool Filled = false);

/// <summary>
/// An Area's polygon or a Line's line on the video: drawn in its node's colour with its name and live numbers, edited there
/// (the selected one has a handle per point).
/// </summary>
public sealed record ShapeItem(
    NodeViewModel Node,
    IReadOnlyList<Point> Points,
    bool Closed,
    string BrushKey,
    string Label,
    bool IsSelected,
    int MinPoints,
    int MaxPoints);

/// <summary>
/// The overlay of the live video (docs/spec/vision-center.md "Page"): for each node whose eye is on, and the selected node,
/// what its output gives in the frame: Detect boxes with class and score, Filter the kept boxes and the left out ones dashed,
/// Track boxes with the id and a short trail, Classify the class on the box (on a picture in the corner), Area its polygon with the inside count and the
/// objects inside filled lightly, Line the line with the In arrow and "In 12 · Out 7", Count the number in the corner. Pure.
/// </summary>
public static class VideoOverlay
{
    /// <summary>The items and shapes of <paramref name="nodes"/> for <paramref name="outputs"/> (by node id).</summary>
    public static (IReadOnlyList<OverlayItem> Items, IReadOnlyList<ShapeItem> Shapes) Build(
        IEnumerable<NodeViewModel> nodes,
        NodeViewModel? selected,
        IReadOnlyDictionary<string, NodeOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(outputs);
        var items = new List<OverlayItem>();
        var shapes = new List<ShapeItem>();
        foreach (var node in nodes)
        {
            var isSelected = ReferenceEquals(node, selected);
            if (!node.CanShowOnVideo || (!node.ShowOnVideo && !isSelected))
            {
                continue;
            }

            var output = node.HasError ? null : outputs.GetValueOrDefault(node.Id);
            var brush = node.BrushKey;
            if (NodeValues.ShapeField(node.Info, node.InputKind) is { } field)
            {
                var points = NodeValues.Points(NodeValues.Get(node.Settings, field));
                if (points.Count > 0)
                {
                    var closed = node.Kind != NodeKinds.Line;
                    shapes.Add(new ShapeItem(node, points, closed, brush, ShapeLabel(node, output), isSelected, field.MinPoints, field.MaxPoints));
                }
            }

            if (output is null)
            {
                continue;
            }

            switch (node.Kind)
            {
                case NodeKinds.Detect:
                    items.AddRange(output.Objects.Select(o => Box(node, o, $"{o.Detection.ClassName} {VisionTexts.Score(o.Detection.Score)}")));
                    break;
                case NodeKinds.Classify:
                    if (output.Label is { } picture)
                    {
                        // Classify on the picture: its class in the corner, like a count.
                        items.Add(new OverlayItem(OverlayItemKind.Badge, node.Id, brush, [], node.Name, $"{picture.ClassName} {VisionTexts.Score(picture.Score)}"));
                    }

                    items.AddRange(output.Objects.Select(o => Box(node, o, o.Label is { } l ? $"{l.ClassName} {VisionTexts.Score(l.Score)}" : o.Detection.ClassName)));
                    break;
                case NodeKinds.Filter:
                    items.AddRange(output.Objects.Select(o => Box(node, o, o.Detection.ClassName)));
                    items.AddRange(output.Dropped.Select(o => Box(node, o, null) with { Dashed = true }));
                    break;
                case NodeKinds.Track:
                    foreach (var o in output.Objects)
                    {
                        if (o.Trail.Count > 1)
                        {
                            items.Add(new OverlayItem(OverlayItemKind.Trail, node.Id, brush, [.. o.Trail.Select(p => new Point(p.X, p.Y))]));
                        }

                        items.Add(Box(node, o, o.TrackId is { } id ? VisionTexts.ObjectId(id) : null));
                    }

                    break;
                case NodeKinds.Area:
                    items.AddRange(output.Objects.Select(o => Box(node, o, o.InsideSeconds is { } s && o.TrackId is not null
                        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(s)} s")
                        : null) with { Filled = true }));
                    break;
                case NodeKinds.Count:
                    items.Add(new OverlayItem(OverlayItemKind.Badge, node.Id, brush, [], node.Name, (output.Value ?? 0).ToString(CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return (items, shapes);
    }

    /// <summary>"Waiting zone · 2 inside", "Gate line · In 12 · Out 7"; the name alone before a frame.</summary>
    public static string ShapeLabel(NodeViewModel node, NodeOutput? output)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (output is null)
        {
            return node.Name;
        }

        return node.Kind == NodeKinds.Line
            ? $"{node.Name} · {VisionTexts.InOut(output.In, output.Out)}"
            : string.Create(CultureInfo.InvariantCulture, $"{node.Name} · {output.Objects.Count} inside");
    }

    /// <summary>The middle of a line and the way its In arrow points (to the right of the line from its first point to its last).</summary>
    public static (Point Middle, Vector InDirection) InArrow(IReadOnlyList<Point> line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Count < 2)
        {
            return (line.Count == 1 ? line[0] : default, new Vector(0, 1));
        }

        var i = (line.Count - 2) / 2;
        var a = line[i];
        var b = line[i + 1];
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return (new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), length <= 0 ? new Vector(0, 1) : new Vector(-dy / length, dx / length));
    }

    private static OverlayItem Box(NodeViewModel node, VisionObject o, string? label)
    {
        var box = o.Detection.Box;
        return new OverlayItem(OverlayItemKind.Box, node.Id, node.BrushKey, [new Point(box.X, box.Y), new Point(box.Right, box.Bottom)], label);
    }
}
