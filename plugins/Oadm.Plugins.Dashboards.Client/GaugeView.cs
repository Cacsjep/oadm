using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>
/// A Gauge widget's drawing: an arc of 240 degrees from Min (lower left) to Max (lower right), colored in the bands of the
/// thresholds (<see cref="GaugeScale.Bands"/>), labelled ticks with small ticks between them (<see cref="GaugeScale.Ticks"/>)
/// in the color of their band, a marker at the value (stopping at the ends outside the range), and in the middle the value
/// in its color with the unit below. Everything follows the size: labels thin out (every second one) and go away before
/// they would overlap. Colors are theme brushes by name, so dark and light both work.
/// </summary>
public sealed class GaugeView : Control
{
    /// <summary>The arc starts here (degrees, counterclockwise from the right) and turns clockwise by <see cref="Sweep"/>.</summary>
    public const double StartAngle = 210;

    public const double Sweep = 240;

    private const double MinLabelRadius = 48;

    private Widget? _widget;
    private WidgetDisplay _display = WidgetDisplay.Empty;

    /// <summary>How many tick labels the last render drew (tests).</summary>
    internal int LabelsDrawn { get; private set; }

    /// <summary>Where the marker stood at the last render, 0 (Min) to 1 (Max); null without one (tests).</summary>
    internal double? MarkerFraction { get; private set; }

    public void Show(Widget widget, WidgetDisplay display)
    {
        _widget = widget;
        _display = display;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        LabelsDrawn = 0;
        MarkerFraction = null;
        if (_widget is not { } widget || Bounds.Width < 24 || Bounds.Height < 24)
        {
            return;
        }

        var min = widget.Min;
        var max = widget.Max;
        var ticks = GaugeScale.Ticks(min, max);
        if (ticks.Major.Count == 0)
        {
            return;
        }

        var typeface = Typeface(FontWeight.Normal);
        var bold = Typeface(FontWeight.SemiBold);
        var secondary = Brush("Secondary");

        // Size: the arc's radius from the space left after the labels (they go away when it gets too small).
        var width = Bounds.Width;
        var height = Bounds.Height;
        var labelSize = Math.Clamp(Math.Floor(Math.Min(width, height) * 0.06), 9, 14);
        var labels = ticks.Major.Select(v => Text(GaugeScale.Label(v, ticks.Decimals), typeface, labelSize, secondary)).ToList();
        var labelWidth = labels.Max(l => l.Width);
        var labelHeight = labels.Max(l => l.Height);
        var gap = Math.Round(labelSize * 0.5);
        var radius = Radius(width, height, gap, labelWidth, labelHeight);
        var labelEvery = radius < MinLabelRadius ? 0 : LabelStep(radius, labelWidth, ticks.Major.Count);
        if (labelEvery == 0)
        {
            (gap, labelWidth, labelHeight) = (0, 0, 0);
            radius = Radius(width, height, 2, 0, 0);
        }

        if (radius < 10)
        {
            return;
        }

        // The arc spans 1.5 radii in height (it reaches half a radius below the center): centered in the space.
        var top = Math.Round((height - (radius * 1.5 + gap * 1.5 + labelHeight * 1.5)) / 2);
        var center = new Point(Math.Round(width / 2), Math.Round(top + gap + labelHeight + radius));

        var band = Math.Max(1, Math.Round(radius * 0.0175)); // a thin arc: the ticks carry the color
        var tickOuter = radius - band - Math.Max(2, Math.Round(radius * 0.03));
        var major = Math.Max(4, radius * 0.13);
        var minorLength = Math.Max(2, radius * 0.065);
        var bands = GaugeScale.Bands(min, max, widget.Thresholds, widget.Color);

        // Bands of the arc.
        foreach (var part in bands)
        {
            DrawArc(context, center, radius - band / 2, Angle(part.From, min, max), Angle(part.To, min, max), new Pen(Brush(part.Color), band));
        }

        // Ticks in the color of their band.
        foreach (var value in ticks.Minor)
        {
            DrawTick(context, center, Angle(value, min, max), tickOuter, minorLength, Math.Max(1, Math.Round(radius * 0.012)), Brush(BandColor(bands, value)));
        }

        foreach (var value in ticks.Major)
        {
            DrawTick(context, center, Angle(value, min, max), tickOuter, major, Math.Max(1.5, Math.Round(radius * 0.022)), Brush(BandColor(bands, value)));
        }

        // Labels outside the arc, upright.
        if (labelEvery > 0)
        {
            for (var i = 0; i < ticks.Major.Count; i += labelEvery)
            {
                var angle = Angle(ticks.Major[i], min, max) * Math.PI / 180;
                var label = labels[i];
                var distance = radius + gap + label.Height / 2 + label.Width / 2 * Math.Abs(Math.Cos(angle));
                var at = new Point(center.X + distance * Math.Cos(angle), center.Y - distance * Math.Sin(angle));
                context.DrawText(label, new Point(Math.Round(at.X - label.Width / 2), Math.Round(at.Y - label.Height / 2)));
                LabelsDrawn++;
            }
        }

        if (!_display.HasValue)
        {
            return;
        }

        var color = Brush(_display.Color);
        var inner = tickOuter - major - Math.Max(3, radius * 0.04);

        // The marker: a triangle inside the ticks, pointing at the value.
        if (_display.Number is { } number)
        {
            var fraction = GaugeScale.Fraction(number, min, max);
            MarkerFraction = fraction;
            var angle = (StartAngle - fraction * Sweep) * Math.PI / 180;
            var size = Math.Max(6, Math.Round(radius * 0.09));
            var direction = new Vector(Math.Cos(angle), -Math.Sin(angle));
            var across = new Vector(-direction.Y, direction.X);
            var tip = center + direction * inner;
            var baseCenter = center + direction * (inner - size);
            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(baseCenter + across * (size * 0.6));
                g.LineTo(baseCenter - across * (size * 0.6));
                g.EndFigure(true);
            }

            context.DrawGeometry(color, null, triangle);
            inner -= size + Math.Max(2, radius * 0.03);
        }

