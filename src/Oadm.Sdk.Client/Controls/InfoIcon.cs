using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// A small info icon next to an input (every "what does this mean" text, user decision 2026-10-11): the text shows as a
/// tooltip on hover and in a flyout on click. <see cref="FormField.Info"/> places one right of the input.
/// </summary>
public sealed class InfoIcon : Button
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InfoIcon, string?>(nameof(Text));

    private readonly TextBlock _flyoutText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };

    public InfoIcon()
    {
        Classes.Add("toolbar");
        Classes.Add("iconOnly");
        Classes.Add("info");
        VerticalAlignment = VerticalAlignment.Center;
        Content = new OadmIcon { Data = ToolbarButton.ResolveIcon("info") };
        Flyout = new Flyout { Content = _flyoutText, Placement = PlacementMode.BottomEdgeAlignedLeft };
        IsVisible = false; // shown once there is a text (an event or action is chosen)
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The explanation (tooltip and flyout).</summary>
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
            _flyoutText.Text = Text;
            ToolTip.SetTip(this, Text);
            IsVisible = !string.IsNullOrEmpty(Text);
        }
    }
}
