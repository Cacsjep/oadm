using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Metadata;

namespace Oadm.Client.Controls;

/// <summary>
/// A card that folds (the Settings page, decision 2026-10-25): its title with the chevron of a group as the header (the
/// whole row toggles it), its content below while open. Closed by default (<see cref="IsExpanded"/>).
/// </summary>
public sealed class CollapsibleCard : Border
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CollapsibleCard, string?>(nameof(Title));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapsibleCard, bool>(nameof(IsExpanded), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<Control?> BodyProperty =
        AvaloniaProperty.Register<CollapsibleCard, Control?>(nameof(Body));

    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _header;
    private readonly ContentControl _body = new() { Margin = new Thickness(0, 12, 0, 0) };

    public CollapsibleCard()
    {
        Classes.Add("card");
        _title.Classes.Add("sectionTitle");
        _header = new ToggleButton { Content = _title, HorizontalAlignment = HorizontalAlignment.Stretch };
        _header.Classes.Add("sectionExpander");
        _header.IsCheckedChanged += (_, _) => SetCurrentValue(IsExpandedProperty, _header.IsChecked == true);
        Child = new StackPanel { Children = { _header, _body } };
        Update();
    }

    /// <summary>The card's title ("Polling").</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Open: the content shows. False (closed) by default.</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>The card's content (below the title while open).</summary>
    [Content]
    public Control? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == IsExpandedProperty || change.Property == BodyProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _title.Text = Title;
        _header.IsChecked = IsExpanded;
        _body.Content = Body;
        _body.IsVisible = IsExpanded;
    }
}
