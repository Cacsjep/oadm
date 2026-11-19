using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Client.Controls;

/// <summary>
/// The unit of a number field, inside the field at its right (NumericUpDown.InnerRightContent): "sec", "ms", "FPS".
/// Accent background, white text, nearly square corners.
/// </summary>
public sealed class UnitBadge : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<UnitBadge, string?>(nameof(Text));

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

    public UnitBadge()
    {
        Classes.Add("unitBadge");
        Child = _text;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
    }
}
