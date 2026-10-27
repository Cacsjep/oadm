using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>The colour role of a chart series or bar; the theme gives each role its brush in both appearances.</summary>
public enum ChartColor
{
    /// <summary>The first series (the accent).</summary>
    Primary = 0,

    /// <summary>A second series beside the first.</summary>
    Secondary = 1,

    /// <summary>A value that is fine (the ok green).</summary>
    Ok = 2,

    /// <summary>A value to check (the warning amber).</summary>
    Warning = 3,

    /// <summary>A value that failed (the error red).</summary>
    Error = 4,

    /// <summary>A neutral value (grey).</summary>
    Neutral = 5,
}

/// <summary>One line of a <see cref="LineChart"/>: one value per <see cref="LineChart.Step"/>, NaN = no value (a gap).</summary>
/// <param name="Name">The legend text and the tooltip label.</param>
/// <param name="Values">The values, oldest first.</param>
/// <param name="Color">The colour role.</param>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values, ChartColor Color = ChartColor.Primary);

/// <summary>One bar of a <see cref="BarChart"/>.</summary>
/// <param name="Label">The text under the bar ("25 %").</param>
/// <param name="Value">The bar's value.</param>
/// <param name="Color">The colour role (e.g. Error for a failed step).</param>
public sealed record ChartBar(string Label, double Value, ChartColor Color = ChartColor.Primary);

/// <summary>A named part of a <see cref="LineChart"/>'s time axis (a phase of a check).</summary>
/// <param name="From">The first value index of the part.</param>
/// <param name="To">The index after its last value; beyond the values the part runs to the right edge (a running phase).</param>
/// <param name="Label">The text above the part ("Normal").</param>
public sealed record ChartBand(int From, int To, string Label);

