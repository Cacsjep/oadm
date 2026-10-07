using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The heading of a card, like the steps of the add devices wizard: card title, optional
/// secondary description below it, optional controls on the right of the title (<see cref="Trailing"/>,
/// e.g. a device picker). Spacing below comes from the theme.
/// </summary>
public sealed class CardHeader : Grid
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CardHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<CardHeader, string?>(nameof(Description));

    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _trailing = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

    public CardHeader()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("Auto,Auto");
        _title.Classes.Add("cardTitle");
        _description.Classes.Add("secondary");
        SetColumn(_trailing, 1);
        SetRow(_description, 1);
        SetColumnSpan(_description, 2);
        Children.Add(_title);
        Children.Add(_trailing);
        Children.Add(_description);
        Update();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Controls right of the title.</summary>
    [Content]
    public Avalonia.Controls.Controls Trailing => _trailing.Children;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == DescriptionProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _title.Text = Title;
        _description.Text = Description;
        _description.IsVisible = !string.IsNullOrEmpty(Description);
    }
}
