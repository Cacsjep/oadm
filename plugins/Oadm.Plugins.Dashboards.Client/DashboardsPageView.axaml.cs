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
/// tabs' videos rest), the lists on the left, dragging an event or a camera onto the edited dashboard, and a click on a
/// widget while editing that opens its panel.
/// </summary>
public sealed partial class DashboardsPageView : UserControl
{
    /// <summary>A press must move this far before it is a drag (a shorter one is a click).</summary>
    private const double DragThreshold = 6;

    private readonly Dictionary<DashboardTab, TileGrid> _pages = [];
    private DashboardsPageViewModel? _vm;
    private object? _pressed; // a DeviceTreeDevice or a CatalogEvent
    private Point _pressPoint;
    private bool _dragging;
    private bool _dragEnded;

    public DashboardsPageView()
    {
        InitializeComponent();
        // A new panel for every widget: a recycled one would push one widget's select values into the next.
        EditorHost.DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate<WidgetEditorViewModel>((_, _) => new WidgetEditorView(), supportsRecycling: false));
        Tree.DeviceInvoked += (_, row) =>
        {
            if (!_dragEnded)
            {
                _vm?.ClickCamera(row.Device);
            }

            _dragEnded = false;
        };
        DashboardList.AddHandler(TappedEvent, OnDashboardTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        CollapsedDashboardList.AddHandler(TappedEvent, OnDashboardTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        EventList.AddHandler(TappedEvent, OnEventTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerPressedEvent, OnSourcePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        EventList.AddHandler(PointerPressedEvent, OnSourcePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Pages.AddHandler(PointerPressedEvent, OnGridPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

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

    private async Task<IReadOnlyList<Guid>?> PickDevicesAsync(IReadOnlyList<IDeviceInfo> devices, Func<IDeviceInfo, string?> unavailable) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowAsync(owner, devices, null, unavailable, "Copy for other devices").ConfigureAwait(true)
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

    /// <summary>A press on a widget while editing opens its panel (the frame's own drag moves it).</summary>
    private void OnGridPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm?.ActiveTab is not { IsEditing: true } tab || e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return; // the X removes the widget
        }

        if (source.FindAncestorOfType<TileFrame>(includeSelf: true) is { Item: WidgetViewModel widget })
        {
            tab.Select(widget);
        }
    }

    // Dragging an event or a camera from the left: a press on a row, a move beyond the threshold starts the drag (only
    // while a dashboard is edited), the release over the grid drops it on that cell.
    private void OnSourcePressed(object? sender, PointerPressedEventArgs e)
    {
        _dragEnded = false;
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? (e.Source as Control)?.DataContext switch
        {
            DeviceTreeDevice device => device,
            EventListItem { Event: { } catalogEvent } => catalogEvent,
            _ => null,
        } : null;
        _pressPoint = e.GetPosition(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
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
            DragGhostText.Text = NameOf(_pressed);
            DragGhost.IsVisible = true;
            e.Pointer.Capture(this);
        }

        Canvas.SetLeft(DragGhost, point.X + 12);
        Canvas.SetTop(DragGhost, point.Y + 8);
        if (ActiveGrid is { } grid)
        {
            grid.ShowPreview(PreviewAt(grid, tab, _pressed is DeviceTreeDevice, e.GetPosition(grid)), NameOf(_pressed));
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
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

    private static string NameOf(object? item) => item switch
    {
        DeviceTreeDevice device => device.Address,
        CatalogEvent catalogEvent => catalogEvent.Name,
        _ => string.Empty,
    };

    /// <summary>Where an event or a camera dropped at <paramref name="point"/> would land; null outside the grid or without room.</summary>
    internal static TileRect? PreviewAt(TileGrid grid, DashboardTab tab, bool camera, Point point)
    {
        if (!grid.Contains(point))
        {
            return null;
        }

        var (x, y) = grid.CellAt(point);
        return tab.PreviewAt(camera, x, y);
    }

    /// <summary>Drops an event (<see cref="CatalogEvent"/>) or a camera (<see cref="DeviceTreeDevice"/>) at a point of the grid.</summary>
    internal static bool DropOn(TileGrid grid, DashboardTab tab, object item, Point point)
    {
        if (!grid.Contains(point))
        {
            return false;
        }

        var (x, y) = grid.CellAt(point);
        return item switch
        {
            DeviceTreeDevice device => tab.DropCamera(device.Device, x, y),
            CatalogEvent catalogEvent => tab.DropEvent(catalogEvent, x, y),
            _ => false,
        };
    }

    private void EndDrag()
    {
        _dragging = false;
        DragGhost.IsVisible = false;
        ActiveGrid?.ShowPreview(null);
    }
}