/// <summary>
/// A line chart over time (the Load test's rate, loss and jitter): one or more <see cref="ChartSeries"/>, a value
/// axis with <see cref="Unit"/>, a time axis from <see cref="Step"/>, an optional <see cref="Threshold"/> line, a legend
/// when there are several series and a tooltip with the values under the pointer. Points are reduced to the pixel width
/// (minimum and maximum per pixel column), so long series stay fast. Colours from the theme.
/// </summary>
public sealed class LineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<LineChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<LineChart, string?>(nameof(Unit));

    public static readonly StyledProperty<TimeSpan> StepProperty =
        AvaloniaProperty.Register<LineChart, TimeSpan>(nameof(Step), TimeSpan.FromSeconds(1));

    public static readonly StyledProperty<double?> MaximumProperty = AvaloniaProperty.Register<LineChart, double?>(nameof(Maximum));

    public static readonly StyledProperty<double?> ThresholdProperty = AvaloniaProperty.Register<LineChart, double?>(nameof(Threshold));

    public static readonly StyledProperty<string?> EmptyTextProperty = AvaloniaProperty.Register<LineChart, string?>(nameof(EmptyText));

    public static readonly StyledProperty<IReadOnlyList<ChartBand>?> BandsProperty =
        AvaloniaProperty.Register<LineChart, IReadOnlyList<ChartBand>?>(nameof(Bands));

    /// <summary>A legend names at most this many lines; with more (a line per camera) the hover popup names them.</summary>
    internal const int MaxLegendSeries = 8;

    /// <summary>The hover popup lists at most this many lines (the highest values first when there are more).</summary>
    internal const int MaxHoverSeries = 8;

    private Point? _pointer;
    private LineCache? _cache;

    static LineChart()
    {
        AffectsRender<LineChart>(SeriesProperty, UnitProperty, StepProperty, MaximumProperty, ThresholdProperty, EmptyTextProperty, BandsProperty);
    }

    /// <summary>The lines; a new list redraws (bind a new list per update, never change one in place).</summary>
    public IReadOnlyList<ChartSeries>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    /// <summary>The value unit on the axis and in the tooltip ("Mbit/s", "%", "ms").</summary>
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>The time between two values (default 1 s).</summary>
    public TimeSpan Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    /// <summary>The top of the value axis; null = from the values (rounded up).</summary>
    public double? Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>A dashed limit line (e.g. 1 % loss); null = none.</summary>
    public double? Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }

    /// <summary>The line shown while there are no values ("No values yet").</summary>
    public string? EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    /// <summary>Named parts of the time axis (the phases), each labelled above the plot and parted by a thin line; null = none.</summary>
    public IReadOnlyList<ChartBand>? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    /// <summary>The points the lines were drawn with at the last render (tests: the reduction keeps it bounded).</summary>
    internal int LastPointCount { get; private set; }

    /// <summary>The hover popup's lines at the last render; empty without a pointer over the plot (tests).</summary>
    internal IReadOnlyList<string> LastHoverLines { get; private set; } = [];

    /// <summary>The value axis of the last render (tests).</summary>
    internal ChartAxis? LastAxis { get; private set; }

    /// <summary>The legend's names at the last render, "+N more" last when not all fit (tests).</summary>
    internal IReadOnlyList<string> LastLegend { get; private set; } = [];

    /// <summary>The band edges drawn at the last render (tests).</summary>
    internal int LastBandEdges { get; private set; }

    /// <summary>The band labels drawn at the last render with their boxes (tests: they never touch).</summary>
    internal IReadOnlyList<(string Text, Rect Box)> LastBandLabels { get; private set; } = [];

    /// <summary>The time labels of the last render (tests).</summary>
    internal IReadOnlyList<string> LastTimeLabels { get; private set; } = [];

    /// <summary>What a screen reader says when the page gave no automation name.</summary>
    internal string Summary
    {
        get
        {
            var series = Series ?? [];
            if (series.All(s => s.Values.Count == 0))
            {
                return string.IsNullOrEmpty(EmptyText) ? "Line chart" : EmptyText;
            }

            if (series.Count > 4)
            {
                return $"Line chart, {series.Count:N0} lines";
            }

            var last = series.Select(s =>
            {
                var value = s.Values.LastOrDefault(double.IsFinite, double.NaN);
                var text = ChartScale.FormatValue(value, Unit);
                return string.IsNullOrEmpty(s.Name) ? text : $"{s.Name} {text}";
            });
            return "Line chart: " + string.Join(", ", last);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, () => Summary);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        LastHoverLines = [];
        LastTimeLabels = [];
        LastLegend = [];
        LastBandEdges = 0;
        LastBandLabels = [];
        LastAxis = null;
        var size = Bounds.Size;
        if (size.Width < 40 || size.Height < 40)
        {
            return;
        }

        context.FillRectangle(Brushes.Transparent, new Rect(size)); // the whole chart takes the pointer
        var paint = new ChartPaint(this);
        var series = Series ?? [];
        var count = series.Count == 0 ? 0 : series.Max(s => s.Values.Count);
        if (count == 0)
        {
            LastPointCount = 0;
            paint.DrawEmpty(context, EmptyText, size);
            return;
        }

        // Rows: unit and legend, the band labels, the plot, the time labels.
        var lineHeight = paint.Small("0:00").Height;
        var unit = string.IsNullOrEmpty(Unit) ? null : paint.Small(Unit);
        var legend = series.Count is > 1 and <= MaxLegendSeries ? Legend(paint, series, size.Width - (unit?.Width ?? 0) - 24) : null;
        var top = unit is not null || legend is not null ? lineHeight + ChartPaint.Gap : 0;
        var bands = Bands ?? [];
        var bandTop = top;
        if (bands.Count > 0)
        {
            top += lineHeight + 4;
        }

        top += Math.Ceiling(lineHeight / 2); // the top grid label is centred on its line
        var timeWidth = paint.Small("00:00").Width;
        var plotHeight = size.Height - top - lineHeight - ChartPaint.Gap;
        if (plotHeight < 16)
        {
            return;
        }

        var axis = Axis(series, (int)Math.Clamp(Math.Floor(plotHeight / 40), 2, 6));
        LastAxis = axis;
        var left = paint.AxisLabelWidth(axis) + 8;
        var plot = new Rect(left, top, Math.Max(1, size.Width - left - Math.Ceiling(timeWidth / 2)), plotHeight);
        if (plot.Width < 16)
        {
            return;
        }

        double Y(double v) => plot.Bottom - (v - axis.Min) / (axis.Max - axis.Min) * plot.Height;

        if (unit is not null)
        {
            context.DrawText(unit, new Point(0, 0));
        }

        if (legend is not null)
        {
            LastLegend = legend.Names;
            DrawLegend(context, legend, size.Width);
        }

        paint.DrawValueAxis(context, axis, plot, Y);
        DrawBands(context, paint, bands, count, plot, bandTop);
        paint.DrawThreshold(context, Threshold, axis, plot, Y);

        // The lines, the first series on top.
        var lines = Lines(paint, series, count, plot, axis);
        using (context.PushClip(plot.Inflate(new Thickness(2, 2, 2, 2))))
        using (context.PushOpacity(series.Count > 20 ? 0.6 : 1)) // many thin lines show their density
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                context.DrawGeometry(null, lines[i].Pen, lines[i].Geometry);
            }
        }

        DrawTimeAxis(context, paint, count, plot, size.Width, timeWidth);

        if (_pointer is { } pointer && pointer.X >= plot.Left - 4 && pointer.X <= plot.Right + 4 && pointer.Y >= 0 && pointer.Y <= size.Height)
        {
            DrawHover(context, paint, series, bands, count, plot, axis, pointer, size);
        }
    }

    private ChartAxis Axis(IReadOnlyList<ChartSeries> series, int ticks)
    {
        double low = 0, high = double.NegativeInfinity;
        foreach (var s in series)
        {
            foreach (var v in s.Values)
            {
                if (double.IsFinite(v))
                {
                    low = Math.Min(low, v);
                    high = Math.Max(high, v);
                }
            }
        }

        if (Threshold is { } limit && double.IsFinite(limit))
        {
            high = Math.Max(high, limit);
        }

        return Maximum is { } max && double.IsFinite(max) && max > low
            ? ChartScale.Nice(low, max, ticks, fixedMax: true)
            : ChartScale.Nice(low, double.IsFinite(high) ? high : low + 1, ticks);
    }

    private IReadOnlyList<(StreamGeometry Geometry, Pen Pen)> Lines(ChartPaint paint, IReadOnlyList<ChartSeries> series, int count, Rect plot, ChartAxis axis)
    {
        if (_cache is { } cache && ReferenceEquals(cache.Series, series) && cache.Plot == plot && cache.Axis == axis && cache.Variant == ActualThemeVariant)
        {
            return cache.Lines; // a hover redraws without rebuilding the lines
        }

        var thickness = series.Count <= 6 ? 1.5 : 1;
        var lines = new List<(StreamGeometry, Pen)>(series.Count);
        var points = 0;
        double Y(double v) => plot.Bottom - (v - axis.Min) / (axis.Max - axis.Min) * plot.Height;
        foreach (var s in series)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var open = false;
                var single = default(Point?);
                points += ChartScale.Reduce(s.Values, count, plot, Y, (point, start) =>
                {
                    if (start)
                    {
                        if (open)
                        {
                            End(g, single);
                        }

                        g.BeginFigure(point, false);
                        open = true;
                        single = point;
                    }
                    else
                    {
                        g.LineTo(point);
                        single = null;
                    }
                });
                if (open)
                {
                    End(g, single);
                }
            }

            lines.Add((geometry, new Pen(paint.Of(s.Color), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)));
        }

        LastPointCount = points;
        _cache = new LineCache(series, plot, axis, ActualThemeVariant, lines);
        return lines;

        // A value alone between gaps shows as a dot (a round cap on a tiny line).
        static void End(StreamGeometryContext g, Point? single)
        {
            if (single is { } p)
            {
                g.LineTo(new Point(p.X + 0.01, p.Y));
            }

            g.EndFigure(false);
        }
    }

    private static LegendLayout Legend(ChartPaint paint, IReadOnlyList<ChartSeries> series, double room)
    {
        const double dot = 8, dotGap = 5, spacing = 14;
        var items = new List<(FormattedText Text, IBrush Brush)>();
        var names = new List<string>();
        var width = 0.0;
        for (var i = 0; i < series.Count; i++)
        {
            var text = paint.Small(series[i].Name, paint.Text);
            var item = dot + dotGap + text.Width + (items.Count > 0 ? spacing : 0);
            var rest = series.Count - i - 1;
            var more = rest > 0 ? paint.Small($"+{rest:N0} more").Width + spacing : 0;
            if (width + item + more > room)
            {
                var label = $"+{series.Count - i:N0} more";
                names.Add(label);
                return new LegendLayout(items, paint.Small(label), names);
            }

            items.Add((text, paint.Of(series[i].Color)));
            names.Add(series[i].Name);
            width += item;
        }

        return new LegendLayout(items, null, names);
    }

    private static void DrawLegend(DrawingContext context, LegendLayout legend, double right)
    {
        const double dot = 8, dotGap = 5, spacing = 14;
        var width = legend.Items.Sum(i => dot + dotGap + i.Text.Width) + spacing * Math.Max(0, legend.Items.Count - 1)
            + (legend.More is { } m ? m.Width + (legend.Items.Count > 0 ? spacing : 0) : 0);
        var x = Math.Round(right - width);
        foreach (var (text, brush) in legend.Items)
        {
            context.DrawEllipse(brush, null, new Point(x + dot / 2, text.Height / 2), dot / 2 - 0.5, dot / 2 - 0.5);
            x += dot + dotGap;
            context.DrawText(text, new Point(x, 0));
            x += text.Width + spacing;
        }

        if (legend.More is { } more)
        {
            context.DrawText(more, new Point(x, 0));
        }
    }

    /// <summary>
    /// The phases: a thin line at every band start (always), and the labels left to right, each at its band's start; a
    /// label that would touch the one before or run past the plot is left out (narrow bands share the room).
    /// </summary>
    private void DrawBands(DrawingContext context, ChartPaint paint, IReadOnlyList<ChartBand> bands, int count, Rect plot, double labelTop)
    {
        const double labelGap = 8;
        var labels = new List<(string Text, Rect Box)>();
        var edges = new List<double>();
        var free = double.NegativeInfinity;
        foreach (var band in bands)
        {
            if (band.From < 0 || band.From >= count || band.To <= band.From)
            {
                continue;
            }

            var start = BandEdge(band.From, count, plot);
            if (band.From > 0)
            {
                edges.Add(ChartPaint.Snap(start));
            }

            var label = paint.Small(band.Label);
            var left = Math.Round(band.From > 0 ? start + 5 : plot.Left);
            if (left >= free && left + label.Width <= plot.Right)
            {
                context.DrawText(label, new Point(left, labelTop));
                labels.Add((band.Label, new Rect(left, labelTop, label.Width, label.Height)));
                free = left + label.Width + labelGap;
            }
        }

        // An edge crosses the label row only where no label is.
        var pen = new Pen(paint.Grid, 1);
        foreach (var x in edges)
        {
            var covered = labels.Any(l => x >= l.Box.Left - 2 && x <= l.Box.Right + 2);
            context.DrawLine(pen, new Point(x, covered ? plot.Top : labelTop), new Point(x, plot.Bottom));
        }

        LastBandEdges = edges.Count;
        LastBandLabels = labels;
    }

    /// <summary>Where a band starts: halfway between its first value and the one before.</summary>
    private static double BandEdge(int index, int count, Rect plot) =>
        index <= 0 ? plot.Left : (ChartScale.X(index - 1, count, plot) + ChartScale.X(index, count, plot)) / 2;

    private void DrawTimeAxis(DrawingContext context, ChartPaint paint, int count, Rect plot, double width, double timeWidth)
    {
        var maxLabels = Math.Max(1, (int)Math.Floor(plot.Width / (timeWidth + 32)) + 1);
        var every = ChartScale.TimeLabelEvery(count, Step, maxLabels);
        var labels = new List<string>();
        for (var i = 0; i < count; i += every)
        {
            var text = ChartScale.FormatTime(Step * i);
            var label = paint.Small(text);
            var x = Math.Clamp(ChartScale.X(i, count, plot) - label.Width / 2, 0, width - label.Width);
            context.DrawText(label, new Point(Math.Round(x), Math.Round(plot.Bottom + ChartPaint.Gap - 2)));
            labels.Add(text);
        }

        LastTimeLabels = labels;
    }

    private void DrawHover(DrawingContext context, ChartPaint paint, IReadOnlyList<ChartSeries> series, IReadOnlyList<ChartBand> bands,
        int count, Rect plot, ChartAxis axis, Point pointer, Size size)
    {
        var index = ChartScale.IndexAt(pointer.X, count, plot);
        var x = ChartPaint.Snap(ChartScale.X(index, count, plot));
        context.DrawLine(new Pen(paint.Axis, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));

        var values = new List<(ChartSeries Series, double Value)>(series.Count);
        foreach (var s in series)
        {
            if (index < s.Values.Count && double.IsFinite(s.Values[index]))
            {
                values.Add((s, s.Values[index]));
            }
        }

        if (values.Count <= MaxHoverSeries * 2)
        {
            foreach (var (s, v) in values)
            {
                var at = new Point(x, plot.Bottom - (Math.Clamp(v, axis.Min, axis.Max) - axis.Min) / (axis.Max - axis.Min) * plot.Height);
                context.DrawEllipse(paint.Of(s.Color), new Pen(paint.Card, 1.5), at, 3.5, 3.5);
            }
        }

        var lines = HoverLines(paint, series.Count, values, bands, index);
        LastHoverLines = lines.Select(l => l.Text).ToList();
        paint.DrawPopup(context, lines, x, plot, size);
    }

    private List<ChartHoverLine> HoverLines(ChartPaint paint, int seriesCount, List<(ChartSeries Series, double Value)> values, IReadOnlyList<ChartBand> bands, int index)
    {
        var time = ChartScale.FormatTime(Step * index);
        var band = bands.LastOrDefault(b => index >= b.From && index < b.To);
        var lines = new List<ChartHoverLine> { new(band is null ? time : $"{band.Label}, {time}", null, IsTitle: true) };
        if (values.Count == 0)
        {
            lines.Add(new ChartHoverLine("No value", null));
            return lines;
        }

        var shown = values.Count > MaxHoverSeries ? values.OrderByDescending(v => v.Value).Take(MaxHoverSeries).ToList() : values;
        foreach (var (s, v) in shown)
        {
            var text = ChartScale.FormatValue(v, Unit);
            lines.Add(new ChartHoverLine(string.IsNullOrEmpty(s.Name) ? text : $"{s.Name}: {text}", seriesCount > 1 ? paint.Of(s.Color) : null));
        }

        if (values.Count > shown.Count)
        {
            lines.Add(new ChartHoverLine($"{values.Count - shown.Count:N0} more", null));
        }

        return lines;
    }

    private sealed record LegendLayout(List<(FormattedText Text, IBrush Brush)> Items, FormattedText? More, List<string> Names);

    private sealed record LineCache(
        IReadOnlyList<ChartSeries> Series, Rect Plot, ChartAxis Axis, Avalonia.Styling.ThemeVariant Variant, IReadOnlyList<(StreamGeometry Geometry, Pen Pen)> Lines);
}

