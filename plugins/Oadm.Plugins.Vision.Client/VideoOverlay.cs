using System.Globalization;

using Avalonia;

using Oadm.Plugins.Vision.Graph;
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
/// <param name="ShortLabel">The label when objects stand close: the score or the id alone; null: none then.</param>
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
    bool Filled = false,
    string? ShortLabel = null);

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
    int MaxPoints)
{
    /// <summary>Direction: drawn as an arrow from the first point to the second.</summary>
    public bool IsArrow { get; init; }

    /// <summary>Direction: the tolerance to each side of the arrow, degrees (drawn as a faint wedge).</summary>
    public double Tolerance { get; init; }
}

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
                    var closed = node.Kind is not (NodeKinds.Line or NodeKinds.Direction);
                    var tolerance = node.Kind == NodeKinds.Direction && VisionGraph.FieldOf(node.Info, NodeKinds.AngleKey) is { } angle
                        ? NodeValues.Number(NodeValues.Get(node.Settings, angle)) ?? 45
                        : 0;
                    shapes.Add(new ShapeItem(node, points, closed, brush, ShapeLabel(node, output), isSelected, field.MinPoints, field.MaxPoints)
                    {
                        IsArrow = node.Kind == NodeKinds.Direction,
                        Tolerance = tolerance,
                    });
                }
            }

            if (output is null)
            {
                continue;
            }

            switch (node.Kind)
            {
                case NodeKinds.Detect:
                    items.AddRange(output.Objects.Select(o => Box(node, o, $"{o.Detection.ClassName} {VisionTexts.Score(o.Detection.Score)}")
                        with { ShortLabel = VisionTexts.Score(o.Detection.Score) }));
                    break;
                case NodeKinds.Classify:
                    if (output.Label is { } picture)
                    {
                        // Classify on the picture: its class in the corner, like a count.
                        items.Add(new OverlayItem(OverlayItemKind.Badge, node.Id, brush, [], node.Name, $"{picture.ClassName} {VisionTexts.Score(picture.Score)}"));
                    }

                    items.AddRange(output.Objects.Select(o => Box(node, o, o.Label is { } l ? $"{l.ClassName} {VisionTexts.Score(l.Score)}" : o.Detection.ClassName)
                        with { ShortLabel = o.Label is { } s ? VisionTexts.Score(s.Score) : null }));
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

                        var trackLabel = o.TrackId is { } id ? VisionTexts.ObjectId(id) : null;
                        items.Add(Box(node, o, trackLabel) with { ShortLabel = trackLabel });
                    }

                    break;
                case NodeKinds.Area:
                    foreach (var o in output.Objects)
                    {
                        var inside = o.InsideSeconds is { } s && o.TrackId is not null ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(s)} s") : null;
                        items.Add(Box(node, o, inside) with { Filled = true, ShortLabel = inside });
                    }

                    break;
                case NodeKinds.Dwell or NodeKinds.Stationary or NodeKinds.Direction:
                    // The kept objects, filled lightly, with how long they stay ("45 s").
                    foreach (var o in output.Objects)
                    {
                        var time = o.InsideSeconds is { } s ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(s)} s") : null;
                        items.Add(Box(node, o, time ?? (o.TrackId is { } id ? VisionTexts.ObjectId(id) : null)) with { Filled = true, ShortLabel = time });
                    }

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

        return node.Kind switch
        {
            NodeKinds.Line => $"{node.Name} · {VisionTexts.InOut(output.In, output.Out)}",
            NodeKinds.Direction => string.Create(CultureInfo.InvariantCulture, $"{node.Name} · {output.Objects.Count}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{node.Name} · {output.Objects.Count} inside"),
        };
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

    /// <summary>
    /// The label and its place: the whole label above the box, else inside its top edge, else the short label (the score or the
    /// id alone) the same two ways; null when none keeps clear of <paramref name="taken"/> inside the picture.
    /// </summary>
    public static (string Label, Rect At)? PlaceLabel(OverlayItem item, Rect box, Rect picture, Func<string, Size> measure, IReadOnlyList<Rect> taken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(taken);
        foreach (var label in new[] { item.Label, item.ShortLabel }.Distinct())
        {
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            var size = measure(label);
            foreach (var at in new[] { ShapePainter.TagAbove(size, box.TopLeft, picture), ShapePainter.TagInside(size, box.TopLeft) })
            {
                if (picture.Contains(at) && !taken.Any(t => t.Intersects(at)))
                {
                    return (label, at);
                }
            }
        }

        return null;
    }

    private static OverlayItem Box(NodeViewModel node, VisionObject o, string? label)
    {
        var box = o.Detection.Box;
        return new OverlayItem(OverlayItemKind.Box, node.Id, node.BrushKey, [new Point(box.X, box.Y), new Point(box.Right, box.Bottom)], label);
    }
}
