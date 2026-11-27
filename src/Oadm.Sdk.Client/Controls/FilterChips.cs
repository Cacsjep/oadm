using System.Collections.ObjectModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// One kind of filter of a list (SDK 0.17.0, <see cref="FilterSet"/>): its name in the Filter menu ("Country"), the text of
/// its chip ("Country: Austria"), whether it is set, its editor (any object; the page gives it a DataTemplate, the chip shows
/// it in a flyout) and how to clear it. The page calls <see cref="Refresh"/> when the filter's value changed.
/// </summary>
public sealed partial class FilterKind : ObservableObject
{
    private readonly Func<bool> _isSet;
    private readonly Func<string> _text;
    private readonly Action _clear;

    /// <param name="name">The entry of the Filter menu.</param>
    /// <param name="editor">What the chip's flyout shows (a view model the page has a DataTemplate for).</param>
    /// <param name="isSet">Whether the filter narrows the list now.</param>
    /// <param name="text">The chip's text ("Country: Austria", "Last 24 hours").</param>
    /// <param name="clear">Sets the filter back to everything.</param>
    public FilterKind(string name, object editor, Func<bool> isSet, Func<string> text, Action clear)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _isSet = isSet ?? throw new ArgumentNullException(nameof(isSet));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _clear = clear ?? throw new ArgumentNullException(nameof(clear));
        Text = text();
        IsSet = isSet();
    }

    /// <summary>The entry of the Filter menu ("Country").</summary>
    public string Name { get; }

    /// <summary>What the chip's flyout shows: a view model the page gives a DataTemplate.</summary>
    public object Editor { get; }

    /// <summary>The chip's text ("Country: Austria"), kept up to date by <see cref="Refresh"/>.</summary>
    [ObservableProperty] public partial string Text { get; private set; }

    /// <summary>Whether the filter narrows the list now; a set filter shows its chip.</summary>
    [ObservableProperty] public partial bool IsSet { get; private set; }

    /// <summary>The page asks to close the chip's editor (a choice that applies at once).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The chip's editor opens (the page fills it, e.g. a date range with today).</summary>
    public event EventHandler? Opening;

    /// <summary>The filter's value changed: the chip's text and state follow.</summary>
    public void Refresh()
    {
        Text = _text();
        IsSet = _isSet();
    }

    /// <summary>Closes the chip's editor, e.g. after a choice that applies at once.</summary>
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal void RaiseOpening() => Opening?.Invoke(this, EventArgs.Empty);

    internal void Clear()
    {
        _clear();
        Refresh();
    }
}

/// <summary>
/// The filters of a list as chips (SDK 0.17.0; License Plate Center's log): the <b>Filter</b> button (<see cref="FilterButton"/>)
/// lists the kinds; choosing one adds its chip to the row below the toolbar (<see cref="FilterBar"/>) and opens its editor;
/// a chip's text opens its editor again, its x clears the filter and removes the chip; <b>Clear filters</b> clears them all.
/// The row exists only while a chip is shown. A chip whose editor closes without a value goes away.
/// </summary>
public sealed partial class FilterSet : ObservableObject
{
    private readonly Action? _clearAll;

