using Avalonia;
using Avalonia.Input;

namespace Oadm.Plugins.Shared.Vision.Client;

// Linked into the client parts of Text recognition and Vision Center (docs/spec/vision-center.md "Parts and contract"):
// shapes on a camera picture, relative to the picture (0 to 1). A rectangle is one case of the shapes: its corners and edges
// have handles; a polygon or a line has a handle per point. Each plugin keeps its own load context; the picture size type
// is the plugin's own (the global alias PictureResolution of each project).

/// <summary>What a drag on the selected shape changes: the whole shape (move), a corner or edge of a rectangle, or a point.</summary>
public enum AreaHandle
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,

    /// <summary>One point of a polygon or a line (its index comes with the hit).</summary>
    Vertex,
}

/// <summary>What a press on a shape takes: the handle and, for <see cref="AreaHandle.Vertex"/>, the point's index.</summary>
public readonly record struct ShapeHit(AreaHandle Handle, int Vertex = -1)
{
    public static ShapeHit None => new(AreaHandle.None);
}

/// <summary>
/// The rules of moving and resizing shapes on the picture, relative to it (0 to 1): a shape always stays inside the picture;
/// a rectangle keeps at least its minimum on each side, the opposite edge stays in place.
/// </summary>
public static class ShapeGeometry
{
    /// <summary>One arrow key press moves the selected shape by this much; with Shift by <see cref="BigNudge"/>.</summary>
    public const double Nudge = 0.01;

    public const double BigNudge = 0.05;

    /// <summary>The rectangle moved by (<paramref name="dx"/>, <paramref name="dy"/>), held inside the picture with its size kept.</summary>
    public static Rect MoveBox(Rect box, double dx, double dy, double minSize)
    {
        var width = Math.Clamp(box.Width, minSize, 1);
        var height = Math.Clamp(box.Height, minSize, 1);
        return new Rect(Math.Clamp(box.X + dx, 0, 1 - width), Math.Clamp(box.Y + dy, 0, 1 - height), width, height);
    }

    /// <summary>
    /// The rectangle with the edges of <paramref name="handle"/> moved by (<paramref name="dx"/>, <paramref name="dy"/>): each
    /// moved edge stays inside the picture and at least <paramref name="minSize"/> from the opposite edge, which stays in place.
    /// </summary>
    public static Rect ResizeBox(Rect box, AreaHandle handle, double dx, double dy, double minSize)
    {
        if (handle == AreaHandle.Move)
        {
            return MoveBox(box, dx, dy, minSize);
        }

        var left = box.X;
        var top = box.Y;
        var right = box.X + box.Width;
        var bottom = box.Y + box.Height;
        if (handle is AreaHandle.Left or AreaHandle.TopLeft or AreaHandle.BottomLeft)
        {
            left = Math.Clamp(left + dx, 0, right - minSize);
        }

        if (handle is AreaHandle.Right or AreaHandle.TopRight or AreaHandle.BottomRight)
        {
            right = Math.Clamp(right + dx, left + minSize, 1);
        }

        if (handle is AreaHandle.Top or AreaHandle.TopLeft or AreaHandle.TopRight)
        {
            top = Math.Clamp(top + dy, 0, bottom - minSize);
        }

        if (handle is AreaHandle.Bottom or AreaHandle.BottomLeft or AreaHandle.BottomRight)
        {
            bottom = Math.Clamp(bottom + dy, top + minSize, 1);
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>The rectangle between two points (a drawn shape).</summary>
    public static Rect Between(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>A drawn rectangle counts once both sides reach <paramref name="minSize"/>.</summary>
    public static bool IsBigEnough(Rect box, double minSize) => box.Width >= minSize && box.Height >= minSize;

    /// <summary>The four corners of a rectangle, clockwise from the top left: a drawn area as a polygon.</summary>
    public static IReadOnlyList<Point> Corners(Rect box) => [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];

    /// <summary>The points moved together by (<paramref name="dx"/>, <paramref name="dy"/>), the shape kept and held inside the picture.</summary>
    public static IReadOnlyList<Point> MovePoints(IReadOnlyList<Point> points, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return points;
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        dx = Math.Clamp(dx, -minX, 1 - maxX);
        dy = Math.Clamp(dy, -minY, 1 - maxY);
        return [.. points.Select(p => new Point(p.X + dx, p.Y + dy))];
    }

    /// <summary>One point moved by (<paramref name="dx"/>, <paramref name="dy"/>), held inside the picture; the others stay.</summary>
    public static IReadOnlyList<Point> MoveVertex(IReadOnlyList<Point> points, int index, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (index < 0 || index >= points.Count)
        {
            return points;
        }

        var moved = points.ToArray();
        moved[index] = new Point(Math.Clamp(points[index].X + dx, 0, 1), Math.Clamp(points[index].Y + dy, 0, 1));
        return moved;
    }

    /// <summary>A point added on the edge after <paramref name="index"/> (at <paramref name="at"/>), at most <paramref name="max"/> points.</summary>
    public static IReadOnlyList<Point> InsertVertex(IReadOnlyList<Point> points, int index, Point at, int max)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count >= max || index < 0 || index >= points.Count)
        {
            return points;
        }

        var list = points.ToList();
        list.Insert(index + 1, new Point(Math.Clamp(at.X, 0, 1), Math.Clamp(at.Y, 0, 1)));
        return list;
    }

    /// <summary>The point at <paramref name="index"/> taken out, never below <paramref name="min"/> points.</summary>
    public static IReadOnlyList<Point> RemoveVertex(IReadOnlyList<Point> points, int index, int min)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Count <= min || index < 0 || index >= points.Count ? points : [.. points.Where((_, i) => i != index)];
    }

    /// <summary>Whether <paramref name="point"/> lies inside the polygon (even-odd rule).</summary>
    public static bool Inside(IReadOnlyList<Point> polygon, Point point)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The distance from <paramref name="point"/> to the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    public static double DistanceToSegment(Point point, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = (dx * dx) + (dy * dy);
        var t = length <= 0 ? 0 : Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / length, 0, 1);
        var x = a.X + (t * dx) - point.X;
        var y = a.Y + (t * dy) - point.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    /// <summary>The move of an arrow key (relative to the picture), with Shift the big one; null for any other key.</summary>
    public static Vector? NudgeOf(Key key, KeyModifiers modifiers)
    {
        var step = modifiers.HasFlag(KeyModifiers.Shift) ? BigNudge : Nudge;
        return key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => null,
        };
    }
}
