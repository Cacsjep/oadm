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
        TagGroupRows.Attach(DeviceGrid);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Selections from this size on are taken off the grid while it sorts or regroups and put back after: after a sort or
    /// regroup the grid looks up every selected row in the whole list (quadratic: sorting 30,000 selected rows took 4 s,
    /// 60,000 took 24 s; regrouping 30,000 took 14 s).
    /// </summary>
    public const int LargeSelection = 1_000;

    private bool _ownSort;

    /// <summary>Time the ring gets to paint before a long grid action blocks the UI thread (tests set zero: at once).</summary>
    internal static TimeSpan BusyPaintDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>A long grid action runs with the ring over the grid (tests).</summary>
    internal bool IsGridBusy { get; private set; }

    /// <summary>Long grid actions that showed the ring (tests).</summary>
    internal int BusyRuns { get; private set; }

    private System.Collections.IEnumerable? GridRows => _viewModel is not { } vm ? null : vm.GroupByTag ? vm.TagGrouping.Rows : vm.FilteredDevices;

    /// <summary>
    /// A sort with a large selection: the ring shows first, the selection comes off the grid, the column sorts and the
    /// selection goes back (the view model keeps its selection meanwhile).
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
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RunBusyAsync("Sorting", async () =>
        {
            var selection = TakeSelectionOff();
            try
            {
                // The grid raises the sort event again for this sort, in a job of its own: that one is let through. Cleared
                // after the sort in any case, so a sort that raised nothing never makes the next one skip the ring.
                _ownSort = true;
                column.Sort();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(static () => { }, Avalonia.Threading.DispatcherPriority.Background);
            }
            finally
            {
                _ownSort = false;
                selection.PutBack(GridRows);
            }
        }));
    }

    /// <summary>
    /// Takes a large selection off the grid without telling the view model; <see cref="TakenSelection.PutBack"/> selects
    /// the same rows again in the grid's current rows and hands the result to the view model. Every row selected is told
    /// by the count alone: in a grouped view the grid finds each selected row by walking its group from the start.
    /// </summary>
    private TakenSelection TakeSelectionOff()
    {
        int rows = DeviceGrid.ItemsSource switch
        {
            DataGridCollectionView view => view.Count,
            System.Collections.ICollection list => list.Count,
            _ => int.MaxValue,
        };
        bool all = DeviceGrid.SelectedItems.Count >= rows;
        var keep = all ? null : DeviceGrid.SelectedItems.Cast<object>().ToHashSet();
        var paused = GridSelection.Pause(DeviceGrid);
        DeviceGrid.SelectedItems.Clear();
        return new TakenSelection(DeviceGrid, keep, paused);
    }

    /// <summary>
    /// A selection taken off the grid (<paramref name="keep"/> null: every row). Putting it back: every row in one step;
    /// otherwise row by row (the grid looks up each row it adds in the whole list), in the plain grid from the smaller side
    /// (in a grouped view removing a row makes the grid walk the whole selection). Across a switch of group by tag the
    /// devices stay selected: every tag row of a selected device, every device of a selected tag row.
    /// </summary>
    private sealed class TakenSelection(DataGrid grid, HashSet<object>? keep, IDisposable paused)
    {
        private readonly HashSet<DeviceRowViewModel> _devices = keep is null ? [] : [.. DeviceTagGrouping.DistinctDevices(keep)];
        private readonly bool _fromGroups = keep?.Any(o => o is DeviceTagRow) == true;

        private bool Kept(object row) => row switch
        {
            DeviceTagRow tagRow => _fromGroups ? keep!.Contains(tagRow) : _devices.Contains(tagRow.Row),
            DeviceRowViewModel device => _devices.Contains(device),
            _ => keep!.Contains(row),
        };

        public void PutBack(System.Collections.IEnumerable? rows)
        {
            List<object> current = rows is null ? [] : [.. rows.Cast<object>()];
            List<object> selected = [];
            try
            {
                if (keep is null)
                {
                    grid.SelectAll();
                    selected = current;
                    return;
                }

                var kept = current.ConvertAll(Kept);
                selected = [.. current.Where((_, i) => kept[i])];
                if (selected.Count == current.Count && current.Count > 0)
                {
                    grid.SelectAll();
                }
                else if (selected.Count * 2 > current.Count && grid.ItemsSource is not DataGridCollectionView)
                {
                    grid.SelectAll();
                    foreach (var row in current.Where((_, i) => !kept[i]))
                    {
                        grid.SelectedItems.Remove(row);
                    }
                }
                else
                {
                    foreach (var row in selected)
                    {
                        grid.SelectedItems.Add(row);
                    }
                }
            }
            finally
            {
                paused.Dispose();
                GridSelection.Sync(grid, selected); // the view model gets the selection of the current rows
            }
        }
    }

    /// <summary>Shows the ring over the grid, lets it paint, then runs <paramref name="action"/> on the UI thread.</summary>
    internal Task RunBusyAsync(string text, Action action) => RunBusyAsync(text, () =>
    {
        action();
        return Task.CompletedTask;
    });

    /// <summary>Shows the ring over the grid, lets it paint, then runs <paramref name="action"/> on the UI thread until it ends.</summary>
    internal async Task RunBusyAsync(string text, Func<Task> action)
    {
        GridBusy.Text = text;
        GridBusy.IsVisible = true;
        IsGridBusy = true;
        BusyRuns++;
        try
        {
            // One frame with the ring before the UI thread is busy; without a delay (tests) still after the current job, so
            // the work sees what the job still changes (a second items source when group by tag turns on).
            if (BusyPaintDelay > TimeSpan.Zero)
            {
                await Task.Delay(BusyPaintDelay);
            }
            else
            {
                await Task.Yield();
            }

            await action();
        }
        finally
        {
            GridBusy.IsVisible = false;
            IsGridBusy = false;
        }
    }

    /// <summary>
    /// The grouped rows were rebuilt (tags changed, group by tag turned on) while many rows are selected: regrouping them in
    /// place takes the grid 14 s at 30,000 devices. This handler runs before the grid's collection view: the grid gets a new
    /// view at once (the selection taken off first), then the selection comes back with the ring meanwhile.
    /// </summary>
    private void OnGroupRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && _viewModel?.GroupByTag == true)
        {
            ReplaceItemsSource(onlyLargeSelection: true);
        }
    }

    /// <summary>
    /// A new items source. With a large selection it comes off first and goes back after with the ring: the grid would
    /// look up every selected row in the whole new view. <paramref name="onlyLargeSelection"/>: nothing to do otherwise.
    /// </summary>
    private void ReplaceItemsSource(bool onlyLargeSelection = false)
    {
        if (DeviceGrid.SelectedItems.Count < LargeSelection || TopLevel.GetTopLevel(this) is not WindowBase { IsVisible: true })
        {
            if (!onlyLargeSelection)
            {
                ApplyItemsSource();
            }

            return;
        }

        var selection = TakeSelectionOff();
        ApplyItemsSource();
        _ = RunBusyAsync("Selecting", () => selection.PutBack(GridRows));
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
            _viewModel.TagGrouping.Rows.CollectionChanged -= OnGroupRowsChanged;
            _viewModel.Compliance.ChecksChanged -= OnComplianceChecksChanged;
            _viewModel = null;
        }

        if (DataContext is DevicesViewModel vm)
        {
            _viewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.TagGrouping.Rows.CollectionChanged += OnGroupRowsChanged; // before any collection view of the rows
            ApplyItemsSource();
            vm.Toolbar.AttachTo(ToolbarPanel, vm.ToolbarContext, TrailingToolbarPanel);
            EmptyAddButton.Flyout = Toolbar.AddToolbarPlugin.CreateMenu(vm.ToolbarContext);
            Toolbar.AddToolbarPlugin.FollowRefusal(EmptyAddButton, vm.ToolbarContext, null);
            DeviceGridLayoutBinder.Attach(DeviceGrid, vm.Columns);
            _liveView = vm.LiveView;
            _liveView.PropertyChanged += OnLiveViewPropertyChanged;
            ApplyLiveViewColumns();
            vm.Compliance.ChecksChanged += OnComplianceChecksChanged;
            ApplyComplianceColumns();
            ApplySiteColumn();
        }
    }

    private const string CheckColumnPrefix = "check:";
    private IReadOnlyList<(string Id, string Name)> _checkColumns = [];

    private void OnComplianceChecksChanged(object? sender, EventArgs e) => ApplyComplianceColumns();

    /// <summary>
    /// The Compliance column (worst result), or with "Show each compliance check" one column per check that is on, after
    /// the Compliance column's place; none while the role may not see compliance.
    /// </summary>
    private void ApplyComplianceColumns()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        var expanded = vm.ExpandCompliance && vm.HasCompliance && vm.ComplianceColumnOn;
        DeviceGrid.Classes.Set("complianceExpanded", expanded);
        DeviceGrid.Classes.Set("noCompliance", !vm.HasCompliance);
        if (DeviceGrid.Columns.FirstOrDefault(c => c.Tag as string == ColumnLayoutViewModel.ComplianceKey) is { } summary)
        {
            summary.IsVisible = (vm.Columns.Find(ColumnLayoutViewModel.ComplianceKey)?.IsVisible ?? true)
                && !DeviceGridLayoutBinder.HiddenByGroupMode(DeviceGrid, ColumnLayoutViewModel.ComplianceKey);
        }

        var wanted = expanded ? vm.Compliance.Checks.Where(c => c.Enabled).Select(c => (c.Id, c.Name)).ToList() : [];
        if (wanted.SequenceEqual(_checkColumns))
        {
            return;
        }

        _checkColumns = wanted;
        foreach (var column in DeviceGrid.Columns.Where(c => c.Tag is string tag && tag.StartsWith(CheckColumnPrefix, StringComparison.Ordinal)).ToList())
        {
            DeviceGrid.Columns.Remove(column);
        }

        var at = DeviceGrid.Columns.FirstOrDefault(c => c.Tag as string == ColumnLayoutViewModel.ComplianceKey)?.DisplayIndex ?? DeviceGrid.Columns.Count;
        foreach (var check in expanded ? vm.Compliance.Checks.Where(c => c.Enabled) : [])
        {
            var column = Compliance.ComplianceView.CheckColumn(vm.Compliance, check);
            DeviceGrid.Columns.Add(column);
            column.DisplayIndex = Math.Min(++at, DeviceGrid.Columns.Count - 1);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DevicesViewModel.GroupByTag))
        {
            ReplaceItemsSource();
        }
        else if (e.PropertyName is nameof(DevicesViewModel.ExpandCompliance) or nameof(DevicesViewModel.HasCompliance) or nameof(DevicesViewModel.ComplianceColumnOn))
        {
            ApplyComplianceColumns();
        }
        else if (e.PropertyName == nameof(DevicesViewModel.HasSites))
        {
            ApplySiteColumn();
        }
    }

    /// <summary>The Site column shows while the server has sites and the column chooser has it on.</summary>
    private void ApplySiteColumn()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        DeviceGrid.Classes.Set("noSites", !vm.HasSites);
        if (DeviceGrid.Columns.FirstOrDefault(c => c.Tag as string == DevicesViewModel.SiteColumnKey) is { } column)
        {
            column.IsVisible = (vm.Columns.Find(DevicesViewModel.SiteColumnKey)?.IsVisible ?? true)
                && !DeviceGridLayoutBinder.HiddenByGroupMode(DeviceGrid, DevicesViewModel.SiteColumnKey);
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

        DeviceGrid.Classes.Add("tagGroups");
        DeviceGrid.ItemsSource = vm.TagGrouping.CreateView();
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