    /// <param name="kinds">The kinds in the Filter menu's order.</param>
    /// <param name="clearAll">Clears every filter at once (one read of the list); null: each kind clears itself.</param>
    public FilterSet(IReadOnlyList<FilterKind> kinds, Action? clearAll = null)
    {
        Kinds = kinds ?? throw new ArgumentNullException(nameof(kinds));
        _clearAll = clearAll;
        foreach (var kind in kinds)
        {
            kind.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterKind.IsSet) && kind.IsSet && !Shown.Contains(kind))
                {
                    Shown.Add(kind); // set from elsewhere (a link, a saved view): its chip shows
                    Changed();
                }
            };
            if (kind.IsSet)
            {
                Shown.Add(kind);
            }
        }

        AddCommand = new RelayCommand<FilterKind>(Add);
        RemoveCommand = new RelayCommand<FilterKind>(Remove);
        ClearCommand = new RelayCommand(ClearAll, () => HasShown);
    }

    /// <summary>Every kind, in the Filter menu's order.</summary>
    public IReadOnlyList<FilterKind> Kinds { get; }

    /// <summary>The chips, in the order they were added.</summary>
    public ObservableCollection<FilterKind> Shown { get; } = [];

    /// <summary>The chip row shows.</summary>
    public bool HasShown => Shown.Count > 0;

    /// <summary>A filter of the Filter menu: its chip shows and its editor opens.</summary>
    public IRelayCommand<FilterKind> AddCommand { get; }

    /// <summary>The x of a chip: the filter is cleared, the chip goes.</summary>
    public IRelayCommand<FilterKind> RemoveCommand { get; }

    /// <summary>Clear filters: every filter cleared, every chip gone.</summary>
    public IRelayCommand ClearCommand { get; }

    /// <summary>A chip's editor is to open (after Add); the bar opens it.</summary>
    public event EventHandler<FilterKind>? OpenRequested;

    /// <summary>Shows the chip of <paramref name="kind"/> (the Filter menu's entry) and asks to open its editor.</summary>
    public void Add(FilterKind? kind)
    {
        if (kind is null)
        {
            return;
        }

        if (!Shown.Contains(kind))
        {
            Shown.Add(kind);
            Changed();
        }

        OpenRequested?.Invoke(this, kind);
    }

    /// <summary>Removes the chip of <paramref name="kind"/> and sets its filter back to everything.</summary>
    public void Remove(FilterKind? kind)
    {
        if (kind is null)
        {
            return;
        }

        if (kind.IsSet)
        {
            kind.Clear();
        }

        if (Shown.Remove(kind))
        {
            Changed();
        }
    }

    /// <summary>A chip's editor closed: a chip without a value goes away.</summary>
    public void EditorClosed(FilterKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!kind.IsSet && Shown.Remove(kind))
        {
            Changed();
        }
    }

    private void ClearAll()
    {
        if (_clearAll is { } clearAll)
        {
            clearAll();
            foreach (var kind in Kinds)
            {
                kind.Refresh();
            }
        }
        else
        {
            foreach (var kind in Shown.ToList())
            {
                if (kind.IsSet)
                {
                    kind.Clear();
                }
            }
        }

        Shown.Clear();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasShown));
        ClearCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>The command button <b>Filter</b> (filter icon) of a toolbar: a menu of the kinds of a <see cref="FilterSet"/> (SDK 0.17.0).</summary>
public sealed class FilterButton : ToolbarButton
{
    /// <summary>The button's text.</summary>
    public const string ButtonText = "Filter";

    public static readonly StyledProperty<FilterSet?> FiltersProperty =
        AvaloniaProperty.Register<FilterButton, FilterSet?>(nameof(Filters));

    private readonly MenuFlyout _menu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };

    public FilterButton()
    {
        Text = ButtonText;
        IconKey = "filter";
        Flyout = _menu;
    }

    /// <summary>The filters of the list: their kinds and the chips shown.</summary>
    public FilterSet? Filters
    {
        get => GetValue(FiltersProperty);
        set => SetValue(FiltersProperty, value);
    }

    /// <summary>The menu's entries (tests).</summary>
    public IReadOnlyList<MenuItem> Entries => [.. _menu.Items.OfType<MenuItem>()];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FiltersProperty)
        {
            _menu.Items.Clear();
            foreach (var kind in Filters?.Kinds ?? [])
            {
                _menu.Items.Add(new MenuItem { Header = kind.Name, Command = Filters!.AddCommand, CommandParameter = kind });
            }
        }
    }
}

/// <summary>
/// The row of filter chips below a toolbar (SDK 0.17.0; <see cref="FilterSet"/>): a chip per shown filter (theme
/// <c>Border.chatChip</c>: the text, which opens the editor in a flyout, and an x), then <b>Clear filters</b>. Hidden while no
/// chip shows. The editors are the page's DataTemplates for the kinds' <see cref="FilterKind.Editor"/>.
/// </summary>
public sealed class FilterBar : WrapPanel
{
    /// <summary>The text of the button that removes every chip.</summary>
    public const string ClearText = "Clear filters";

    /// <summary>The tooltip of a chip's x.</summary>
    public const string RemoveTip = "Remove filter";

    public static readonly StyledProperty<FilterSet?> FiltersProperty =
        AvaloniaProperty.Register<FilterBar, FilterSet?>(nameof(Filters));

