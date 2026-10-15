using System.ComponentModel;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Oadm.Client.Controls;
using Oadm.Client.LiveView;
using Oadm.Client.Tags;

namespace Oadm.Client.Devices;

public partial class DevicesView : UserControl
{
    private LiveViewViewModel? _liveView;
    private DevicesViewModel? _viewModel;

    // Device card : live view split while the panel is open (about 65 : 35), kept when the user drags the splitter.
    private GridLength _deviceColumn = new(65, GridUnitType.Star);
    private GridLength _liveColumn = new(35, GridUnitType.Star);

    public DevicesView()
    {
        InitializeComponent();
        DeviceGrid.CellPointerPressed += OnCellPointerPressed;
        DeviceGrid.Sorting += OnSorting;
        DeviceGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Selections from this size on are taken off the grid while it sorts or regroups and put back after: the grid keeps a
    /// selection row by row, so sorting 30,000 selected rows took 2 s and regrouping them 14 s (docs/30kspec.md).
    /// </summary>
    public const int LargeSelection = 1_000;

    private bool _ownSort;

    /// <summary>Time the ring gets to paint before a long grid action blocks the UI thread (tests set zero: at once).</summary>
    internal static TimeSpan BusyPaintDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>A long grid action runs with the ring over the grid (tests).</summary>
    internal bool IsGridBusy { get; private set; }

    /// <summary>Long grid actions that showed the ring (tests).</summary>
    internal int BusyRuns { get; private set; }

    private int GridRowCount => _viewModel is not { } vm ? 0 : vm.GroupByTag ? vm.TagGrouping.Rows.Count : vm.FilteredDevices.Count;

    /// <summary>
    /// A sort with a large selection takes the grid about 2 s at 30,000 selected rows (it keeps the selection row by row):
    /// the ring shows first, then the column sorts.
    /// </summary>
    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_ownSort)
        {
            _ownSort = false; // the event of the sort below: let it sort
            return;
        }

        if (DeviceGrid.SelectedItems.Count < LargeSelection)
        {
            return;
        }

