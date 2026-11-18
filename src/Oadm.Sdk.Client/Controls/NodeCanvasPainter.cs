using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>How a node card's border looks: plain, in the accent (selected, or a link fits), red (the node has an error).</summary>
public enum NodeCardBorder
{
    Normal = 0,

    Accent = 1,

    Error = 2,
}

/// <summary>
/// The one look of every node canvas (Vision Center's pipelines, Provisioning's profiles and a camera's flow): the dotted
/// background, the links as vertical curves, the node cards (colour stripe on the left, icon, name, a line of text, the input
/// dot at the top and the output dot at the bottom, the + below an output), the error directly below a red-bordered card.
/// Colours from the theme (<see cref="CanvasTheme"/>); sizes from <see cref="NodeCanvasGeometry"/>. The canvas controls call
/// these in drawing order; what only one canvas has (Vision Center's eye and control inputs) it draws itself.
/// </summary>
public static class NodeCanvasPainter
{
    /// <summary>The corner radius of a node card.</summary>
    public const double CornerRadius = 8;

    /// <summary>The distance between two dots of the background, in canvas units.</summary>
    public const double DotStep = 22;

    /// <summary>The left edge of a card's text (the icon sits left of it).</summary>
    public const double TextLeft = 38;

    /// <summary>The top of the line below the name.</summary>
    public const double LineTop = 35;

    /// <summary>The canvas's background and its dot grid, which moves and scales with the view.</summary>
    public static void DrawBackground(DrawingContext context, CanvasTheme theme, Rect bounds, CanvasView view)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        context.FillRectangle(theme.Brush("Oadm.CanvasBackgroundBrush"), bounds);
        var step = DotStep * view.Zoom;
        if (step < 6)
        {
            return;
        }

