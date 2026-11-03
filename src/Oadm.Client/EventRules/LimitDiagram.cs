using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Client.EventRules;

/// <summary>Which limit of a rule a <see cref="LimitDiagram"/> shows.</summary>
public enum RuleLimit
{
    /// <summary>Dead time: after a run, the device's next events are ignored for that many seconds.</summary>
    DeadTime,

    /// <summary>Max per minute: at most that many runs a minute, further events are skipped.</summary>
    MaxPerMinute,
}

/// <summary>
/// The pulse diagram beside a limit of the Rule step (like a logic analyzer): the event on top, the actions below, each
/// rising edge of the event runs the actions unless the limit stops it. Follows the typed value; colours from the theme.
/// </summary>
public sealed class LimitDiagram : Control
{
    public static readonly StyledProperty<RuleLimit> LimitProperty =
        AvaloniaProperty.Register<LimitDiagram, RuleLimit>(nameof(Limit));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<LimitDiagram, string?>(nameof(Value));

    // The drawing's own grid; it is scaled uniformly into the bounds.
    private const double W = 440, H = 104, X0 = 58, X1 = 430, YE = 30, YA = 74, Ph = 16, Pw = 7;
    private const int DefaultDead = 10, DefaultMax = 60;

    static LimitDiagram() => AffectsRender<LimitDiagram>(LimitProperty, ValueProperty);

    public RuleLimit Limit
    {
        get => GetValue(LimitProperty);
        set => SetValue(LimitProperty, value);
    }

