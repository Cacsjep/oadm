using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>
/// One value widget: its name at the top (small, grey, one line), the value in the middle sized as a share of the widget
/// (<see cref="FitText"/>, the icon and caption of an On / Off widget, or the <see cref="GaugeView"/>), the note below it ("Connection lost", "No value
/// yet"). A dimmed value is drawn at 45 %. Colors are theme brushes, so dark and light both work. Theme class
/// <c>Border.dashboardWidget</c>.
/// </summary>
public sealed class WidgetView : Border
{
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly FitText _value = new();
    private readonly FitBox _iconBox = new();
    private readonly OadmIcon _icon = new();
    private readonly TextBlock _caption = new() { TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid _onOff = new() { RowDefinitions = new RowDefinitions("*,Auto") };
    private readonly GaugeView _gauge = new();
    private readonly Panel _body = new();
    private readonly TextBlock _note = new() { TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private IDisposable? _valueBrush;
    private IDisposable? _iconBrush;
    private WidgetDisplay _display = WidgetDisplay.Empty;
    private Widget? _widget;
    private bool _showName = true;

    public WidgetView()
    {
        Classes.Add("dashboardWidget");
        _name.Classes.Add("secondary");
        _name.Classes.Add("small");
        _note.Classes.Add("secondary");
        _note.Classes.Add("small");
        _caption.Classes.Add("secondary");
        _iconBox.Child = _icon;
        _onOff.Children.Add(_iconBox);
        Grid.SetRow(_caption, 1);
        _onOff.Children.Add(_caption);
        _body.Children.Add(_value);
        _body.Children.Add(_onOff);
        _body.Children.Add(_gauge);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(_body, 1);
        Grid.SetRow(_note, 2);
        grid.Children.AddRange([_name, _body, _note]);
        Child = grid;
    }

    /// <summary>Shows <paramref name="widget"/> with <paramref name="display"/>.</summary>
    public void Show(Widget widget, WidgetDisplay display, bool showName = true)
    {
        ArgumentNullException.ThrowIfNull(widget);
        ArgumentNullException.ThrowIfNull(display);
        _widget = widget;
        _display = display;
        _name.Text = widget.Name;
        _showName = showName;
        _name.IsVisible = widget.ShowName && !string.IsNullOrEmpty(widget.Name);
        _name.Opacity = showName ? 1 : 0; // while editing the frame names the widget; the row stays, so nothing moves
        var isOnOff = widget.Kind == WidgetKind.OnOff && display.HasValue && display.Icon is not null;
        var isGauge = widget.Kind == WidgetKind.Gauge; // the scale shows also before the first value
        _onOff.IsVisible = isOnOff;
        _gauge.IsVisible = isGauge;
        _gauge.Show(widget, display);
        _value.IsVisible = !isOnOff && !isGauge && display.Text is not null;
        _value.Text = display.Text;
        _value.Unit = display.Unit;
        _value.Share = widget.FontSize / 100.0;
        _iconBox.Share = widget.IconSize / 100.0;
        _caption.Text = display.Caption;
        _caption.IsVisible = display.Caption is not null;
        _note.Text = display.Note;
        _note.IsVisible = display.Note is not null;
        _body.Opacity = display.Dimmed ? 0.45 : 1;
        ToolTip.SetTip(this, display.ToolTip);
        if (isOnOff && this.TryFindResource("Icon." + display.Icon, ActualThemeVariant, out var geometry) && geometry is Geometry g)
        {
            _icon.Data = g;
        }

        _valueBrush?.Dispose();
        _iconBrush?.Dispose();
        var key = WidgetColors.BrushKey(display.Color);
        _valueBrush = _value.Bind(FitText.ForegroundProperty, this.GetResourceObservable(key));
        _iconBrush = _icon.Bind(OadmIcon.ForegroundProperty, this.GetResourceObservable(key));
    }

    /// <summary>The text shown (tests).</summary>
    internal WidgetDisplay Display => _display;

    /// <summary>The gauge of a Gauge widget (tests).</summary>
    internal GaugeView Gauge => _gauge;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_widget is not null)
        {
            Show(_widget, _display, _showName); // resources are found once the control is in the tree
        }
    }
}