        // After the grid's sort event: sorting (or changing the selection) inside it takes the grid's slow path.
        e.Handled = true;
        var column = e.Column;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RunBusyAsync("Sorting", () =>
        {
            // The grid raises the sort event again for this sort (also after it returned): that one is let through.
            _ownSort = true;
            column.Sort();
        }));
    }

    /// <summary>Ctrl+A while grouped by tag on many rows takes about 2 s at 30,000 devices: the ring shows meanwhile.</summary>
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _viewModel?.GroupByTag == true && GridRowCount >= LargeSelection)
        {
            e.Handled = true;
            _ = RunBusyAsync("Selecting", DeviceGrid.SelectAll);
        }
    }

    /// <summary>Shows the ring over the grid, lets it paint, then runs <paramref name="action"/> on the UI thread.</summary>
    internal async Task RunBusyAsync(string text, Action action)
    {
        GridBusy.Text = text;
        GridBusy.IsVisible = true;
        IsGridBusy = true;
        BusyRuns++;
        try
        {
            await Task.Delay(BusyPaintDelay); // one frame with the ring before the UI thread is busy
            action();
        }
        finally
        {
            GridBusy.IsVisible = false;
            IsGridBusy = false;
        }
    }

    /// <summary>
    /// The grouped rows were rebuilt (tags changed) while many rows are selected: regrouping them in place takes the grid
    /// 14 s at 30,000 devices. This handler runs before the grid's collection view: the grid gets a new view at once
    /// (no selection), then the selection comes back with the ring meanwhile.
    /// </summary>
    private void OnGroupRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset || _viewModel?.GroupByTag != true
            || DeviceGrid.SelectedItems.Count < LargeSelection || TopLevel.GetTopLevel(this) is not WindowBase { IsVisible: true })
        {
            return;
        }

        var all = DeviceGrid.SelectedItems.Count >= GridRowCount;
        var keep = all ? null : DeviceGrid.SelectedItems.Cast<object>().ToHashSet();
        var paused = GridSelection.Pause(DeviceGrid);
        ApplyItemsSource(); // a new view before the old one regroups: the grid lets go of the selected rows at once
        _ = RunBusyAsync("Selecting", () =>
        {
            try
            {
                if (all)
                {
                    DeviceGrid.SelectAll();
                }
                else
                {
                    foreach (var row in _viewModel?.TagGrouping.Rows.Where(r => keep!.Contains(r)) ?? [])
                    {
                        DeviceGrid.SelectedItems.Add(row);
                    }
                }
            }
            finally
            {
                paused.Dispose();
            }
        });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_liveView is not null)
        {
            _liveView.PropertyChanged -= OnLiveViewPropertyChanged;
            _liveView = null;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TagGrouping.GroupsChanged -= OnGroupsChanged;
            _viewModel.TagGrouping.Rows.CollectionChanged -= OnGroupRowsChanged;
            _viewModel = null;
        }

        if (DataContext is DevicesViewModel vm)
        {
            _viewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.TagGrouping.GroupsChanged += OnGroupsChanged;
            vm.TagGrouping.Rows.CollectionChanged += OnGroupRowsChanged; // before any collection view of the rows
            ApplyItemsSource();
            vm.Toolbar.AttachTo(ToolbarPanel, vm.ToolbarContext, TrailingToolbarPanel);
            EmptyAddButton.Flyout = Toolbar.AddToolbarPlugin.CreateMenu(vm.ToolbarContext);
            DeviceGridLayoutBinder.Attach(DeviceGrid, vm.Columns);
            _liveView = vm.LiveView;
            _liveView.PropertyChanged += OnLiveViewPropertyChanged;
            ApplyLiveViewColumns();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DevicesViewModel.GroupByTag))
        {
            ApplyItemsSource();
        }
    }

    /// <summary>Other groups (a tag appeared, was renamed or has no devices left): new group keys in tag name order.</summary>
    private void OnGroupsChanged(object? sender, EventArgs e)
    {
        if (_viewModel?.GroupByTag == true)
        {
            ApplyItemsSource();
        }
    }

    /// <summary>
    /// Normal: the device rows. Group mode: the (device x tag) rows in a collection view grouped by tag, with the groups
    /// as explicit keys so they keep the tag name order ("No tag" last) whatever column the user sorts by.
    /// </summary>
    private void ApplyItemsSource()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (!vm.GroupByTag)
        {
            DeviceGrid.Classes.Remove("tagGroups");
            DeviceGrid.ItemsSource = vm.FilteredDevices;
            ApplyTagsColumn(vm);
            return;
        }

        var view = new DataGridCollectionView(vm.TagGrouping.Rows);
        var byTag = new DataGridPathGroupDescription(nameof(DeviceTagRow.Group));
        foreach (TagGroupKey key in vm.TagGrouping.Groups)
        {
            byTag.GroupKeys.Add(key);
        }

        view.GroupDescriptions.Add(byTag);
        DeviceGrid.Classes.Add("tagGroups");
        DeviceGrid.ItemsSource = view;
        ApplyTagsColumn(vm);
    }

    /// <summary>Group mode hides the Tags column (the group header names the tag); otherwise the column chooser decides.</summary>
    private void ApplyTagsColumn(DevicesViewModel vm)
    {
        if (DeviceGrid.Columns.FirstOrDefault(c => c.Tag as string == "tags") is { } column)
        {
            column.IsVisible = (vm.Columns.Find("tags")?.IsVisible ?? true) && !DeviceGridLayoutBinder.HiddenByGroupMode(DeviceGrid, "tags");
        }
    }

    private void OnLiveViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveViewViewModel.IsOpen))
        {
            ApplyLiveViewColumns();
        }
    }

    /// <summary>Open: device card and live view side by side with the splitter between. Closed: the card takes the row.</summary>
    private void ApplyLiveViewColumns()
    {
        ColumnDefinitions columns = DeviceRow.ColumnDefinitions;
        if (_liveView?.IsOpen == true)
        {
            columns[0].Width = _deviceColumn;
            columns[2].Width = _liveColumn;
            columns[0].MinWidth = Resource("Oadm.DevicesCardMinWidth");
            columns[2].MinWidth = Resource("Oadm.LiveViewMinWidth");
        }
        else
        {
            if (columns[2].Width.IsStar)
            {
                _deviceColumn = columns[0].Width;
                _liveColumn = columns[2].Width;
            }

            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[2].Width = new GridLength(0, GridUnitType.Pixel);
            columns[0].MinWidth = 0;
            columns[2].MinWidth = 0;
        }
    }

    private double Resource(string key) => this.TryFindResource(key, out object? value) && value is double d ? d : 0;

    /// <summary>Escape closes the live view.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _liveView?.IsOpen == true)
        {
            _liveView.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Right-click on an unselected row selects it first, like ADM and Explorer.</summary>
    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.PointerPressedEventArgs.GetCurrentPoint(DeviceGrid).Properties.IsRightButtonPressed
            && e.Row.DataContext is IDeviceGridItem row
            && !DeviceGrid.SelectedItems.Contains(row))
        {
            DeviceGrid.SelectedItem = row;
        }
    }
}
