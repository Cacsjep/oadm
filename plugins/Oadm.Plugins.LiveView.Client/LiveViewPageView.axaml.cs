using System.Collections.Specialized;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LiveView.Client;

/// <summary>
/// The Live View page view: one <see cref="ViewGrid"/> per open view (only the active one shown, so the hidden views'
/// videos rest), the list of views, and dragging a camera from the tree into the edited view.
/// </summary>
public sealed partial class LiveViewPageView : UserControl
{
    /// <summary>A press must move this far before it is a drag (a shorter one is a click).</summary>
    private const double DragThreshold = 6;

    private readonly Dictionary<ViewTab, ViewGrid> _pages = [];
    private LiveViewPageViewModel? _vm;
    private DeviceTreeDevice? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private bool _dragEnded; // the release of a drag is no click on the camera

    public LiveViewPageView()
    {
        InitializeComponent();
        Tree.DeviceInvoked += (_, row) =>
        {
            if (!_dragEnded)
            {
                _vm?.AddCamera(row.Device);
            }

            _dragEnded = false;
        };
        ViewList.AddHandler(TappedEvent, OnViewTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        ViewList.KeyDown += OnViewListKeyDown;
        CollapsedViewList.AddHandler(TappedEvent, OnViewTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerPressedEvent, OnTreePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
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
        }

        RemoveAllPages();
        _vm = DataContext as LiveViewPageViewModel;
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged += OnTabsChanged;
            _vm.PropertyChanged += OnViewModelChanged;
            foreach (var tab in _vm.Tabs)
            {
                AddPage(tab);
            }

            ShowActive();
        }
    }

    /// <summary>The grid of the active view (tests).</summary>
    internal ViewGrid? ActiveGrid => _vm?.ActiveTab is { } tab ? _pages.GetValueOrDefault(tab) : null;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveViewPageViewModel.ActiveTab))
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

        foreach (var tab in e.OldItems?.OfType<ViewTab>() ?? [])
        {
            RemovePage(tab);
        }

        foreach (var tab in e.NewItems?.OfType<ViewTab>() ?? [])
        {
            AddPage(tab);
        }

        ShowActive();
    }

    private void AddPage(ViewTab tab)
    {
        if (_pages.ContainsKey(tab))
        {
            return;
        }

        var grid = new ViewGrid { Tab = tab, IsVisible = false };
        _pages[tab] = grid;
        Pages.Children.Add(grid);
    }

    private void RemovePage(ViewTab tab)
    {
        if (_pages.Remove(tab, out var grid))
        {
            Pages.Children.Remove(grid);
            grid.Tab = null;
        }
    }

    private void RemoveAllPages()
    {
        foreach (var tab in _pages.Keys.ToList())
        {
            RemovePage(tab);
        }
    }

    /// <summary>Only the active view is in the visual tree's visible part: the others' videos stop after a moment.</summary>
    private void ShowActive()
    {
        var active = _vm?.ActiveTab;
        foreach (var (tab, grid) in _pages)
        {
            grid.IsVisible = tab == active;
        }

        ViewList.SelectedItem = active?.Id is { } id ? _vm!.Views.FirstOrDefault(v => v.Id == id) : null;
        CollapsedViewList.SelectedItem = ViewList.SelectedItem;
    }

    private void OnViewTapped(object? sender, TappedEventArgs e)
    {
        // The row's Edit and Delete buttons do their own thing.
        if (e.Source is Control source && !IsInButton(source) && source.DataContext is ViewEntry entry)
        {
            _vm?.OpenViewCommand.Execute(entry);
        }
    }

    private static bool IsInButton(Visual? visual)
    {
        for (var v = visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
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

    private void OnViewListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && ViewList.SelectedItem is ViewEntry entry)
        {
            _vm?.OpenViewCommand.Execute(entry);
            e.Handled = true;
        }
    }

    // Dragging a camera from the tree: a press on a camera row, a move beyond the threshold starts the drag (only while a
    // view is edited), the release over the grid drops it on that cell.
    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        _dragEnded = false;
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? (e.Source as Control)?.DataContext as DeviceTreeDevice : null;
        _pressPoint = e.GetPosition(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || _vm?.ActiveTab is not { IsEditing: true })
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
            DragGhostText.Text = _pressed.Address;
            DragGhost.IsVisible = true;
            e.Pointer.Capture(this);
        }

        Canvas.SetLeft(DragGhost, point.X + 12);
        Canvas.SetTop(DragGhost, point.Y + 8);
        if (ActiveGrid is { } grid && _vm.ActiveTab is { } tab)
        {
            grid.ShowPreview(PreviewAt(grid, tab, _pressed, e.GetPosition(grid)), _pressed.Address); // where it would land
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

    /// <summary>Where a camera dropped at <paramref name="point"/> would land; null outside the grid or without room.</summary>
    internal static ViewTile? PreviewAt(ViewGrid grid, ViewTab tab, DeviceTreeDevice camera, Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > grid.Bounds.Width || point.Y > grid.Bounds.Height)
        {
            return null;
        }

        var (x, y) = grid.CellAt(point);
        return ViewLayout.Place(tab.TileList(), camera.Device.Id, 0, x, y);
    }

    /// <summary>Drops a camera at a point of the grid; outside the grid nothing happens.</summary>
    internal static bool DropOn(ViewGrid grid, ViewTab tab, DeviceTreeDevice camera, Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > grid.Bounds.Width || point.Y > grid.Bounds.Height)
        {
            return false;
        }

        var (x, y) = grid.CellAt(point);
        return tab.Drop(camera.Device, x, y);
    }

    private void EndDrag()
    {
        _dragging = false;
        DragGhost.IsVisible = false;
        ActiveGrid?.ShowPreview(null);
    }
}