    private readonly ToolbarButton _clear = new() { Text = ClearText, IconKey = "eraser", Name = "ClearFiltersButton", VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<FilterKind, FilterChipView> _chips = [];

    public FilterBar()
    {
        Orientation = Orientation.Horizontal;
        ItemSpacing = 8;
        LineSpacing = 8;
        IsVisible = false;
        Children.Add(_clear);
    }

    /// <summary>The filters of the list: their kinds and the chips shown.</summary>
    public FilterSet? Filters
    {
        get => GetValue(FiltersProperty);
        set => SetValue(FiltersProperty, value);
    }

    /// <summary>The chip of a shown filter (tests).</summary>
    public FilterChipView? ChipOf(FilterKind kind) => _chips.GetValueOrDefault(kind);

    /// <summary>Clear filters (tests).</summary>
    public ToolbarButton ClearButton => _clear;

    protected override Type StyleKeyOverride => typeof(WrapPanel);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FiltersProperty)
        {
            return;
        }

        if (change.OldValue is FilterSet old)
        {
            old.Shown.CollectionChanged -= OnShownChanged;
            old.OpenRequested -= OnOpenRequested;
        }

        if (change.NewValue is FilterSet set)
        {
            set.Shown.CollectionChanged += OnShownChanged;
            set.OpenRequested += OnOpenRequested;
            _clear.Command = set.ClearCommand;
        }

        Rebuild();
    }

    private void OnShownChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnOpenRequested(object? sender, FilterKind kind)
    {
        Rebuild();
        if (_chips.TryGetValue(kind, out var chip))
        {
            // After the layout: the flyout opens at the chip's place.
            Avalonia.Threading.Dispatcher.UIThread.Post(chip.Open, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>The chips in the set's order, kept per kind (an open editor stays), Clear filters last.</summary>
    private void Rebuild()
    {
        var shown = Filters?.Shown.ToList() ?? [];
        foreach (var gone in _chips.Keys.Where(k => !shown.Contains(k)).ToList())
        {
            _chips.Remove(gone);
        }

        Children.Clear();
        foreach (var kind in shown)
        {
            if (!_chips.TryGetValue(kind, out var chip))
            {
                chip = new FilterChipView(kind, Filters!);
                _chips[kind] = chip;
            }

            Children.Add(chip);
        }

        Children.Add(_clear);
        IsVisible = shown.Count > 0;
    }
}

/// <summary>One filter chip of a <see cref="FilterBar"/>: the text (a click opens the editor in a flyout) and an x.</summary>
public sealed class FilterChipView : Border
{
    private readonly Flyout _flyout;
    private readonly Button _text;

    internal FilterChipView(FilterKind kind, FilterSet set)
    {
        Kind = kind;
        Classes.Add("chatChip");
        Classes.Add("filterChip");
        DataContext = kind;
        _text = new Button { Name = "FilterChipText", VerticalAlignment = VerticalAlignment.Center };
        _text.Classes.Add("toolbar");
        _text.Bind(ContentControl.ContentProperty, new Binding(nameof(FilterKind.Text)));
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new ContentControl { Content = kind.Editor, MinWidth = 220 },
        };
        _flyout.Opening += (_, _) => kind.RaiseOpening();
        _flyout.Closed += (_, _) => set.EditorClosed(kind);
        _text.Flyout = _flyout;
        kind.CloseRequested += (_, _) => _flyout.Hide();

        var remove = new Button { Name = "RemoveFilterButton", Command = set.RemoveCommand, CommandParameter = kind, VerticalAlignment = VerticalAlignment.Center };
        remove.Classes.Add("toolbar");
        remove.Classes.Add("iconOnly");
        var icon = new OadmIcon { Width = 12, Height = 12 };
        icon.Bind(OadmIcon.DataProperty, icon.GetResourceObservable("Icon.close"));
        remove.Content = icon;
        ToolTip.SetTip(remove, FilterBar.RemoveTip);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _text, remove } };
    }

    /// <summary>The filter this chip shows.</summary>
    public FilterKind Kind { get; }

    /// <summary>The chip's editor (tests and the bar).</summary>
    public Flyout Editor => _flyout;

    /// <summary>The text button (tests).</summary>
    public Button TextButton => _text;

    /// <summary>Opens the editor at the chip.</summary>
    public void Open()
    {
        if (TopLevel.GetTopLevel(this) is not null)
        {
            _flyout.ShowAt(_text);
        }
    }
}
