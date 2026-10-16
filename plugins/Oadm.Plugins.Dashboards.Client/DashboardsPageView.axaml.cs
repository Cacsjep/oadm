using System.Collections.Specialized;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Layout;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>
/// The Dashboards page view: one <see cref="TileGrid"/> per open dashboard (only the active one shown, so the hidden
/// tabs' videos rest), the lists on the left, dragging an event onto the edited dashboard, and selecting widgets while
/// editing: a click selects one (its panel), Ctrl+click adds or removes one, a drag over empty cells selects every widget
/// it touches, Ctrl+A selects all.
/// </summary>
public sealed partial class DashboardsPageView : UserControl
{
    /// <summary>A press must move this far before it is a drag (a shorter one is a click).</summary>
    private const double DragThreshold = 6;

    private readonly Dictionary<DashboardTab, TileGrid> _pages = [];
    private DashboardsPageViewModel? _vm;
    private CatalogEvent? _pressed;
    private TileGrid? _areaGrid; // a selection rectangle is being drawn on this grid
    private (int X, int Y) _areaStart;
    private Point _areaPoint;
    private TileRect? _area;
    private bool _areaAdds;
    private Point _pressPoint;
    private bool _dragging;
    private bool _dragEnded;

    public DashboardsPageView()
    {
        InitializeComponent();
        // A new panel for every widget: a recycled one would push one widget's select values into the next.
        EditorHost.DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate<WidgetEditorViewModel>((_, _) => new WidgetEditorView(), supportsRecycling: false));
        EditorHost.DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate<GroupEditorViewModel>((_, _) => new GroupEditorView(), supportsRecycling: false));
        DashboardList.AddHandler(TappedEvent, OnDashboardTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        CollapsedDashboardList.AddHandler(TappedEvent, OnDashboardTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        EventList.AddHandler(TappedEvent, OnEventTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        EventList.AddHandler(PointerPressedEvent, OnSourcePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Pages.AddHandler(PointerPressedEvent, OnGridPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            EndDrag();
            EndArea();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        KeyDown += OnKeyDown;
    }

    /// <summary>Ctrl+A while editing: every widget.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            && _vm?.ActiveTab is { IsEditing: true } tab && e.Source is not TextBox)
        {
            tab.SelectAll();
            e.Handled = true;
        }
    }

    private static bool AddsToSelection(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.PickDevice = null;
            _vm.PickDevices = null;
        }

        RemoveAllPages();
        _vm = DataContext as DashboardsPageViewModel;
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged += OnTabsChanged;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.PickDevice = PickDeviceAsync;
            _vm.PickDevices = PickDevicesAsync;
            foreach (var tab in _vm.Tabs)
            {
                AddPage(tab);
            }

            ShowActive();
        }
    }

    /// <summary>The grid of the active dashboard (tests).</summary>
    internal TileGrid? ActiveGrid => _vm?.ActiveTab is { } tab ? _pages.GetValueOrDefault(tab) : null;

    private async Task<Guid?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, Guid? selected) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowOneAsync(owner, devices, selected, heading: "Choose a device").ConfigureAwait(true)
            : null;

    private async Task<IReadOnlyList<Guid>?> PickDevicesAsync(string heading, IReadOnlyList<IDeviceInfo> devices, Func<IDeviceInfo, string?> unavailable) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowAsync(owner, devices, null, unavailable, heading).ConfigureAwait(true)
            : null;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardsPageViewModel.ActiveTab))
        {
            ShowActive();
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RemoveAllPages();
            return;
        }

        foreach (var tab in e.OldItems?.OfType<DashboardTab>() ?? [])
        {
            RemovePage(tab);
        }

        foreach (var tab in e.NewItems?.OfType<DashboardTab>() ?? [])
        {
            AddPage(tab);
        }

        ShowActive();
    }

    private void AddPage(DashboardTab tab)
    {
        if (_pages.ContainsKey(tab))
        {
            return;
        }

        var grid = new TileGrid { Source = tab, IsVisible = false };
        _pages[tab] = grid;
        Pages.Children.Add(grid);
    }

    private void RemovePage(DashboardTab tab)
    {
        if (_pages.Remove(tab, out var grid))
        {
            Pages.Children.Remove(grid);
            grid.Source = null;
        }
    }

    private void RemoveAllPages()
    {
        foreach (var tab in _pages.Keys.ToList())
        {
            RemovePage(tab);
        }
    }

    /// <summary>Only the active dashboard is visible: the others' videos stop after a moment.</summary>
    private void ShowActive()
    {
        var active = _vm?.ActiveTab;
        foreach (var (tab, grid) in _pages)
        {
            grid.IsVisible = tab == active;
        }

        DashboardList.SelectedItem = active?.Id is { } id ? _vm!.Dashboards.FirstOrDefault(d => d.Id == id) : null;
        CollapsedDashboardList.SelectedItem = DashboardList.SelectedItem;
    }

    private void OnDashboardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && !IsInButton(source) && source.DataContext is DashboardEntry entry)
        {
            _vm?.OpenDashboardCommand.Execute(entry);
        }
    }

    private void OnEventTapped(object? sender, TappedEventArgs e)
    {
        if (!_dragEnded && e.Source is Control { DataContext: EventListItem { Event: { } catalogEvent } })
        {
            _vm?.ClickEvent(catalogEvent);
        }

        _dragEnded = false;
    }

    private static bool IsInButton(Visual? visual)
    {
        for (var v = visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button)
            {
                return true;
            }

            if (v is ListBoxItem)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// A press while editing: on a widget it selects it (one already selected keeps the selection, so the group can be
    /// dragged; Ctrl+click adds or removes it); on empty cells it starts a selection rectangle.
    /// </summary>
    private void OnGridPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm?.ActiveTab is not { IsEditing: true } tab || e.Source is not Visual source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(); // Ctrl+A
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return; // the copy icon and the X
        }

        var adds = AddsToSelection(e.KeyModifiers);
        if (source.FindAncestorOfType<TileFrame>(includeSelf: true) is { Item: WidgetViewModel widget })
        {
            if (adds)
            {
                tab.Toggle(widget);
                e.Handled = true; // no move with Ctrl
            }
            else if (!tab.Selection.Contains(widget))
            {
                tab.Select(widget);
            }

            return;
        }

        if (ActiveGrid is { } grid)
        {
            _areaGrid = grid;
            _areaPoint = e.GetPosition(grid);
            _areaStart = grid.CellAt(_areaPoint);
            _areaAdds = adds;
            _area = null;
            e.Pointer.Capture(grid);
        }
    }

    /// <summary>The cells between the press and the pointer, while a selection rectangle is drawn.</summary>
    private void MoveArea(PointerEventArgs e)
    {
        var grid = _areaGrid!;
        var point = e.GetPosition(grid);
        if (_area is null && Math.Abs(point.X - _areaPoint.X) < DragThreshold && Math.Abs(point.Y - _areaPoint.Y) < DragThreshold)
        {
            return;
        }

        var (x, y) = grid.CellAt(point);
        _area = new TileRect(Math.Min(x, _areaStart.X), Math.Min(y, _areaStart.Y), Math.Abs(x - _areaStart.X) + 1, Math.Abs(y - _areaStart.Y) + 1);
        grid.ShowSelectionArea(_area);
        e.Handled = true;
    }

    /// <summary>Released: the widgets the rectangle touches are selected; a click on empty cells clears the selection.</summary>
    private void ReleaseArea(PointerReleasedEventArgs e)
    {
        var area = _area;
        var adds = _areaAdds;
        e.Pointer.Capture(null);
        EndArea();
        if (_vm?.ActiveTab is not { IsEditing: true } tab)
        {
            return;
        }

        if (area is { } rect)
        {
            tab.SelectArea(rect, adds);
        }
        else if (!adds)
        {
            tab.Select(null);
        }
    }

    private void EndArea()
    {
        _areaGrid?.ShowSelectionArea(null);
        _areaGrid = null;
        _area = null;
    }

    // Dragging an event from the left: a press on a row, a move beyond the threshold starts the drag (only while a
    // dashboard is edited), the release over the grid drops it on that cell.
    private void OnSourcePressed(object? sender, PointerPressedEventArgs e)
    {
        _dragEnded = false;
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && (e.Source as Control)?.DataContext is EventListItem { Event: { } catalogEvent }
            ? catalogEvent
            : null;
        _pressPoint = e.GetPosition(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_areaGrid is not null)
        {
            MoveArea(e);
            return;
        }

        if (_pressed is null || _vm?.ActiveTab is not { IsEditing: true } tab)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(point.X - _pressPoint.X) < DragThreshold && Math.Abs(point.Y - _pressPoint.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            DragGhostText.Text = _pressed.Name;
            DragGhost.IsVisible = true;
            e.Pointer.Capture(this);
        }

        Canvas.SetLeft(DragGhost, point.X + 12);
        Canvas.SetTop(DragGhost, point.Y + 8);
        if (ActiveGrid is { } grid)
        {
            grid.ShowPreview(PreviewAt(grid, tab, e.GetPosition(grid)), _pressed.Name);
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_areaGrid is not null)
        {
            ReleaseArea(e);
            return;
        }

        var pressed = _pressed;
        var dragging = _dragging;
        _pressed = null;
        if (!dragging)
        {
            return;
        }

        e.Pointer.Capture(null);
        EndDrag();
        _dragEnded = true;
        e.Handled = true;
        if (pressed is not null && ActiveGrid is { } grid && _vm?.ActiveTab is { IsEditing: true } tab)
        {
            DropOn(grid, tab, pressed, e.GetPosition(grid));
        }
    }

    /// <summary>Where an event dropped at <paramref name="point"/> would land; null outside the grid or without room.</summary>
    internal static TileRect? PreviewAt(TileGrid grid, DashboardTab tab, Point point)
    {
        if (!grid.Contains(point))
        {
            return null;
        }

        var (x, y) = grid.CellAt(point);
        return tab.PreviewAt(x, y);
    }

    /// <summary>Drops an event at a point of the grid; outside the grid nothing happens.</summary>
    internal static bool DropOn(TileGrid grid, DashboardTab tab, CatalogEvent catalogEvent, Point point)
    {
        if (!grid.Contains(point))
        {
            return false;
        }

        var (x, y) = grid.CellAt(point);
        return tab.DropEvent(catalogEvent, x, y);
    }

    private void EndDrag()
    {
        _dragging = false;
        DragGhost.IsVisible = false;
        ActiveGrid?.ShowPreview(null);
    }
}
