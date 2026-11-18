using Avalonia;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// Where the parts of a node canvas are, in canvas units (Vision Center's pipelines, Provisioning's profiles): node cards of
/// one size, the graph drawn from top to bottom: the input dot at the top centre of a card, the output dot at its bottom
/// centre, the + below the output (below the error when the node has one), the eye top right; links as vertical curves from
/// an output down to an input; the view (zoom and pan). The canvas controls draw with these (<see cref="NodeCanvasPainter"/>)
/// and their view models find what a press hit with them.
/// </summary>
public static class NodeCanvasGeometry
{
    public const double NodeWidth = 200;

    public const double NodeHeight = 62;

    /// <summary>A row per control input below the live line: its dot on the left edge, its label beside it.</summary>
    public const double ControlRowHeight = 20;

    /// <summary>The height of a card with <paramref name="controlInputs"/> control inputs.</summary>
    public static double HeightFor(int controlInputs) => controlInputs == 0 ? NodeHeight : NodeHeight + (controlInputs * ControlRowHeight) + 4;

    public const double PortRadius = 7;

    /// <summary>A control input's dot: the size of the data dots, centred on the card's left edge.</summary>
    public const double ControlPortRadius = PortRadius - 1;

    /// <summary>A press this near a port takes it.</summary>
    public const double PortReach = 11;

    public const double PlusSize = 22;

    /// <summary>The + sits this far below the node's bottom edge.</summary>
    public const double PlusGap = 18;

    /// <summary>The room of an error below a node (two lines of small text): the + moves below it.</summary>
    public const double ErrorRoom = 34;

    public const double EyeSize = 24;

    /// <summary>Tidy up: the gap between two cards side by side, and one column (a card and its gap).</summary>
    public const double ColumnGap = 40;

    public const double ColumnStep = NodeWidth + ColumnGap;

    /// <summary>Tidy up: the gap between a row's tallest card and the next row: room for a link and its count.</summary>
    public const double RowGap = 56;

    /// <summary>Where a node added after another goes: below it, one card and one gap down.</summary>
    public const double RowStep = NodeHeight + RowGap;

    public const double Margin = 40;

    /// <summary>A press this near a link (screen pixels) takes it.</summary>
    public const double LinkReach = 6;

    public const double MinZoom = 0.25;

    public const double MaxZoom = 2;

    public static Rect NodeRect(double x, double y, double height = NodeHeight) => new(x, y, NodeWidth, height);

    /// <summary>The output dot: the bottom centre of the card.</summary>
    public static Point OutputPort(double x, double y, double height = NodeHeight) => new(x + (NodeWidth / 2), y + height);

    /// <summary>The input dot: the top centre of the card.</summary>
    public static Point InputPort(double x, double y) => new(x + (NodeWidth / 2), y);

    /// <summary>
    /// The + below the output, below the node's error when it has one; beside the links when the output already leads on
    /// (<paramref name="besideLinks"/>), so it never covers their curve and count.
    /// </summary>
    public static Rect PlusRect(double x, double y, bool belowError = false, bool besideLinks = false, double height = NodeHeight) =>
        new(x + ((NodeWidth - PlusSize) / 2) + (besideLinks ? PlusSize + 16 : 0), y + height + PlusGap + (belowError ? ErrorRoom : 0), PlusSize, PlusSize);

    /// <summary>A control input (Reset, Send now, Only while, Second): a dot on the card's left edge in its own row below the live line.</summary>
    public static Point ControlPort(double x, double y, int index = 0) =>
        new(x, y + NodeHeight + (index * ControlRowHeight) + (ControlRowHeight / 2) - 6);

    /// <summary>The + left of an empty control input of the selected node: a click adds a node into that input (a Timer into Reset).</summary>
    public static Rect ControlPlusRect(double x, double y, int index = 0)
    {
        var port = ControlPort(x, y, index);
        const double size = 16;
        return new Rect(port.X - 34 - size, port.Y - (size / 2), size, size);
    }

