using System.Collections.ObjectModel;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Sdk.Client.Controls;

/// <summary>One column of a grid in the column chooser (SDK 0.17.0; the device grid, the Log of License Plate Center).</summary>
public sealed partial class GridColumnOption(string key, string header, bool canHide = true) : ObservableObject
{
    /// <summary>The column's key: the <c>Tag</c> of its <see cref="DataGridColumn"/> and the saved layout.</summary>
    public string Key { get; } = key;

    public string Header { get; } = header;

    /// <summary>False for a column that always shows (the device grid's icon); the chooser leaves it out.</summary>
    public bool CanHide { get; } = canHide;

    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    /// <summary>The chooser offers the column (false: greyed with <see cref="UnavailableReason"/>, e.g. compliance is off).</summary>
    [ObservableProperty] public partial bool IsAvailable { get; set; } = true;

    /// <summary>Why the column cannot be turned on now (the chooser's tooltip).</summary>
    [ObservableProperty] public partial string? UnavailableReason { get; set; }

    /// <summary>Pixel width, 0 = the width of the view.</summary>
    public double Width { get; set; }

    /// <summary>The user's order (0 first).</summary>
    public int DisplayIndex { get; set; }
}

/// <summary>The saved state of one column: what <see cref="GridColumnLayout"/> loads and saves.</summary>
public sealed record GridColumnState(string Key, bool IsVisible, double Width, int DisplayIndex);

/// <summary>A column of <see cref="GridColumnLayout"/>: its key, header, whether it can be hidden and whether it shows by default.</summary>
public sealed record GridColumnDefinition(string Key, string Header, bool CanHide = true, bool IsDefault = true);

/// <summary>
/// The columns of a grid the user chooses (SDK 0.17.0): which show, their order and widths, kept per user through
/// <c>load</c> and <c>save</c> (the device grid: the client's settings; a plugin page: <c>Get/SetLocalSetting</c>). Bind
/// a <see cref="DataGrid"/> with <see cref="GridColumnBinder.Attach"/> (columns matched by their <c>Tag</c>) and offer
/// <see cref="Choosable"/> with a <see cref="ColumnChooserButton"/>. View model side, testable without Avalonia.
/// </summary>
public sealed class GridColumnLayout
{
    private readonly IReadOnlyList<GridColumnDefinition> _definitions;
    private readonly Action<IReadOnlyList<GridColumnState>> _save;

    /// <param name="columns">The columns in their default order.</param>
    /// <param name="load">The saved state (empty: the defaults); unknown keys are ignored, new columns get their default.</param>
    /// <param name="save">Keeps the state after every change.</param>
    public GridColumnLayout(IReadOnlyList<GridColumnDefinition> columns, IReadOnlyList<GridColumnState> load, Action<IReadOnlyList<GridColumnState>> save)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(load);
        _definitions = columns;
        _save = save ?? throw new ArgumentNullException(nameof(save));
        var saved = load.Where(c => !string.IsNullOrEmpty(c.Key)).GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            var definition = columns[i];
            var option = new GridColumnOption(definition.Key, definition.Header, definition.CanHide) { DisplayIndex = i, IsVisible = definition.IsDefault || !definition.CanHide };
            if (saved.TryGetValue(definition.Key, out var entry))
            {
                option.IsVisible = entry.IsVisible || !option.CanHide;
                option.Width = entry.Width;
                option.DisplayIndex = entry.DisplayIndex;
            }

            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GridColumnOption.IsVisible))
                {
                    Save();
                }
            };
            Columns.Add(option);
        }

        NormalizeOrder();
    }

    /// <summary>All columns in their default order; <see cref="GridColumnOption.DisplayIndex"/> holds the user's order.</summary>
    public ObservableCollection<GridColumnOption> Columns { get; } = [];

    /// <summary>The columns the user may hide (the chooser's list).</summary>
    public IEnumerable<GridColumnOption> Choosable => Columns.Where(c => c.CanHide);

    /// <summary>The keys of the shown columns in the user's order (an export takes these).</summary>
    public IReadOnlyList<string> VisibleKeys => [.. Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).Select(c => c.Key)];

    public GridColumnOption? Find(string key) => Columns.FirstOrDefault(c => c.Key == key);

    /// <summary>Called by the view after the user reordered or resized columns.</summary>
    public void Capture(IEnumerable<(string Key, int DisplayIndex, double Width)> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var changed = false;
        foreach (var (key, index, width) in state)
        {
            if (Find(key) is not { } option)
            {
                continue;
            }

            if (option.DisplayIndex != index || Math.Abs(option.Width - width) > 0.5)
            {
                option.DisplayIndex = index;
                option.Width = width;
                changed = true;
            }
        }

        if (changed)
        {
            NormalizeOrder();
            Save();
        }
    }

    /// <summary>The default columns, order and widths.</summary>
    public void ResetToDefault()
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            Columns[i].DisplayIndex = i;
            Columns[i].Width = 0;
            Columns[i].IsVisible = _definitions[i].IsDefault || !_definitions[i].CanHide;
        }

        Save();
    }

    public void Save() => _save([.. Columns.Select(c => new GridColumnState(c.Key, c.IsVisible, c.Width, c.DisplayIndex))]);

    private void NormalizeOrder()
    {
        var ordered = Columns.Select((c, i) => (c, i)).OrderBy(x => x.c.DisplayIndex).ThenBy(x => x.i).Select(x => x.c).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].DisplayIndex = i;
        }
    }
}

