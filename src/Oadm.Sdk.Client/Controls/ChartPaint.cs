using System.Globalization;

using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>One line of a chart's hover popup: a colour dot (or none) and the text.</summary>
internal readonly record struct ChartHoverLine(string Text, IBrush? Dot, bool IsTitle = false);

/// <summary>
/// The theme of the charts, looked up by key for the control's appearance: fonts, the colour roles, grid and text, the
/// hover popup. Shared drawing helpers of <see cref="LineChart"/> and <see cref="BarChart"/>.
/// </summary>
internal sealed class ChartPaint
{
    public const double Gap = 6;

    private readonly Control _owner;
    private readonly Typeface _typeface;
    private readonly Typeface _bold;

    public ChartPaint(Control owner)
    {
        _owner = owner;
        var family = Find("Oadm.FontFamily") as FontFamily ?? FontFamily.Default;
        _typeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        _bold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        SmallSize = Find("Oadm.FontSizeSmall") is double small ? small : 10;
        BodySize = Find("Oadm.FontSizeBody") is double body ? body : 12;
        Text = Brush("Oadm.TextPrimaryBrush");
        Secondary = Brush("Oadm.TextSecondaryBrush");
        Grid = Brush("Oadm.BorderBrush");
        Axis = Brush("Oadm.InputBorderBrush");
        Hover = Brush("Oadm.SurfaceHoverBrush");
        Card = Brush("Oadm.CardBackgroundBrush");
        Popup = Brush("Oadm.MenuBackgroundBrush");
        Threshold = Brush("Oadm.StatusErrorBrush");
    }

    public double SmallSize { get; }

    public double BodySize { get; }

    public IBrush Text { get; }

    public IBrush Secondary { get; }

    /// <summary>The grid lines (the faint border).</summary>
    public IBrush Grid { get; }

    /// <summary>The base line of the plot and the hover line (the input border, a little stronger).</summary>
    public IBrush Axis { get; }

    /// <summary>The column behind a hovered bar.</summary>
    public IBrush Hover { get; }

    public IBrush Card { get; }

    public IBrush Popup { get; }

    public IBrush Threshold { get; }

    /// <summary>The theme brush of a colour role.</summary>
    public IBrush Of(ChartColor color) => Brush(color switch
    {
        ChartColor.Secondary => "Oadm.TealBrush",
        ChartColor.Ok => "Oadm.StatusOkBrush",
        ChartColor.Warning => "Oadm.StatusWarningBrush",
        ChartColor.Error => "Oadm.StatusErrorBrush",
        ChartColor.Neutral => "Oadm.StatusNeutralBrush",
        _ => "Oadm.AccentBrush",
    });

    public FormattedText Small(string text, IBrush? brush = null) => Make(text, _typeface, SmallSize, brush ?? Secondary);

    public FormattedText Body(string text, IBrush? brush = null, bool bold = false) => Make(text, bold ? _bold : _typeface, BodySize, brush ?? Text);

    /// <summary>A one pixel line on whole pixels.</summary>
    public static double Snap(double value) => Math.Round(value - 0.5) + 0.5;

    public static Pen Dashed(IBrush brush) => new(brush, 1, new DashStyle([4, 3], 0));

    /// <summary>The widest label of a value axis.</summary>
    public double AxisLabelWidth(ChartAxis axis) => axis.Ticks().Max(t => Small(axis.Label(t)).Width);

    /// <summary>The horizontal grid lines with their labels left of <paramref name="plot"/>; the bottom one is the base line.</summary>
    public void DrawValueAxis(DrawingContext context, ChartAxis axis, Rect plot, Func<double, double> y)
    {
        var grid = new Pen(Grid, 1);
        var baseLine = new Pen(Axis, 1);
        var ticks = axis.Ticks();
        for (var i = 0; i < ticks.Count; i++)
        {
            var at = Snap(y(ticks[i]));
            context.DrawLine(i == 0 || ticks[i] == 0 ? baseLine : grid, new Point(plot.Left, at), new Point(plot.Right, at));
            var label = Small(axis.Label(ticks[i]));
            context.DrawText(label, new Point(Math.Round(plot.Left - 8 - label.Width), Math.Round(at - label.Height / 2)));
        }
    }