    public static Rect EyeRect(double x, double y) => new(x + NodeWidth - EyeSize - 6, y + 5, EyeSize, EyeSize);

    /// <summary>The control points of a link's curve from <paramref name="from"/> (an output) down to <paramref name="to"/> (an input).</summary>
    public static (Point C1, Point C2) Curve(Point from, Point to, bool intoSide = false)
    {
        var dy = Math.Max(40, Math.Abs(to.Y - from.Y) / 2);
        if (intoSide)
        {
            // Into a control input: down from the output, then from the left into the card's edge.
            var dx = Math.Max(40, Math.Abs(to.X - from.X) / 2);
            return (new Point(from.X, from.Y + dy), new Point(to.X - dx, to.Y));
        }

        return (new Point(from.X, from.Y + dy), new Point(to.X, to.Y - dy));
    }

    /// <summary>A point on the curve at <paramref name="t"/> (0 to 1).</summary>
    public static Point OnCurve(Point from, Point to, double t, bool intoSide = false)
    {
        var (c1, c2) = Curve(from, to, intoSide);
        var u = 1 - t;
        return new Point(
            (u * u * u * from.X) + (3 * u * u * t * c1.X) + (3 * u * t * t * c2.X) + (t * t * t * to.X),
            (u * u * u * from.Y) + (3 * u * u * t * c1.Y) + (3 * u * t * t * c2.Y) + (t * t * t * to.Y));
    }

    /// <summary>The distance of <paramref name="point"/> to the curve (sampled), in canvas units.</summary>
    public static double DistanceToCurve(Point point, Point from, Point to, bool intoSide = false)
    {
        var best = double.MaxValue;
        var previous = from;
        const int Steps = 24;
        for (var i = 1; i <= Steps; i++)
        {
            var next = OnCurve(from, to, (double)i / Steps, intoSide);
            best = Math.Min(best, DistanceToSegment(point, previous, next));
            previous = next;
        }

        return best;
    }

    public static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

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

    /// <summary>
    /// Fit view: <paramref name="content"/> (canvas units) in the view, at most <paramref name="maxZoom"/>, centred across and
    /// at most <paramref name="topRoom"/> screen pixels below the top (the graph grows downwards).
    /// </summary>
    public static CanvasView Fit(Rect content, Size viewport, double topRoom, double maxZoom = 1)
    {
        var zoom = Math.Clamp(Math.Min(viewport.Width / content.Width, viewport.Height / content.Height), MinZoom, maxZoom);
        return new CanvasView(
            zoom,
            ((viewport.Width - (content.Width * zoom)) / 2) - (content.X * zoom),
            Math.Min(topRoom, (viewport.Height - (content.Height * zoom)) / 2) - (content.Y * zoom));
    }

    /// <summary>
    /// The wheel: zoom by <paramref name="factor"/> around <paramref name="screen"/> (it stays under the pointer), 25 % to
    /// 200 %; null when the zoom stays.
    /// </summary>
    public static CanvasView? ZoomAround(CanvasView view, Point screen, double factor)
    {
        var zoom = Math.Clamp(view.Zoom * factor, MinZoom, MaxZoom);
        if (zoom == view.Zoom)
        {
            return null;
        }

        return new CanvasView(zoom, screen.X - ((screen.X - view.PanX) * zoom / view.Zoom), screen.Y - ((screen.Y - view.PanY) * zoom / view.Zoom));
    }
}

/// <summary>The view of a node canvas: screen = canvas units * <see cref="Zoom"/> + pan.</summary>
public readonly record struct CanvasView(double Zoom, double PanX, double PanY)
{
    /// <summary>A screen point in canvas units.</summary>
    public Point ToWorld(Point screen) => new((screen.X - PanX) / Zoom, (screen.Y - PanY) / Zoom);

    /// <summary>A point in canvas units on the screen.</summary>
    public Point ToScreen(Point world) => new((world.X * Zoom) + PanX, (world.Y * Zoom) + PanY);
}
