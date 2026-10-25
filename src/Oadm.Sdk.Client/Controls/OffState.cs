using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The state of a page or tab whose feature is off (Sites, Compliance, the 802.1X and EST servers of PKI): a card with a
/// large icon, a title ("Site nodes are off"), one or two sentences of what the feature does, and below them the host's
/// controls (<see cref="Actions"/>: the button that turns it on, the line for users who may not, the error), all centered.
/// </summary>
public sealed class OffState : Border
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<OffState, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<OffState, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<OffState, string?>(nameof(Text));

    private readonly OadmIcon _icon = new() { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _actions = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };

    public OffState()
    {
        Classes.Add("card");
        _title.Classes.Add("cardTitle");
        _text.Classes.Add("secondary");
        Child = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            MaxWidth = 460,
            Children = { _icon, _title, _text, _actions },
        };
        Update();
    }

    /// <summary>The feature's icon (32 px).</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>"Site nodes are off".</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>What the feature does, one or two short sentences.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The controls below the text, centered (the turn-on button, a line for other users, the error).</summary>
    [Content]
    public Avalonia.Controls.Controls Actions => _actions.Children;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == TitleProperty || change.Property == TextProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _icon.Data = Icon;
        _icon.IsVisible = Icon is not null;
        _title.Text = Title;
        _text.Text = Text;
        _text.IsVisible = !string.IsNullOrEmpty(Text);
    }
}
