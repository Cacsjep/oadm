using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// A small info icon next to an input (every "what does this mean" text): the text shows as a
/// tooltip on hover and in a flyout on click. <see cref="FormField.Info"/> places one right of the input.
/// <see cref="PictureTemplate"/> adds a drawing below the text (the pulse diagrams of the event rule limits).
/// </summary>
public sealed class InfoIcon : Button
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InfoIcon, string?>(nameof(Text));

    public static readonly StyledProperty<IDataTemplate?> PictureTemplateProperty =
        AvaloniaProperty.Register<InfoIcon, IDataTemplate?>(nameof(PictureTemplate));

    private readonly TextBlock _flyoutText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
    private readonly StackPanel _flyoutBody = new() { Spacing = 10 };

    public InfoIcon()
    {
        Classes.Add("toolbar");
        Classes.Add("iconOnly");
        Classes.Add("info");
        VerticalAlignment = VerticalAlignment.Center;
        Content = new OadmIcon { Data = ToolbarButton.ResolveIcon("info") };
        _flyoutBody.Children.Add(_flyoutText);
        Flyout = new Flyout { Content = _flyoutBody, Placement = PlacementMode.BottomEdgeAlignedLeft };
        IsVisible = false; // shown once there is a text (an event or action is chosen)
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The explanation (tooltip and flyout).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Optional: a drawing below the text in the tooltip and the flyout, built from the template with the icon's data
    /// context (one copy for each). The tooltip then has the class <c>wide</c> (theme: room for the drawing).
    /// </summary>
    public IDataTemplate? PictureTemplate
    {
        get => GetValue(PictureTemplateProperty);
        set => SetValue(PictureTemplateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == PictureTemplateProperty)
        {
            _flyoutText.Text = Text;
            while (_flyoutBody.Children.Count > 1)
            {
                _flyoutBody.Children.RemoveAt(1);
            }

            if (PictureTemplate is null)
            {
                ToolTip.SetTip(this, Text);
            }
            else
            {
                _flyoutBody.Children.Add(Picture());
                var body = new StackPanel { Spacing = 10 };
                body.Children.Add(new TextBlock { Text = Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left });
                body.Children.Add(Picture());
                var tip = new ToolTip { Content = body };
                tip.Classes.Add("wide");
                ToolTip.SetTip(this, tip);
            }

            IsVisible = !string.IsNullOrEmpty(Text);
        }
    }

    /// <summary>A copy of the drawing that follows the icon's data context.</summary>
    private ContentControl Picture() => new()
    {
        ContentTemplate = PictureTemplate,
        [!ContentControl.ContentProperty] = new Binding(nameof(DataContext)) { Source = this },
    };
}
