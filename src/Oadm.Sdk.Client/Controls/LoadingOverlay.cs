using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// Hides the content of a dialog (or page area) while it loads: the window background with the theme's <see cref="ProgressRing"/> and the
/// text below it in the middle; it takes the pointer, so nothing below can be clicked. Place it last in the cell it covers
/// and bind <see cref="Visual.IsVisible"/> to the loading state. Look: theme style <c>ui|LoadingOverlay</c>.
/// </summary>
public sealed class LoadingOverlay : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<LoadingOverlay, string?>(nameof(Text));

    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = Avalonia.Media.TextAlignment.Center };

    public LoadingOverlay()
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new ProgressRing { HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(_text);
        Child = panel;
    }

    /// <summary>What is loading, e.g. "Reading 10.0.0.48".</summary>
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