        var dot = CanvasTheme.Tint(theme.Brush("Oadm.InputBorderBrush"), 0.9);
        var radius = Math.Max(0.8, 1.1 * view.Zoom);
        var startX = view.PanX % step;
        var startY = view.PanY % step;
        for (var x = startX; x < bounds.Width; x += step)
        {
            for (var y = startY; y < bounds.Height; y += step)
            {
                context.DrawEllipse(dot, null, new Point(x, y), radius, radius);
            }
        }
    }

    /// <summary>The transform from canvas units to the control (inside <see cref="DrawBackground"/>'s bounds).</summary>
    public static Matrix Transform(CanvasView view) => Matrix.CreateScale(view.Zoom, view.Zoom) * Matrix.CreateTranslation(view.PanX, view.PanY);

    /// <summary>The curve of a link from an output down to an input (<see cref="NodeCanvasGeometry.Curve"/>).</summary>
    public static StreamGeometry Curve(Point from, Point to, bool intoSide = false)
    {
        var (c1, c2) = NodeCanvasGeometry.Curve(from, to, intoSide);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false);
            ctx.CubicBezierTo(c1, c2, to);
            ctx.EndFigure(false);
        }

        return geometry;
    }

    /// <summary>A link: the theme's link colour (1 px), or the accent (2 px) while it is selected.</summary>
    public static void DrawLink(DrawingContext context, CanvasTheme theme, Point start, Point end, bool isSelected, bool intoSide = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        var pen = isSelected ? new Pen(theme.Brush("Oadm.AccentBrush"), 2) : new Pen(LinkBrush(theme), 1);
        context.DrawGeometry(null, pen, Curve(start, end, intoSide));
    }

    /// <summary>The colour of links, ports and the +.</summary>
    public static IBrush LinkBrush(CanvasTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return theme.Brush("Oadm.TextDisabledBrush");
    }

    /// <summary>
    /// A node card: the selection ring, the card with its border, the colour <paramref name="stripe"/> along the left edge, the
    /// icon in that colour and the name (trimmed before <paramref name="nameRight"/>; null: the card's right edge less 10).
    /// </summary>
    public static void DrawCard(
        DrawingContext context,
        Control owner,
        CanvasTheme theme,
        Rect rect,
        IBrush stripe,
        string iconKey,
        string name,
        bool isSelected,
        NodeCardBorder border,
        double? nameRight = null,
        IBrush? iconBrush = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        var accent = theme.Brush("Oadm.AccentBrush");
        var fill = theme.Brush("Oadm.CanvasNodeBrush");
        if (isSelected)
        {
            context.DrawRectangle(null, new Pen(CanvasTheme.Tint(accent, 0.35), 4), rect.Inflate(2), CornerRadius + 2, CornerRadius + 2);
        }

        var pen = border switch
        {
            NodeCardBorder.Error => new Pen(theme.Brush("Oadm.StatusErrorBrush"), 1.5),
            NodeCardBorder.Accent => new Pen(accent, 1.5),
            _ => new Pen(theme.Brush("Oadm.InputBorderBrush"), 1),
        };
        context.DrawRectangle(fill, pen, rect, CornerRadius, CornerRadius);

        // The kind's colour: a stripe along the left edge, inside the rounded corners.
        using (context.PushClip(new RoundedRect(rect, CornerRadius)))
        {
            context.FillRectangle(stripe, new Rect(rect.X, rect.Y, 4, rect.Height));
        }

        DrawIcon(context, owner, iconKey, iconBrush ?? stripe, new Rect(rect.X + 14, rect.Y + 9, 16, 16));
        var right = nameRight ?? rect.Right - 10;
        var text = theme.Body(name, theme.Brush("Oadm.TextPrimaryBrush"), FontWeight.SemiBold);
        text.MaxTextWidth = Math.Max(10, right - (rect.X + TextLeft));
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(text, new Point(rect.X + TextLeft, rect.Y + 9));
    }

    /// <summary>The line of a card below its name (a live value, a result), trimmed to the card.</summary>
    public static void DrawLine(DrawingContext context, CanvasTheme theme, Rect rect, string text, IBrush? brush = null, int maxLines = 1)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var line = theme.Body(text, brush ?? theme.Brush("Oadm.TextSecondaryBrush"));
        line.MaxTextWidth = rect.Width - 24;
        line.MaxLineCount = maxLines;
        line.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(line, new Point(rect.X + 14, rect.Y + LineTop));
    }

    /// <summary>An input or output dot; <paramref name="isLit"/> fills it with the accent (a link being dragged fits).</summary>
    public static void DrawPort(DrawingContext context, CanvasTheme theme, Point at, bool isLit = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        var accent = theme.Brush("Oadm.AccentBrush");
        var link = LinkBrush(theme);
        context.DrawEllipse(isLit ? accent : theme.Brush("Oadm.CanvasNodeBrush"), new Pen(isLit ? accent : link, 1), at, NodeCanvasGeometry.PortRadius - 1, NodeCanvasGeometry.PortRadius - 1);
    }

    /// <summary>The + below an output: a line down from the output dot at <paramref name="output"/> and the box with the icon.</summary>
    public static void DrawPlus(DrawingContext context, Control owner, CanvasTheme theme, Point output, Rect plus)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        var link = LinkBrush(theme);
        context.DrawLine(new Pen(link, 1), new Point(output.X, output.Y + NodeCanvasGeometry.PortRadius), new Point(plus.Center.X, plus.Y));
        context.DrawRectangle(theme.Brush("Oadm.CanvasNodeBrush"), new Pen(link, 1), plus, 4, 4);
        DrawIcon(context, owner, "add", theme.Brush("Oadm.TextSecondaryBrush"), plus.Deflate(5));
    }

    /// <summary>
    /// A message directly below a card (its error in red; a canvas may use another brush), on the canvas's background so a
    /// link down from the output passes behind it, wrapping at <paramref name="maxWidth"/> (null: the card width plus 30).
    /// </summary>
    public static void DrawBelow(DrawingContext context, CanvasTheme theme, Rect rect, string message, IBrush? brush = null, double? maxWidth = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);
        var text = theme.Small(message, brush ?? theme.Brush("Oadm.StatusErrorBrush"));
        text.MaxTextWidth = maxWidth ?? NodeCanvasGeometry.NodeWidth + 30;
        context.FillRectangle(theme.Brush("Oadm.CanvasBackgroundBrush"), new Rect(rect.X - 2, rect.Bottom + 4, text.Width + 4, text.Height + 2), 3);
        context.DrawText(text, new Point(rect.X, rect.Bottom + 5));
    }

    /// <summary>The height <see cref="DrawBelow"/> takes for <paramref name="message"/> (with its gap above).</summary>
    public static double BelowHeight(CanvasTheme theme, string message, double? maxWidth = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var text = theme.Small(message, Brushes.Gray);
        text.MaxTextWidth = maxWidth ?? NodeCanvasGeometry.NodeWidth + 30;
        return text.Height + 7;
    }

    /// <summary>An icon of the theme (<c>Icon.</c> + <paramref name="key"/>) as strokes in <paramref name="brush"/>.</summary>
    public static void DrawIcon(DrawingContext context, Control owner, string key, IBrush brush, Rect at)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.TryFindResource("Icon." + key, owner.ActualThemeVariant, out var value) || value is not Geometry geometry)
        {
            return;
        }

        var scale = Math.Min(at.Width, at.Height) / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