    /// <summary>The dashed limit line across <paramref name="plot"/>, when it lies on the axis.</summary>
    public void DrawThreshold(DrawingContext context, double? threshold, ChartAxis axis, Rect plot, Func<double, double> y)
    {
        if (threshold is not { } limit || !double.IsFinite(limit) || limit < axis.Min || limit > axis.Max)
        {
            return;
        }

        var at = Snap(y(limit));
        context.DrawLine(Dashed(Threshold), new Point(plot.Left, at), new Point(plot.Right, at));
    }

    /// <summary>The empty chart: its text in the middle.</summary>
    public void DrawEmpty(DrawingContext context, string? text, Size size)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = Body(text, Secondary);
        formatted.MaxTextWidth = Math.Max(1, size.Width - 16);
        formatted.TextAlignment = TextAlignment.Center;
        context.DrawText(formatted, new Point(8, Math.Round((size.Height - formatted.Height) / 2))); // centred in its 8 px inset box
    }

    /// <summary>
    /// The hover popup beside <paramref name="anchorX"/> (right of it, left when it does not fit), at the top of
    /// <paramref name="plot"/>: a menu-coloured box with a faint border, the title, then one line per value with its dot.
    /// </summary>
    public void DrawPopup(DrawingContext context, IReadOnlyList<ChartHoverLine> lines, double anchorX, Rect plot, Size bounds)
    {
        if (lines.Count == 0)
        {
            return;
        }

        const double padX = 10, padY = 7, dot = 8, dotGap = 6, lineGap = 3;
        var texts = lines.Select(l => l.IsTitle ? Body(l.Text, Text, bold: true) : Body(l.Text)).ToList();
        var hasDots = lines.Any(l => l.Dot is not null);
        var width = texts.Select((t, i) => t.Width + (lines[i].Dot is not null || (hasDots && !lines[i].IsTitle) ? dot + dotGap : 0)).Max() + padX * 2;
        var height = texts.Sum(t => t.Height) + lineGap * (texts.Count - 1) + padY * 2;
        var x = anchorX + 12;
        if (x + width > bounds.Width - 1)
        {
            x = anchorX - 12 - width;
        }

        x = Math.Round(Math.Max(0, x));
        var y = Math.Round(Math.Max(0, Math.Min(plot.Top + 4, bounds.Height - height - 1)));
        var box = new Rect(x, y, Math.Ceiling(width), Math.Ceiling(height));
        context.DrawRectangle(Popup, new Pen(Axis, 1), new Rect(box.X + 0.5, box.Y + 0.5, box.Width - 1, box.Height - 1), 4, 4);
        var top = y + padY;
        for (var i = 0; i < texts.Count; i++)
        {
            var left = x + padX;
            if (lines[i].Dot is { } brush)
            {
                context.DrawEllipse(brush, null, new Point(left + dot / 2, top + texts[i].Height / 2), dot / 2 - 0.5, dot / 2 - 0.5);
            }

            if (lines[i].Dot is not null || (hasDots && !lines[i].IsTitle))
            {
                left += dot + dotGap;
            }

            context.DrawText(texts[i], new Point(left, top));
            top += texts[i].Height + lineGap;
        }
    }

    private static FormattedText Make(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    private object? Find(string key) => _owner.TryFindResource(key, _owner.ActualThemeVariant, out var value) ? value : null;

    private IBrush Brush(string key) => Find(key) as IBrush ?? Brushes.Gray;
}

/// <summary>The screen reader view of a chart: an image named by the control's automation name, else its summary.</summary>
internal sealed class ChartAutomationPeer(Control owner, Func<string> summary) : ControlAutomationPeer(owner)
{
    protected override string? GetNameCore()
    {
        var name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? summary() : name;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
