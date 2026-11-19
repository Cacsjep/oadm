using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// One setting as a row (Settings page): icon, title and hint on the left, the control on the right. Inside a
/// <see cref="SettingsExpander"/> the row is attached to its header (smaller neutral icon, see the theme).
/// A validation error of the control shows directly below it (the control's own DataValidationErrors).
/// </summary>
public class SettingsCard : Border
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<SettingsCard, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Hint));

    public static readonly StyledProperty<object?> ActionProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Action));

    public static readonly StyledProperty<object?> TrailingProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Trailing));

    /// <summary>The view's own condition (bind it instead of IsVisible: the search of <see cref="SettingsList"/> hides rows too).</summary>
    public static readonly StyledProperty<bool> IsShownProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsShown), true);

    private readonly OadmIcon _icon = new();
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _action = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _trailing = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _filterMatch = true;

    public SettingsCard()
    {
        Classes.Add("settingsCard");
        _icon.Classes.Add("settingsIcon");
        _title.Classes.Add("settingsTitle");
        _hint.Classes.Add("settingsHint");

        // The icon slot is as wide as the largest icon, so titles of rows and attached rows share one left edge.
        var iconSlot = new Panel { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        iconSlot.Children.Add(_icon);

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_title);
        texts.Children.Add(_hint);

        // The control and, after it, the chevron of an expander: one group at the right edge, so a row without a
        // chevron ends where the chevron of its neighbours ends.
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_action);
        right.Children.Add(_trailing);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(iconSlot);
        grid.Children.Add(texts);
        grid.Children.Add(right);
        Child = grid;
        Update();
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>The control of the setting, on the right.</summary>
    [Content]
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    /// <summary>After the control, at the right edge (the chevron of a <see cref="SettingsExpander"/>).</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public bool IsShown
    {
        get => GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    /// <summary>True when the title or the hint contains the search text.</summary>
    public bool Matches(string filter) => SettingsList.Contains(Title, filter) || SettingsList.Contains(Hint, filter);

    internal void SetFilterMatch(bool match)
    {
        _filterMatch = match;
        IsVisible = IsShown && _filterMatch;
    }

    internal bool IsShownAndMatching => IsShown && _filterMatch;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == TitleProperty || change.Property == HintProperty
            || change.Property == ActionProperty || change.Property == TrailingProperty)
        {
            Update();
        }
        else if (change.Property == IsShownProperty)
        {
            IsVisible = IsShown && _filterMatch;
            SettingsList.RefreshFor(this);
        }
    }

    private void Update()
    {
        _icon.Data = Icon;
        _icon.IsVisible = Icon is not null;
        _title.Text = Title;
        _hint.Text = Hint;
        _hint.IsVisible = !string.IsNullOrEmpty(Hint);
        _action.Content = Action;
        _trailing.Content = Trailing;
        _trailing.IsVisible = Trailing is not null;
    }
}
