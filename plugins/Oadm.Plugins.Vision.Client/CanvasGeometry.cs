using Avalonia;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// Where the parts of the canvas are, in canvas units (docs/spec/vision-center.md "Page"): node cards of one size, the input
/// dot on the left edge, the output dot on the right edge, the + right of the output, the eye top right; links as curves from
/// an output to an input. The canvas control draws with these and its view model finds what a press hit with them.
/// </summary>
public static class CanvasGeometry
{
    public const double NodeWidth = 200;

    public const double NodeHeight = 62;

    /// <summary>The ports sit this far below the node's top.</summary>
    public const double PortY = 31;

    public const double PortRadius = 7;

    /// <summary>A press this near a port takes it.</summary>
    public const double PortReach = 11;

    public const double PlusSize = 22;

    /// <summary>The + sits this far right of the node's right edge.</summary>
    public const double PlusGap = 18;

    public const double EyeSize = 24;

    /// <summary>The columns and rows of Tidy up.</summary>
    public const double ColumnStep = 260;

    public const double RowStep = 110;

    public const double Margin = 40;

    /// <summary>A press this near a link (screen pixels) takes it.</summary>
    public const double LinkReach = 6;

    public const double MinZoom = 0.25;

    public const double MaxZoom = 2;

    public static Rect NodeRect(double x, double y) => new(x, y, NodeWidth, NodeHeight);

    public static Point OutputPort(double x, double y) => new(x + NodeWidth, y + PortY);

    public static Point InputPort(double x, double y) => new(x, y + PortY);

    public static Rect PlusRect(double x, double y) => new(x + NodeWidth + PlusGap, y + PortY - (PlusSize / 2), PlusSize, PlusSize);

    public static Rect EyeRect(double x, double y) => new(x + NodeWidth - EyeSize - 6, y + 5, EyeSize, EyeSize);

    /// <summary>The control points of a link's curve from <paramref name="from"/> (an output) to <paramref name="to"/> (an input).</summary>
    public static (Point C1, Point C2) Curve(Point from, Point to)
    {
        var dx = Math.Max(40, Math.Abs(to.X - from.X) / 2);
        return (new Point(from.X + dx, from.Y), new Point(to.X - dx, to.Y));
    }

    /// <summary>A point on the curve at <paramref name="t"/> (0 to 1).</summary>
    public static Point OnCurve(Point from, Point to, double t)
    {
        var (c1, c2) = Curve(from, to);
        var u = 1 - t;
        return new Point(
            (u * u * u * from.X) + (3 * u * u * t * c1.X) + (3 * u * t * t * c2.X) + (t * t * t * to.X),
            (u * u * u * from.Y) + (3 * u * u * t * c1.Y) + (3 * u * t * t * c2.Y) + (t * t * t * to.Y));
    }

    /// <summary>The distance of <paramref name="point"/> to the curve (sampled), in canvas units.</summary>
    public static double DistanceToCurve(Point point, Point from, Point to)
    {
        var best = double.MaxValue;
        var previous = from;
        const int Steps = 24;
        for (var i = 1; i <= Steps; i++)
        {
            var next = OnCurve(from, to, (double)i / Steps);
            best = Math.Min(best, Oadm.Plugins.Shared.Vision.Client.ShapeGeometry.DistanceToSegment(point, previous, next));
            previous = next;
        }

        return best;
    }

    public static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