/// <summary>
/// A value as large as its share of the space allows: the font size is <see cref="Share"/> of the height, made smaller
/// until the text (and the unit, at half the size) fits the width; still too long at the smallest size, it is cut with
/// "…". Whole-pixel sizes only.
/// </summary>
public sealed class FitText : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<FitText, string?>(nameof(Text));

    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<FitText, string?>(nameof(Unit));

    public static readonly StyledProperty<double> ShareProperty = AvaloniaProperty.Register<FitText, double>(nameof(Share), 0.6);

    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<FitText, IBrush?>(nameof(Foreground));

    private const double MinSize = 8;

    static FitText() => AffectsRender<FitText>(TextProperty, UnitProperty, ShareProperty, ForegroundProperty);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public double Share
    {
        get => GetValue(ShareProperty);
        set => SetValue(ShareProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The font size the text got at the last render (tests).</summary>
    internal double RenderedSize { get; private set; }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Text) || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        if (this.TryFindResource("Oadm.FontFamily", ActualThemeVariant, out var family) && family is FontFamily f)
        {
            typeface = new Typeface(f, FontStyle.Normal, FontWeight.SemiBold);
        }

        var brush = Foreground ?? Brushes.White;
        var size = Math.Max(MinSize, Math.Floor(Bounds.Height * Math.Clamp(Share, 0.1, 1) * 0.75));
        var text = Text;
        FormattedText value;
        FormattedText? unit;
        while (true)
        {
            value = Format(text, typeface, size, brush);
            unit = string.IsNullOrEmpty(Unit) ? null : Format(" " + Unit, typeface, Math.Max(MinSize, Math.Floor(size / 2)), brush);
            var width = value.Width + (unit?.Width ?? 0);
            if (width <= Bounds.Width || size <= MinSize)
            {
                break;
            }

            size = Math.Max(MinSize, Math.Floor(size * Math.Max(0.5, Bounds.Width / width)));
        }

        // Still too long at the smallest size: cut with "…".
        while (value.Width + (unit?.Width ?? 0) > Bounds.Width && text.Length > 1)
        {
            text = text[..^2] + "…";
            value = Format(text, typeface, size, brush);
        }

        RenderedSize = size;
        var total = value.Width + (unit?.Width ?? 0);
        var x = Math.Round((Bounds.Width - total) / 2);
        var y = Math.Round((Bounds.Height - value.Height) / 2);
        context.DrawText(value, new Point(x, y));
        if (unit is not null)
        {
            // On the value's baseline.
            context.DrawText(unit, new Point(x + value.Width, Math.Round(y + value.Baseline - unit.Baseline)));
        }
    }

    private static FormattedText Format(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
}

/// <summary>Sizes its child to a square of <see cref="Share"/> of the smaller side of its space, centered.</summary>
public sealed class FitBox : Decorator
{
    public static readonly StyledProperty<double> ShareProperty = AvaloniaProperty.Register<FitBox, double>(nameof(Share), 0.6);

    static FitBox() => AffectsMeasure<FitBox>(ShareProperty);

    public double Share
    {
        get => GetValue(ShareProperty);
        set => SetValue(ShareProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Side(availableSize);
        if (Child is { } child && (child.Width != side || child.Height != side))
        {
            child.Width = side; // icons have a fixed size in the theme: the box sets it
            child.Height = side;
        }

        Child?.Measure(new Size(side, side));
        return new Size(double.IsInfinity(availableSize.Width) ? side : availableSize.Width, double.IsInfinity(availableSize.Height) ? side : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var side = Side(finalSize);
        Child?.Arrange(new Rect(Math.Round((finalSize.Width - side) / 2), Math.Round((finalSize.Height - side) / 2), side, side));
        return finalSize;
    }

    private double Side(Size size)
    {
        var smaller = Math.Min(double.IsInfinity(size.Width) ? 48 : size.Width, double.IsInfinity(size.Height) ? 48 : size.Height);
        return Math.Max(8, Math.Floor(smaller * Math.Clamp(Share, 0.1, 1)));
    }
}
