using System.ComponentModel;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Oadm.Client.LiveView;
using Oadm.Client.Tags;

namespace Oadm.Client.Devices;

public partial class DevicesView : UserControl
{
    private LiveViewViewModel? _liveView;
    private DevicesViewModel? _viewModel;

    // Device card : live view split while the panel is open (about 60 : 40), kept when the user drags the splitter.
    private GridLength _deviceColumn = new(3, GridUnitType.Star);
    private GridLength _liveColumn = new(2, GridUnitType.Star);

    public DevicesView()
    {
        InitializeComponent();
        DeviceGrid.CellPointerPressed += OnCellPointerPressed;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
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
            _viewModel = null;
        }

        if (DataContext is DevicesViewModel vm)
        {
            _viewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.TagGrouping.GroupsChanged += OnGroupsChanged;
            ApplyItemsSource();
            vm.Toolbar.AttachTo(ToolbarPanel, vm.ToolbarContext);
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