        // The value in the middle, as large as the room inside the marker allows, the unit below it.
        if (_display.Text is { } text && inner > 8)
        {
            var room = inner * 1.7;
            var valueSize = Math.Max(9, Math.Floor(radius * 0.34));
            var value = Text(text, bold, valueSize, color);
            if (value.Width > room)
            {
                valueSize = Math.Max(9, Math.Floor(valueSize * room / value.Width));
                value = Text(text, bold, valueSize, color);
            }

            var unit = string.IsNullOrEmpty(_display.Unit) ? null : Text(_display.Unit, typeface, Math.Max(9, Math.Floor(valueSize * 0.4)), secondary);
            var block = value.Height + (unit?.Height ?? 0);
            var y = Math.Round(center.Y - block / 2);
            context.DrawText(value, new Point(Math.Round(center.X - value.Width / 2), y));
            if (unit is not null)
            {
                context.DrawText(unit, new Point(Math.Round(center.X - unit.Width / 2), Math.Round(y + value.Height)));
            }
        }
    }

    /// <summary>The largest radius whose arc and labels fit the space.</summary>
    private static double Radius(double width, double height, double gap, double labelWidth, double labelHeight) =>
        Math.Floor(Math.Min(width / 2 - gap - labelWidth, (height - gap * 1.5 - labelHeight * 1.5) / 1.5));

    /// <summary>Every label (1), every second (2) or none (0), so neighbours never touch.</summary>
    private static int LabelStep(double radius, double labelWidth, int count)
    {
        var spacing = radius * Sweep * Math.PI / 180 / Math.Max(1, count - 1);
        return spacing >= labelWidth * 1.3 ? 1 : spacing * 2 >= labelWidth * 1.3 ? 2 : 0;
    }

    /// <summary>The angle of a value on the arc (degrees, counterclockwise from the right).</summary>
    private static double Angle(double value, double min, double max) => StartAngle - GaugeScale.Fraction(value, min, max) * Sweep;

    private static string BandColor(IReadOnlyList<GaugeBand> bands, double value) =>
        bands.LastOrDefault(b => value >= b.From - 1e-9)?.Color ?? bands[0].Color;

    private static void DrawArc(DrawingContext context, Point center, double radius, double from, double to, Pen pen)
    {
        if (from - to < 0.01)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(PointAt(center, radius, from), false);
            g.ArcTo(PointAt(center, radius, to), new Size(radius, radius), 0, from - to > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawTick(DrawingContext context, Point center, double angle, double outer, double length, double thickness, IBrush brush) =>
        context.DrawLine(new Pen(brush, thickness), PointAt(center, outer, angle), PointAt(center, outer - length, angle));

    private static Point PointAt(Point center, double radius, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(angle), center.Y - radius * Math.Sin(angle));
    }

    private Typeface Typeface(FontWeight weight) =>
        this.TryFindResource("Oadm.FontFamily", ActualThemeVariant, out var family) && family is FontFamily f
            ? new Typeface(f, FontStyle.Normal, weight)
            : new Typeface(FontFamily.Default, FontStyle.Normal, weight);

    private IBrush Brush(string color) =>
        this.TryFindResource(WidgetColors.BrushKey(color), ActualThemeVariant, out var brush) && brush is IBrush b ? b : Brushes.Gray;

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
