using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Oadm.Plugins.Shared.Vision.Client;

/// <summary>
/// Where the camera's picture sits in a control: its shape kept and centred, the same letterboxing as the live video below
/// the overlay, so shapes drawn relative to the picture (0 to 1) fit it exactly.
/// </summary>
public static class PictureFit
{
    /// <summary>The shape of a picture of <paramref name="width"/> x <paramref name="height"/>; 16:9 without a size.</summary>
    public static double AspectOf(int width, int height) => width > 0 && height > 0 ? (double)width / height : 16.0 / 9;

    /// <summary>The largest rectangle of <paramref name="aspect"/> inside <paramref name="space"/>, centred.</summary>
    public static Rect Fit(Size space, double aspect)
    {
        if (space.Width <= 0 || space.Height <= 0)
        {
            return default;
        }

        var width = space.Width;
        var height = width / aspect;
        if (height > space.Height)
        {
            height = space.Height;
            width = height * aspect;
        }

        return new Rect((space.Width - width) / 2, (space.Height - height) / 2, width, height);
    }

    /// <summary>The size a picture control asks for: the whole width (640 when unbounded), its height by the picture's shape.</summary>
    public static Size Measure(Size available, double aspect)
    {
        var width = double.IsInfinity(available.Width) ? 640 : available.Width;
        var height = width / aspect;
        if (!double.IsInfinity(available.Height) && height > available.Height)
        {
            height = available.Height;
            width = height * aspect;
        }

        return new Size(width, height);
    }

    /// <summary>A point of the control relative to the picture (0 to 1 each, clamped).</summary>
    public static Point ToRelative(Point point, Rect picture) =>
        picture.Width <= 0 || picture.Height <= 0
            ? default
            : new Point(Math.Clamp((point.X - picture.X) / picture.Width, 0, 1), Math.Clamp((point.Y - picture.Y) / picture.Height, 0, 1));

    /// <summary>A relative point in the control.</summary>
    public static Point ToControl(Point relative, Rect picture) =>
        new(picture.X + (relative.X * picture.Width), picture.Y + (relative.Y * picture.Height));

    /// <summary>A relative rectangle in the control.</summary>
    public static Rect ToControl(double x, double y, double width, double height, Rect picture) =>
        new(picture.X + (x * picture.Width), picture.Y + (y * picture.Height), width * picture.Width, height * picture.Height);
}

/// <summary>The handles of a selected shape on the picture, in pixels: where they are, what a press near one takes, its cursor.</summary>
public static class ShapeHandles
{
    /// <summary>The side of a handle and how near the pointer must be to take it, in pixels.</summary>
    public const double Size = 8;

    public const double Reach = 7;

    /// <summary>A rectangle with a side below this (pixels) is small: the handles' reach would cover it, a press inside moves it.</summary>
    public const double SmallArea = 4 * Reach;

    /// <summary>The eight handles of a rectangle: the corners and the middle of each edge.</summary>
    public static IEnumerable<(AreaHandle Handle, Point At)> OfRect(Rect rect)
    {
        var midX = rect.X + (rect.Width / 2);
        var midY = rect.Y + (rect.Height / 2);
        yield return (AreaHandle.TopLeft, rect.TopLeft);
        yield return (AreaHandle.TopRight, rect.TopRight);
        yield return (AreaHandle.BottomRight, rect.BottomRight);
        yield return (AreaHandle.BottomLeft, rect.BottomLeft);
        yield return (AreaHandle.Top, new Point(midX, rect.Y));
        yield return (AreaHandle.Right, new Point(rect.Right, midY));
        yield return (AreaHandle.Bottom, new Point(midX, rect.Bottom));
        yield return (AreaHandle.Left, new Point(rect.X, midY));
    }

    /// <summary>The handle of <paramref name="rect"/> within reach of <paramref name="point"/>, else <see cref="AreaHandle.None"/>.</summary>
    public static AreaHandle HitRect(Rect rect, Point point)
    {
        foreach (var (handle, at) in OfRect(rect))
        {
            if (Near(point, at))
            {
                return handle;
            }
        }

        return AreaHandle.None;
    }