/// <summary>
/// The view glue of a <see cref="GridColumnLayout"/> (SDK 0.17.0): applies the saved visibility, order and widths to a
/// <see cref="DataGrid"/> (columns matched by their <c>Tag</c>; saved widths as star weights, so the grid keeps filling its
/// width) and captures the user's reorder and resize.
/// </summary>
public sealed class GridColumnBinder : IDisposable
{
    private readonly DataGrid _grid;
    private readonly GridColumnLayout _layout;
    private readonly Func<string, bool>? _hidden;
    private readonly IReadOnlySet<string> _fixedWidth;
    private bool _listens;
    private bool _disposed;

    private GridColumnBinder(DataGrid grid, GridColumnLayout layout, Func<string, bool>? hidden, IReadOnlySet<string> fixedWidth)
    {
        _grid = grid;
        _layout = layout;
        _hidden = hidden;
        _fixedWidth = fixedWidth;
    }

    /// <summary>Binds the grid to the layout until the result is disposed.</summary>
    /// <param name="grid">The grid; its columns carry their key in <c>Tag</c>.</param>
    /// <param name="layout">The columns the user chose.</param>
    /// <param name="hidden">Columns a mode hides whatever the chooser says (the device grid's Tags column in group mode); null: none.</param>
    /// <param name="fixedWidth">Columns that keep the width of the view (an icon, a picture).</param>
    public static IDisposable Attach(DataGrid grid, GridColumnLayout layout, Func<string, bool>? hidden = null, IReadOnlySet<string>? fixedWidth = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(layout);
        var binder = new GridColumnBinder(grid, layout, hidden, fixedWidth ?? new HashSet<string>(StringComparer.Ordinal));
        binder.Apply();
        foreach (var option in layout.Columns)
        {
            option.PropertyChanged += binder.OnOptionChanged;
        }

        binder._listens = true;
        grid.ColumnReordered += binder.OnColumnReordered;
        grid.AddHandler(InputElement.PointerReleasedEvent, binder.OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        grid.DetachedFromVisualTree += binder.OnDetached;
        return binder;
    }

    /// <summary>Shows or hides every column again (a mode of <c>hidden</c> changed).</summary>
    public static void Refresh(DataGrid grid, GridColumnLayout layout, Func<string, bool>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(layout);
        foreach (var column in grid.Columns)
        {
            if (KeyOf(column) is { } key && layout.Find(key) is { } option)
            {
                column.IsVisible = option.IsVisible && hidden?.Invoke(key) != true;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopListening();
        _grid.ColumnReordered -= OnColumnReordered;
        _grid.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        _grid.DetachedFromVisualTree -= OnDetached;
    }

    private static string? KeyOf(DataGridColumn column) => column.Tag as string;

    private void OnColumnReordered(object? sender, DataGridColumnEventArgs e) => Capture();

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => Capture();

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Capture();
        StopListening();
    }

    private void StopListening()
    {
        if (!_listens)
        {
            return;
        }

        _listens = false;
        foreach (var option in _layout.Columns)
        {
            option.PropertyChanged -= OnOptionChanged;
        }
    }

    private void Apply()
    {
        var ordered = _grid.Columns
            .Select(c => (Column: c, Option: KeyOf(c) is { } key ? _layout.Find(key) : null))
            .Where(x => x.Option is not null)
            .OrderBy(x => x.Option!.DisplayIndex)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (column, option) = ordered[i];
            column.IsVisible = option!.IsVisible && _hidden?.Invoke(option.Key) != true;
            if (option.Width > 0 && !_fixedWidth.Contains(option.Key))
            {
                column.Width = new DataGridLength(option.Width, DataGridLengthUnitType.Star);
            }

            column.DisplayIndex = i;
        }
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is GridColumnOption option && e.PropertyName == nameof(GridColumnOption.IsVisible)
            && _grid.Columns.FirstOrDefault(c => KeyOf(c) == option.Key) is { } column)
        {
            column.IsVisible = option.IsVisible && _hidden?.Invoke(option.Key) != true;
        }
    }