/// <summary>
/// A bar chart of a few named values (the Load test's ramp steps): one bar per <see cref="ChartBar"/>, a value
/// axis with <see cref="Unit"/>, an optional <see cref="Threshold"/> line, the value on hover. Colours from the theme.
/// </summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartBar>?> BarsProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<ChartBar>?>(nameof(Bars));

    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<BarChart, string?>(nameof(Unit));

    public static readonly StyledProperty<double?> MaximumProperty = AvaloniaProperty.Register<BarChart, double?>(nameof(Maximum));

    public static readonly StyledProperty<double?> ThresholdProperty = AvaloniaProperty.Register<BarChart, double?>(nameof(Threshold));

    public static readonly StyledProperty<string?> EmptyTextProperty = AvaloniaProperty.Register<BarChart, string?>(nameof(EmptyText));

    private Point? _pointer;

    static BarChart()
    {
        AffectsRender<BarChart>(BarsProperty, UnitProperty, MaximumProperty, ThresholdProperty, EmptyTextProperty);
    }

    /// <summary>The bars, left to right; a new list redraws.</summary>
    public IReadOnlyList<ChartBar>? Bars { get => GetValue(BarsProperty); set => SetValue(BarsProperty, value); }

    /// <summary>The value unit on the axis and on hover.</summary>
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>The top of the value axis; null = from the values.</summary>
    public double? Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>A dashed limit line; null = none.</summary>
    public double? Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }

    /// <summary>The line shown while there are no bars.</summary>
    public string? EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    /// <summary>The hover popup's lines at the last render; empty without a pointer over a bar (tests).</summary>
    internal IReadOnlyList<string> LastHoverLines { get; private set; } = [];

    /// <summary>The labels drawn under the bars at the last render (tests).</summary>
    internal IReadOnlyList<string> LastLabels { get; private set; } = [];

    /// <summary>The value axis of the last render (tests).</summary>
    internal ChartAxis? LastAxis { get; private set; }

    /// <summary>What a screen reader says when the page gave no automation name.</summary>
    internal string Summary
    {
        get
        {
            var bars = Bars ?? [];
            if (bars.Count == 0)
            {
                return string.IsNullOrEmpty(EmptyText) ? "Bar chart" : EmptyText;
            }

            return "Bar chart: " + string.Join(", ", bars.Take(12).Select(b => $"{b.Label} {ChartScale.FormatValue(b.Value, Unit)}"))
                + (bars.Count > 12 ? $", {bars.Count - 12:N0} more" : string.Empty);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, () => Summary);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        LastHoverLines = [];
        LastLabels = [];
        LastAxis = null;
        var size = Bounds.Size;
        if (size.Width < 40 || size.Height < 40)
        {
            return;
        }

        context.FillRectangle(Brushes.Transparent, new Rect(size));
        var paint = new ChartPaint(this);
        var bars = Bars ?? [];
        if (bars.Count == 0)
        {
            paint.DrawEmpty(context, EmptyText, size);
            return;
        }

        var lineHeight = paint.Small("0").Height;
        var unit = string.IsNullOrEmpty(Unit) ? null : paint.Small(Unit);
        var top = (unit is not null ? lineHeight + ChartPaint.Gap : 0) + Math.Ceiling(lineHeight / 2);
        var plotHeight = size.Height - top - lineHeight - ChartPaint.Gap;
        if (plotHeight < 16)
        {
            return;
        }

        var axis = Axis(bars, (int)Math.Clamp(Math.Floor(plotHeight / 40), 2, 6));
        LastAxis = axis;
        var left = paint.AxisLabelWidth(axis) + 8;
        var plot = new Rect(left, top, Math.Max(1, size.Width - left - 2), plotHeight);
        if (plot.Width < 16)
        {
            return;
        }

        double Y(double v) => plot.Bottom - (Math.Clamp(v, axis.Min, axis.Max) - axis.Min) / (axis.Max - axis.Min) * plot.Height;

        if (unit is not null)
        {
            context.DrawText(unit, new Point(0, 0));
        }

        var slot = plot.Width / bars.Count;
        var barWidth = Math.Clamp(Math.Floor(slot * 0.56), 2, 56);
        var hovered = _pointer is { } p && p.X >= plot.Left && p.X < plot.Right && p.Y >= 0 && p.Y <= size.Height
            ? Math.Clamp((int)Math.Floor((p.X - plot.Left) / slot), 0, bars.Count - 1)
            : -1;
        if (hovered >= 0)
        {
            var column = Math.Min(slot - 2, barWidth + 16);
            var center = plot.Left + slot * (hovered + 0.5);
            context.FillRectangle(paint.Hover, new Rect(Math.Round(center - column / 2), plot.Top, Math.Round(column), plot.Height), 4);
        }

        paint.DrawValueAxis(context, axis, plot, Y);

        var zero = Y(0);
        for (var i = 0; i < bars.Count; i++)
        {
            var value = bars[i].Value;
            if (!double.IsFinite(value))
            {
                continue;
            }

            var center = plot.Left + slot * (i + 0.5);
            var barLeft = Math.Round(center - barWidth / 2);
            var barRight = barLeft + barWidth;
            var end = Y(value);
            var (high, low) = end < zero ? (end, zero) : (zero, end);
            if (low - high < 0.5)
            {
                continue;
            }

            context.DrawGeometry(paint.Of(bars[i].Color), null, Bar(barLeft, barRight, Math.Round(high), Math.Round(low), end < zero));
        }

        paint.DrawThreshold(context, Threshold, axis, plot, Y);
        DrawLabels(context, paint, bars, plot, slot);

        if (hovered >= 0)
        {
            var bar = bars[hovered];
            var lines = new List<ChartHoverLine>
            {
                new(bar.Label, null, IsTitle: true),
                new(ChartScale.FormatValue(bar.Value, Unit), paint.Of(bar.Color)),
            };
            LastHoverLines = lines.Select(l => l.Text).ToList();
            paint.DrawPopup(context, lines, plot.Left + slot * (hovered + 0.5) + barWidth / 2, plot, size);
        }
    }

    private ChartAxis Axis(IReadOnlyList<ChartBar> bars, int ticks)
    {
        double low = 0, high = double.NegativeInfinity;
        foreach (var bar in bars)
        {
            if (double.IsFinite(bar.Value))
            {
                low = Math.Min(low, bar.Value);
                high = Math.Max(high, bar.Value);
            }
        }

        if (Threshold is { } limit && double.IsFinite(limit))
        {
            high = Math.Max(high, limit);
        }

        return Maximum is { } max && double.IsFinite(max) && max > low
            ? ChartScale.Nice(low, max, ticks, fixedMax: true)
            : ChartScale.Nice(low, double.IsFinite(high) && high > low ? high : low + 1, ticks);
    }

    /// <summary>A bar with its outer corners rounded (the top of a positive bar, the bottom of a negative one).</summary>
    private static StreamGeometry Bar(double left, double right, double top, double bottom, bool up)
    {
        var radius = Math.Min(3, Math.Min((right - left) / 2, bottom - top));
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        var corner = new Size(radius, radius);
        if (up)
        {
            g.BeginFigure(new Point(left, bottom), true);
            g.LineTo(new Point(left, top + radius));
            g.ArcTo(new Point(left + radius, top), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(right - radius, top));
            g.ArcTo(new Point(right, top + radius), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(right, bottom));
        }
        else
        {
            g.BeginFigure(new Point(left, top), true);
            g.LineTo(new Point(right, top));
            g.LineTo(new Point(right, bottom - radius));
            g.ArcTo(new Point(right - radius, bottom), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(left + radius, bottom));
            g.ArcTo(new Point(left, bottom - radius), corner, 0, false, SweepDirection.Clockwise);
        }

        g.EndFigure(true);
        return geometry;
    }

    /// <summary>The labels under the bars; every second (third, ...) when they would touch.</summary>
    private void DrawLabels(DrawingContext context, ChartPaint paint, IReadOnlyList<ChartBar> bars, Rect plot, double slot)
    {
        var texts = bars.Select(b => paint.Small(b.Label)).ToList();
        var widest = texts.Max(t => t.Width);
        var every = Math.Max(1, (int)Math.Ceiling((widest + 8) / slot));
        var drawn = new List<string>();
        for (var i = 0; i < bars.Count; i += every)
        {
            var center = plot.Left + slot * (i + 0.5);
            var x = Math.Clamp(center - texts[i].Width / 2, 0, Bounds.Width - texts[i].Width);
            context.DrawText(texts[i], new Point(Math.Round(x), Math.Round(plot.Bottom + ChartPaint.Gap - 2)));
            drawn.Add(bars[i].Label);
        }

        LastLabels = drawn;
    }
}