    /// <summary>The index of the point within reach of <paramref name="point"/>, the last drawn first; -1 when none is.</summary>
    public static int HitVertex(IReadOnlyList<Point> points, Point point)
    {
        ArgumentNullException.ThrowIfNull(points);
        for (var i = points.Count - 1; i >= 0; i--)
        {
            if (Near(point, points[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The edge of a polygon or line near <paramref name="point"/>: the index of its first point, else -1.</summary>
    public static int HitEdge(IReadOnlyList<Point> points, Point point, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        var edges = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < edges; i++)
        {
            if (ShapeGeometry.DistanceToSegment(point, points[i], points[(i + 1) % points.Count]) <= Reach)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool Near(Point point, Point at) => Math.Abs(point.X - at.X) <= Reach && Math.Abs(point.Y - at.Y) <= Reach;

    public static Cursor? CursorOf(AreaHandle handle) => handle switch
    {
        AreaHandle.Move or AreaHandle.Vertex => new Cursor(StandardCursorType.SizeAll),
        AreaHandle.TopLeft => new Cursor(StandardCursorType.TopLeftCorner),
        AreaHandle.TopRight => new Cursor(StandardCursorType.TopRightCorner),
        AreaHandle.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
        AreaHandle.BottomLeft => new Cursor(StandardCursorType.BottomLeftCorner),
        AreaHandle.Top or AreaHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
        AreaHandle.Left or AreaHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
        _ => null,
    };
}

/// <summary>The theme's brushes, numbers and fonts as a control finds them (dark or light), for overlays drawn in code.</summary>
public sealed class OverlayTheme(Control control)
{
    private readonly Control _control = control ?? throw new ArgumentNullException(nameof(control));

    public IBrush Brush(string key) => Find(key) as IBrush ?? Brushes.Gray;

    public double Number(string key, double fallback) => Find(key) is double value ? value : fallback;

    public Typeface Font(string key, FontWeight weight = FontWeight.Normal) => new(Find(key) as FontFamily ?? FontFamily.Default, FontStyle.Normal, weight);

    /// <summary>The theme's small text (11 px), e.g. the tags and labels on a picture.</summary>
    public FormattedText Small(string text, IBrush brush, string font = "Oadm.FontFamily") =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font(font), Number("Oadm.FontSizeSmall", 11), brush);

    /// <summary>The theme's body text (12 px).</summary>
    public FormattedText Body(string text, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font("Oadm.FontFamily", weight), Number("Oadm.FontSizeBody", 12), brush);

    /// <summary>A brush of the same colour at <paramref name="opacity"/>.</summary>
    public static IBrush Tint(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    private object? Find(string key) => _control.TryFindResource(key, _control.ActualThemeVariant, out var value) ? value : null;
}

/// <summary>The parts every picture overlay draws the same way: a shape's name tag, the handles and a hint on the picture.</summary>
public static class ShapePainter
{
    /// <summary>
    /// A name tag above the top left of a shape at <paramref name="corner"/> (inside the picture's top edge when there is no
    /// room above), filled with <paramref name="fill"/>.
    /// </summary>
    public static Rect DrawTag(DrawingContext context, FormattedText text, Point corner, Rect picture, IBrush fill)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);
        var tag = new Rect(corner.X - 1, corner.Y - text.Height - 4, text.Width + 10, text.Height + 4);
        if (tag.Y < picture.Y)
        {
            tag = tag.WithY(corner.Y + 1);
        }

        context.FillRectangle(fill, tag, 3);
        context.DrawText(text, new Point(tag.X + 5, tag.Y + 2));
        return tag;
    }

    /// <summary>The square handles of a selected shape.</summary>
    public static void DrawHandles(DrawingContext context, IEnumerable<Point> handles, IBrush fill, IBrush border)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handles);
        var pen = new Pen(border, 1);
        foreach (var at in handles)
        {
            context.DrawRectangle(fill, pen, new Rect(at.X - (ShapeHandles.Size / 2), at.Y - (ShapeHandles.Size / 2), ShapeHandles.Size, ShapeHandles.Size));
        }
    }

    /// <summary>A hint on the theme's overlay: at the top of the picture, or in its middle.</summary>
    public static void DrawHint(DrawingContext context, OverlayTheme theme, Rect picture, string hint, bool top)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        var text = theme.Body(hint, theme.Brush("Oadm.TextPrimaryBrush"));
        var y = top ? picture.Y + 10 : picture.Y + ((picture.Height - text.Height - 10) / 2);
        var box = new Rect(picture.X + ((picture.Width - text.Width) / 2) - 10, y, text.Width + 20, text.Height + 10);
        context.FillRectangle(theme.Brush("Oadm.OverlayBrush"), box, 4);
        context.DrawText(text, new Point(box.X + 10, box.Y + 5));
    }

    /// <summary>A polygon (closed) or a line through <paramref name="points"/> (in the control).</summary>
    public static StreamGeometry Outline(IReadOnlyList<Point> points, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        if (points.Count > 0)
        {
            ctx.BeginFigure(points[0], closed);
            foreach (var point in points.Skip(1))
            {
                ctx.LineTo(point);
            }

            ctx.EndFigure(closed);
        }

        return geometry;
    }
}
