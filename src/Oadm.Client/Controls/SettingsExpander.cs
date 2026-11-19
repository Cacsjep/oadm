using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// A setting with more behind it (Settings page): a <see cref="SettingsCard"/> as header with a chevron, and the rows
/// attached below it while it is open. Starts closed. Opens by itself when an input inside shows a validation error and
/// while the search of <see cref="SettingsList"/> finds a row inside.
/// </summary>
public sealed class SettingsExpander : Decorator
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<SettingsExpander, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsExpander, string?>(nameof(Title));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<SettingsExpander, string?>(nameof(Hint));

    /// <summary>The control on the header row, left of the chevron (a switch, the current value).</summary>
    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(HeaderContent));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingsExpander, bool>(nameof(IsExpanded), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsShownProperty =
        AvaloniaProperty.Register<SettingsExpander, bool>(nameof(IsShown), true);

    private readonly SettingsCard _header = new();
    private readonly StackPanel _body = new();
    private readonly Border _bodyBorder = new() { IsVisible = false };
    private readonly OadmIcon _chevron = new();
    private readonly Button _toggle = new();
    private bool _filterMatch = true;
    private bool _filterOpen;

    static SettingsExpander()
    {
        // An error inside a closed expander must be seen: open it.
        DataValidationErrors.HasErrorsProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true && control.FindLogicalAncestorOfType<SettingsExpander>() is { } expander)
            {
                expander.SetCurrentValue(IsExpandedProperty, true);
            }
        });
    }

    public SettingsExpander()
    {
        _header.Classes.Add("header");
        _bodyBorder.Classes.Add("settingsExpanderBody");
        _bodyBorder.Child = _body;

        _toggle.Classes.Add("toolbar");
        _toggle.Classes.Add("iconOnly");
        _toggle.Content = _chevron;
        _toggle.Click += (_, _) => SetCurrentValue(IsExpandedProperty, !IsExpanded);
        _header.Trailing = _toggle;
        _header.PointerPressed += OnHeaderPressed;

        var root = new StackPanel();
        root.Children.Add(_header);
        root.Children.Add(_bodyBorder);
        Child = root;
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

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>The view's own condition (bind it instead of IsVisible: the search hides expanders too).</summary>
    public bool IsShown
    {
        get => GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    /// <summary>Whether the rows show now: opened, or the search found a row inside.</summary>
    public bool IsOpen => IsExpanded || _filterOpen;

    /// <summary>The rows inside (SettingsCard, or an ItemsControl of them for a list).</summary>
    [Content]
    public Avalonia.Controls.Controls Items => _body.Children;

    /// <summary>The search of <see cref="SettingsList"/>: shows the rows that match and opens while one does. Returns whether anything is left to show.</summary>
    internal bool ApplyFilter(string filter, bool groupMatches)
    {
        bool active = filter.Length > 0;
        bool self = groupMatches || SettingsList.Contains(Title, filter) || SettingsList.Contains(Hint, filter);
        bool any = false;
        foreach (var card in _body.GetLogicalDescendants().OfType<SettingsCard>())
        {
            bool match = !active || self || card.Matches(filter);
            card.SetFilterMatch(match);
            any |= match && card.IsShown;
        }

        _filterOpen = active && any;
        _filterMatch = !active || self || any;
        Update();
        return IsShown && _filterMatch;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == TitleProperty || change.Property == HintProperty
            || change.Property == HeaderContentProperty || change.Property == IsExpandedProperty)
        {
            Update();
        }
        else if (change.Property == IsShownProperty)
        {
            Update();
            SettingsList.RefreshFor(this);
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        // A click on the row opens it, like the chevron; a click on a control of the row is the control's.
        for (var visual = e.Source as Visual; visual is not null && !ReferenceEquals(visual, _header); visual = visual.GetVisualParent())
        {
            if (visual is Button or ToggleButton or ToggleSwitch or TextBox or ComboBox or NumericUpDown)
            {
                return;
            }
        }

        SetCurrentValue(IsExpandedProperty, !IsExpanded);
    }

    private void Update()
    {
        bool open = IsOpen;
        _header.Icon = Icon;
        _header.Title = Title;
        _header.Hint = Hint;
        _header.Action = HeaderContent;
        _header.Classes.Set("expanded", open);
        _bodyBorder.IsVisible = open;
        _chevron.Data = ToolbarButton.ResolveIcon(open ? "chevronUp" : "chevronDown");
        ToolTip.SetTip(_toggle, open ? "Collapse" : "Expand");
        IsVisible = IsShown && _filterMatch;
    }
}
