using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Geometry;

/// <summary>
/// Inside, overlap and crossing tests of Area and Line (docs/spec/vision-center.md "Nodes"). Pure, in coordinates relative
/// to the picture: 0 to 1 on both axes, (0, 0) top left, y down.
/// </summary>
public static class VisionGeometry
{
    /// <summary>Whether <paramref name="p"/> lies inside the polygon (even-odd rule; a point on an edge may go either way).</summary>
    public static bool Inside(RelativePoint p, IReadOnlyList<RelativePoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(polygon);
        return Inside(p.X, p.Y, polygon);
    }

    public static bool Inside(double x, double y, IReadOnlyList<RelativePoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var n = polygon.Count;
        if (n < 3)
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Whether the box and the polygon share any area: a corner of one inside the other, or crossing edges.</summary>
    public static bool Overlaps(RelativeBox box, IReadOnlyList<RelativePoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(polygon);
        var n = polygon.Count;
        if (n < 3)
        {
            return false;
        }

        // A corner of the polygon in the box.
        foreach (var p in polygon)
        {
            if (p.X >= box.X && p.X <= box.Right && p.Y >= box.Y && p.Y <= box.Bottom)
            {
                return true;
            }
        }

        // A corner of the box in the polygon.
        if (Inside(box.X, box.Y, polygon) || Inside(box.Right, box.Y, polygon)
            || Inside(box.X, box.Bottom, polygon) || Inside(box.Right, box.Bottom, polygon))
        {
            return true;
        }

        // An edge of the polygon crosses an edge of the box.
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            if (SegmentsIntersect(a.X, a.Y, b.X, b.Y, box.X, box.Y, box.Right, box.Y)
                || SegmentsIntersect(a.X, a.Y, b.X, b.Y, box.Right, box.Y, box.Right, box.Bottom)
                || SegmentsIntersect(a.X, a.Y, b.X, b.Y, box.Right, box.Bottom, box.X, box.Bottom)
                || SegmentsIntersect(a.X, a.Y, b.X, b.Y, box.X, box.Bottom, box.X, box.Y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Intersection over union of two boxes, 0 to 1.</summary>
    public static double IoU(RelativeBox a, RelativeBox b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return IoU(a.X, a.Y, a.Right, a.Bottom, b.X, b.Y, b.Right, b.Bottom);
    }

    /// <summary>IoU of two boxes given as left, top, right, bottom.</summary>
    public static double IoU(double ax1, double ay1, double ax2, double ay2, double bx1, double by1, double bx2, double by2)
    {
        var w = Math.Min(ax2, bx2) - Math.Max(ax1, bx1);
        if (w <= 0)
        {
            return 0;
        }

        var h = Math.Min(ay2, by2) - Math.Max(ay1, by1);
        if (h <= 0)
        {
            return 0;
        }

        var inter = w * h;
        var union = (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>
    /// The side of <paramref name="p"/> to the directed line from <paramref name="a"/> to <paramref name="b"/> as seen on the
    /// picture (y down): positive on its right, negative on its left, 0 on it. For a line drawn from left to right, below
    /// it is the right side.
    /// </summary>
    public static double Side(RelativePoint a, RelativePoint b, double px, double py)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);
    }

    /// <summary>
    /// Whether the step from <paramref name="from"/> to <paramref name="to"/> crosses the segment <paramref name="a"/> to
    /// <paramref name="b"/>, and how: <see cref="CrossDirection.In"/> from the segment's left to its right (see
    /// <see cref="Side"/>), <see cref="CrossDirection.Out"/> back. A step that only touches the segment, or starts on it,
    /// does not cross; one that ends on it crosses (the next step then starts on it).
    /// </summary>
    public static CrossDirection? Crossing(RelativePoint from, RelativePoint to, RelativePoint a, RelativePoint b)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var s0 = Side(a, b, from.X, from.Y);
        var s1 = Side(a, b, to.X, to.Y);
        if (s0 == 0 || ((s0 < 0) == (s1 < 0) && s1 != 0))
        {
            return null;
        }

        // The step's line must separate the segment's ends.
        var t0 = Side(from, to, a.X, a.Y);
        var t1 = Side(from, to, b.X, b.Y);
        if ((t0 > 0 && t1 > 0) || (t0 < 0 && t1 < 0))
        {
            return null;
        }

        if (t0 == 0 && t1 == 0)
        {
            return null; // collinear
        }

        return s0 < 0 ? CrossDirection.In : CrossDirection.Out;
    }

    /// <summary>The first segment of the polyline <paramref name="line"/> the step crosses, and how; null: none.</summary>
    public static CrossDirection? Crossing(RelativePoint from, RelativePoint to, IReadOnlyList<RelativePoint> line)
    {
        ArgumentNullException.ThrowIfNull(line);
        for (var i = 1; i < line.Count; i++)
        {
            var c = Crossing(from, to, line[i - 1], line[i]);
            if (c is not null)
            {
                return c;
            }
        }

        return null;
    }

    private static bool SegmentsIntersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        var d1 = Cross(cx, cy, dx, dy, ax, ay);
        var d2 = Cross(cx, cy, dx, dy, bx, by);
        var d3 = Cross(ax, ay, bx, by, cx, cy);
        var d4 = Cross(ax, ay, bx, by, dx, dy);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(double ax, double ay, double bx, double by, double px, double py) =>
        (bx - ax) * (py - ay) - (by - ay) * (px - ax);
}
