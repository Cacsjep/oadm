using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one icon + text row used everywhere (toolbar buttons, navigation rail, menus, dialogs).
/// Both parts are vertically centered, and the text gets the measured Inter correction so its
/// capitals line up with the icon center. Never build icon + text rows by hand.
/// </summary>
public sealed class IconLabel : Grid
{
    /// <summary>
    /// Inter reserves descender space below the baseline, so a centered line of text puts its
    /// capitals about 1.5 px above center next to a 16 px icon (measured in headless renders).
    /// A 3 px top margin on a centered element moves it down by exactly that.
    /// </summary>
    private const double InterBaselineCorrection = 3;

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<IconLabel, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsTextVisibleProperty =
        AvaloniaProperty.Register<IconLabel, bool>(nameof(IsTextVisible), true);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(Spacing), 8);

    private readonly OadmIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_icon, 0);
        SetColumn(_text, 1);
        Children.Add(_icon);
        Children.Add(_text);
        UpdateIcon();
        UpdateText();
        UpdateTextMargin();
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsTextVisible
    {
        get => GetValue(IsTextVisibleProperty);
        set => SetValue(IsTextVisibleProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The inner text block, so styles can target it as <c>c|IconLabel TextBlock</c>.</summary>
    public TextBlock TextBlock => _text;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            UpdateIcon();
        }
        else if (change.Property == TextProperty || change.Property == IsTextVisibleProperty)
        {
            UpdateText();
        }
        else if (change.Property == SpacingProperty)
        {
            UpdateTextMargin();
        }
    }

    private void UpdateIcon()
    {
        _icon.Data = Icon;
        _icon.IsVisible = Icon is not null;
        UpdateTextMargin();
    }

    private void UpdateText()
    {
        _text.Text = Text;
        _text.IsVisible = IsTextVisible && !string.IsNullOrEmpty(Text);
    }

    private void UpdateTextMargin() =>
        _text.Margin = new Thickness(Icon is null ? 0 : Spacing, InterBaselineCorrection, 0, 0);
}