    private void Capture()
    {
        var state = _grid.Columns
            .Where(c => KeyOf(c) is not null)
            .Select(c => (KeyOf(c)!, c.DisplayIndex, c.IsVisible && c.ActualWidth > 0 ? c.ActualWidth : _layout.Find(KeyOf(c)!)?.Width ?? 0))
            .ToList();
        _layout.Capture(state);
    }
}

/// <summary>
/// The "Choose columns" button of a grid's toolbar (SDK 0.17.0): an icon-only command button with the columns icon and a
/// flyout "Visible columns" with a check box per <see cref="GridColumnOption"/> of <see cref="Columns"/>.
/// </summary>
public sealed class ColumnChooserButton : Button
{
    public const string TipText = "Choose columns";
    public const string ListTitle = "Visible columns";

    public static readonly StyledProperty<IEnumerable<GridColumnOption>?> ColumnsProperty =
        AvaloniaProperty.Register<ColumnChooserButton, IEnumerable<GridColumnOption>?>(nameof(Columns));

    private readonly ItemsControl _list = new() { Name = "ColumnList" };

    public ColumnChooserButton()
    {
        Classes.Add("toolbar");
        Classes.Add("iconOnly");
        ToolTip.SetTip(this, TipText);
        var icon = new OadmIcon();
        icon.Bind(OadmIcon.DataProperty, icon.GetResourceObservable("Icon.columns"));
        Content = icon;
        _list.ItemTemplate = new FuncDataTemplate<GridColumnOption>((option, _) =>
        {
            var box = new CheckBox();
            box.Bind(ContentProperty, new Binding(nameof(GridColumnOption.Header)));
            box.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(GridColumnOption.IsVisible)) { Mode = BindingMode.TwoWay });
            box.Bind(IsEnabledProperty, new Binding(nameof(GridColumnOption.IsAvailable)));
            box.Bind(ToolTip.TipProperty, new Binding(nameof(GridColumnOption.UnavailableReason)));
            ToolTip.SetShowOnDisabled(box, true);
            return box;
        });
        var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
        panel.Children.Add(new TextBlock { Text = ListTitle, Margin = new Thickness(0, 0, 0, 6), Classes = { "secondary" } });
        panel.Children.Add(_list);
        Flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = panel };
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The columns the user may hide (<see cref="GridColumnLayout.Choosable"/>).</summary>
    public IEnumerable<GridColumnOption>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>The list of the flyout (tests).</summary>
    public ItemsControl List => _list;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty)
        {
            _list.ItemsSource = Columns;
        }
    }
}
