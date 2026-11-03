using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>
/// A Button widget: a real button filling the tile in the widget's color (icon and label sized with the tile, white on a
/// dark fill and dark on a light one, <see cref="IsLightFill"/>), and for a moment the answer of a press ("Sent", "No event
/// rule uses this button.") or why it failed, small at the bottom of the fill: the button never moves. The button never
/// touches a device; a press fires the button's event for the event rules. While the dashboard is edited a press does
/// nothing (the edit frame lies over it); a role without "Press dashboard buttons" shows it disabled with the reason.
/// Theme classes <c>Button.dashboardButton</c>, <c>Border.dashboardButtonFill</c> (<c>onLight</c>) and <c>TextBlock.buttonNote</c>.
/// </summary>
public sealed class ButtonWidgetView : Border
{
    /// <summary>The label: this share of the button's height, between these sizes (the box shrinks a long label to fit).</summary>
    private const double LabelShare = 0.3;
    private const double MinLabelSize = 12;
    private const double MaxLabelSize = 40;

    /// <summary>A fill at least this light (WCAG relative luminance) takes dark text, a darker one white.</summary>
    public const double LightFill = 0.5;

    private readonly Button _button = new();
    private readonly Border _fill = new();
    private readonly OadmIcon _icon = new();
    private readonly TextBlock _label = new();
    private readonly TextBlock _note = new();
    private readonly ShareBox _box = new();
    private IDisposable? _fillBrush;
    private Widget? _widget;
    private bool _editing;
    private bool _mayPress = true;
    private string? _noteText;
    private bool _failed;

    public ButtonWidgetView()
    {
        Classes.Add("dashboardWidget");
        _button.Classes.Add("dashboardButton");
        _fill.Classes.Add("dashboardButtonFill");
        _note.Classes.Add("buttonNote");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.AddRange([_icon, _label]);
        // The note lies over the bottom of the fill: the label keeps its place and size.
        var content = new Panel();
        content.Children.AddRange([new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = row }, _note]);
        _fill.Child = content;
        _button.Content = _fill;
        ToolTip.SetShowOnDisabled(_button, true);
        _box.Child = _button;
        Child = _box;
        _button.SizeChanged += (_, e) => SizeLabel(e.NewSize.Height);
    }

    /// <summary>What a press runs (the widget's press command).</summary>
    public ICommand? Command
    {
        get => _button.Command;
        set => _button.Command = value;
    }

    /// <summary>
    /// Shows <paramref name="widget"/>: <paramref name="editing"/> (a press does nothing), <paramref name="mayPress"/> (the
    /// role allows it), <paramref name="note"/> (the answer of the last press, <paramref name="failed"/> when it failed).
    /// </summary>
    public void Show(Widget widget, bool editing, bool mayPress, string? note, bool failed)
    {
        ArgumentNullException.ThrowIfNull(widget);
        _widget = widget;
        _editing = editing;
        _mayPress = mayPress;
        _noteText = note;
        _failed = failed;
        _label.Text = widget.Name;
        _box.Share = ButtonSizes.Share(widget.ButtonSize); // Medium and Small: smaller and centred, the label shrinks with it
        _button.IsEnabled = editing || mayPress;
        _button.IsHitTestVisible = !editing;
        ToolTip.SetTip(_button, editing || mayPress ? null : DashboardRules.PressNotAllowed);
        _note.Text = editing ? null : note;
        _note.IsVisible = !string.IsNullOrEmpty(_note.Text);
        _note.Classes.Set("failed", failed);

        _icon.IsVisible = widget.Icon.Length > 0;
        if (_icon.IsVisible && this.TryFindResource("Icon." + widget.Icon, ActualThemeVariant, out var geometry) && geometry is Geometry g)
        {
            _icon.Data = g;
        }

        _fillBrush?.Dispose();
        var key = WidgetColors.BrushKey(widget.Color);
        _fillBrush = _fill.Bind(BackgroundProperty, this.GetResourceObservable(key));
        _fill.Classes.Set("onLight", this.TryFindResource(key, ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid && IsLightFill(solid.Color));
        SizeLabel(_button.Bounds.Height);
    }

    /// <summary>
    /// Whether a fill takes dark text: its WCAG relative luminance (sRGB channels linearized, 0.2126 R + 0.7152 G +
    /// 0.0722 B) is at least <see cref="LightFill"/>. The one place that decides the label color of a button.
    /// </summary>
    public static bool IsLightFill(Color fill)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(fill.R)) + (0.7152 * Linear(fill.G)) + (0.0722 * Linear(fill.B)) >= LightFill;
    }

    /// <summary>The share of the tile the button takes (tests).</summary>
    internal double Share => _box.Share;

    /// <summary>The fill takes dark text (tests).</summary>
    internal bool OnLight => _fill.Classes.Contains("onLight");

    /// <summary>The label shown (tests).</summary>
    internal string? Label => _label.Text;

    /// <summary>The line below the button (tests).</summary>
    internal string? Note => _note.IsVisible ? _note.Text : null;

    /// <summary>The button itself (tests).</summary>
    internal Button Button => _button;

    /// <summary>The label grows with the tile (a share of its height); a label too long for the width shrinks to fit.</summary>
    private void SizeLabel(double height)
    {
        if (height <= 0)
        {
            return;
        }

        var size = Math.Floor(Math.Clamp(height * LabelShare, MinLabelSize, MaxLabelSize));
        _label.FontSize = size;
        _icon.Width = size;
        _icon.Height = size;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_widget is not null)
        {
            Show(_widget, _editing, _mayPress, _noteText, _failed); // resources are found once the control is in the tree
        }
    }
}

/// <summary>Arranges its child centred at <see cref="Share"/> of its width and height (a Medium or Small button).</summary>
internal sealed class ShareBox : Decorator
{
    public static readonly StyledProperty<double> ShareProperty = AvaloniaProperty.Register<ShareBox, double>(nameof(Share), 1.0);

    static ShareBox() => AffectsArrange<ShareBox>(ShareProperty);

    public double Share
    {
        get => GetValue(ShareProperty);
        set => SetValue(ShareProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var share = Math.Clamp(Share, 0.1, 1);
        var width = Math.Floor(finalSize.Width * share);
        var height = Math.Floor(finalSize.Height * share);
        Child?.Arrange(new Rect(Math.Round((finalSize.Width - width) / 2), Math.Round((finalSize.Height - height) / 2), width, height));
        return finalSize;
    }
}