    /// <summary>The field's text; a value that is not a whole number in range draws the default.</summary>
    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Min(W, availableSize.Width) : W;
        return new Size(width, width * H / W);
    }

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width / W, Bounds.Height / H);
        if (scale <= 0)
        {
            return;
        }

        var paint = new Paint(this);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            DrawFrame(context, paint);
            if (Limit == RuleLimit.DeadTime)
            {
                DrawDeadTime(context, paint, Parse(Value, DefaultDead, 0));
            }
            else
            {
                DrawMaxPerMinute(context, paint, Parse(Value, DefaultMax, 1));
            }
        }
    }

    /// <summary>The value to draw: the typed whole number, else the default.</summary>
    internal static int Parse(string? text, int fallback, int min) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min ? value : fallback;

    /// <summary>The event times (s, of a 30 s window) that run the actions with this dead time.</summary>
    internal static IReadOnlyList<double> Runs(IReadOnlyList<double> events, int dead)
    {
        var runs = new List<double>();
        var last = double.NegativeInfinity;
        foreach (var t in events)
        {
            if (t >= last + dead)
            {
                runs.Add(t);
                last = t;
            }
        }

        return runs;
    }

    internal static readonly double[] DeadTimeEvents = [1.5, 4, 6.5, 9, 13.5, 16, 19, 24.5, 27];

    private static void DrawFrame(DrawingContext context, Paint paint)
    {
        var grid = new Pen(paint.Line, 0.6);
        for (var i = 0; i <= 10; i++)
        {
            var x = X0 + i * (X1 - X0) / 10;
            context.DrawLine(grid, new Point(x, 6), new Point(x, H - 20));
        }

        for (var y = 12.0; y < H - 20; y += 12)
        {
            context.DrawLine(grid, new Point(X0 - 4, y), new Point(X1 + 4, y));
        }

        context.DrawText(paint.Text("Event", paint.Event), new Point(0, YE - 16));
        context.DrawText(paint.Text("Actions", paint.Action), new Point(0, YA - 16));
    }

    private static void DrawDeadTime(DrawingContext context, Paint paint, int dead)
    {
        const double span = 30, k = (X1 - X0) / span;
        var runs = Runs(DeadTimeEvents, dead);
        var edge = new Pen(paint.Action, 1, new DashStyle([2, 3], 0));
        foreach (var t in runs)
        {
            var x = X0 + t * k;
            if (dead > 0)
            {
                context.DrawRectangle(paint.Zone, null, new Rect(x, 8, Math.Min(dead * k, X1 - x), YA + 4 - 8));
            }

            context.DrawLine(edge, new Point(x, YE - Ph - 4), new Point(x, YA + 2));
        }

        var ghost = new Pen(paint.Ghost, 1.5, new DashStyle([2, 2], 0));
        foreach (var t in DeadTimeEvents.Where(t => !runs.Contains(t)))
        {
            context.DrawGeometry(null, ghost, Pulse(X0 + t * k, YA));
        }

        context.DrawGeometry(null, new Pen(paint.Event, 1.25, lineJoin: PenLineJoin.Round), Trace(DeadTimeEvents.Select(t => X0 + t * k), YE));
        context.DrawGeometry(null, new Pen(paint.Action, 1.25, lineJoin: PenLineJoin.Round), Trace(runs.Select(t => X0 + t * k), YA));

        if (dead == 0)
        {
            var none = paint.Text("No dead time: every event runs the actions", paint.Action, bold: true);
            context.DrawText(none, new Point(X0, H - 8 - none.Height / 2));
            return;
        }

        // The measure of the first dead time.
        var from = X0 + runs[0] * k;
        var width = Math.Min(dead * k, X1 - from);
        var label = paint.Text($"{dead.ToString("N0", CultureInfo.CurrentCulture)} s dead time", paint.Action, bold: true);
        DrawMeasure(context, paint, from, from + width, label, beside: true);
    }

    private static void DrawMaxPerMinute(DrawingContext context, Paint paint, int max)
    {
        const double gap = 20;
        var runs = new List<(double X, int N)>();
        var x = X0 + 6;
        double? brk = null;
        if (max <= 5)
        {
            for (var n = 1; n <= max; n++, x += gap)
            {
                runs.Add((x, n));
            }
        }
        else
        {
            for (var n = 1; n <= 3; n++, x += gap)
            {
                runs.Add((x, n));
            }

            brk = x + 2;
            x += 22;
            runs.Add((x, max - 1));
            x += gap;
            runs.Add((x, max));
            x += gap;
        }

        var cut = x - gap + Pw + 6;
        var skipped = new List<double>();
        for (var i = 0; i < 5 && x + 6 + Pw < X1; i++, x += gap)
        {
            skipped.Add(x + 6);
        }

        context.DrawRectangle(paint.SkipZone, null, new Rect(cut, 8, X1 - cut, YA + 4 - 8));
        context.DrawLine(new Pen(paint.Skip, 1, new DashStyle([2, 3], 0)), new Point(cut, 8), new Point(cut, YA + 4));
        context.DrawGeometry(null, new Pen(paint.Event, 1.25, lineJoin: PenLineJoin.Round), Trace(runs.Select(r => r.X).Concat(skipped), YE));
        context.DrawGeometry(null, new Pen(paint.Action, 1.25, lineJoin: PenLineJoin.Round), Trace(runs.Select(r => r.X), YA));
        var ghost = new Pen(paint.Ghost, 1.5, new DashStyle([2, 2], 0));
        foreach (var s in skipped)
        {
            context.DrawGeometry(null, ghost, Pulse(s, YA));
        }

        if (brk is { } b)
        {
            // Break marks: the time between the third run and the last ones is left out.
            var pen = new Pen(paint.Secondary, 1.2);
            foreach (var y in new[] { YE, YA })
            {
                context.DrawLine(pen, new Point(b, y + 5), new Point(b + 6, y - 5));
                context.DrawLine(pen, new Point(b + 6, y + 5), new Point(b + 12, y - 5));
            }
        }

        // The numbers of the runs: 1, 2, 3 and the last (the one before it would collide with large values).
        var numbersEnd = 0.0;
        foreach (var (rx, n) in runs.Where(r => r.N <= 3 || r.N == max))
        {
            var number = paint.Text(n.ToString("N0", CultureInfo.CurrentCulture), paint.Secondary);
            var left = n == max && n > 3 ? rx + Pw - number.Width : rx + Pw / 2 - number.Width / 2;
            context.DrawText(number, new Point(left, YA + 2));
            numbersEnd = left + number.Width;
        }

        var skippedLabel = paint.Text("skipped", paint.Skip, bold: true);
        context.DrawText(skippedLabel, new Point(Math.Max(cut + 8, numbersEnd + 10), YA + 2));

        var label = paint.Text($"1 minute: at most {max.ToString("N0", CultureInfo.CurrentCulture)} runs", paint.Action, bold: true);
        DrawMeasure(context, paint, X0, X1, label, beside: false);
    }

    /// <summary>A measure below the diagram with arrowheads at both ends; its text beside it or in its middle.</summary>
    private static void DrawMeasure(DrawingContext context, Paint paint, double from, double to, FormattedText label, bool beside)
    {
        const double y = H - 8;
        var pen = new Pen(paint.Action, 1.2);
        context.DrawGeometry(paint.Action, null, Arrow(from, y, -1));
        context.DrawGeometry(paint.Action, null, Arrow(to, y, 1));
        if (beside && to + 6 + label.Width <= W)
        {
            context.DrawLine(pen, new Point(from, y), new Point(to, y));
            context.DrawText(label, new Point(to + 6, y - label.Height / 2));
            return;
        }

        // The text in the line: the line stops 6 px before and after it (no box, so any background shows through).
        var left = beside ? Math.Max(from, to - label.Width) : (from + to) / 2 - label.Width / 2;
        if (left - 6 > from)
        {
            context.DrawLine(pen, new Point(from, y), new Point(left - 6, y));
        }

        if (left + label.Width + 6 < to)
        {
            context.DrawLine(pen, new Point(left + label.Width + 6, y), new Point(to, y));
        }

        context.DrawText(label, new Point(left, y - label.Height / 2));
    }

    private static StreamGeometry Arrow(double x, double y, int dir)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y), true);
        g.LineTo(new Point(x - 6 * dir, y - 3.5));
        g.LineTo(new Point(x - 6 * dir, y + 3.5));
        g.EndFigure(true);
        return geometry;
    }

    /// <summary>A digital trace on the row whose low line is <paramref name="y"/>: one pulse per start.</summary>
    private static StreamGeometry Trace(IEnumerable<double> starts, double y)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(X0, y), false);
        foreach (var x in starts.Order())
        {
            g.LineTo(new Point(x, y));
            g.LineTo(new Point(x, y - Ph));
            g.LineTo(new Point(x + Pw, y - Ph));
            g.LineTo(new Point(x + Pw, y));
        }

        g.LineTo(new Point(X1, y));
        g.EndFigure(false);
        return geometry;
    }

    private static StreamGeometry Pulse(double x, double y)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y), false);
        g.LineTo(new Point(x, y - Ph));
        g.LineTo(new Point(x + Pw, y - Ph));
        g.LineTo(new Point(x + Pw, y));
        g.EndFigure(false);
        return geometry;
    }

    /// <summary>The theme's brushes and font for the control's appearance.</summary>
    private sealed class Paint
    {
        private readonly Control _owner;
        private readonly Typeface _typeface;
        private readonly Typeface _bold;
        private readonly double _size;

        public Paint(Control owner)
        {
            _owner = owner;
            var family = Find("Oadm.FontFamily") as FontFamily ?? FontFamily.Default;
            _typeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
            _bold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
            _size = Find("Oadm.FontSizeBody") is double body ? body - 1 : 11;
            Line = Brush("Oadm.InputBorderBrush");
            Event = Brush("Oadm.TealBrush");
            Action = Brush("Oadm.AccentBrush");
            Zone = Brush("Oadm.StatusAccentBackgroundBrush");
            Skip = Brush("Oadm.StatusWarningBrush");
            SkipZone = Brush("Oadm.StatusWarningBackgroundBrush");
            Ghost = Brush("Oadm.TextDisabledBrush");
            Secondary = Brush("Oadm.TextSecondaryBrush");
        }

        public IBrush Line { get; }

        public IBrush Event { get; }

        public IBrush Action { get; }

        public IBrush Zone { get; }

        public IBrush Skip { get; }

        public IBrush SkipZone { get; }

        public IBrush Ghost { get; }

        public IBrush Secondary { get; }

        public FormattedText Text(string text, IBrush brush, bool bold = false) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? _bold : _typeface, _size, brush);

        private object? Find(string key) => _owner.TryFindResource(key, _owner.ActualThemeVariant, out var value) ? value : null;

        private IBrush Brush(string key) => Find(key) as IBrush ?? Brushes.Gray;
    }
}
