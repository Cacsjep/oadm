using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Oadm.Client.LiveView;
using Oadm.Client.Tasks;

namespace Oadm.Client.Devices;

public partial class DevicesView : UserControl
{
    private const int DevicesRow = 1;
    private const int TasksRow = 3;

    private TasksViewModel? _tasks;
    private LiveViewViewModel? _liveView;

    // Device card : live view split while the panel is open (about 60 : 40), kept when the user drags the splitter.
    private GridLength _deviceColumn = new(3, GridUnitType.Star);
    private GridLength _liveColumn = new(2, GridUnitType.Star);

    public DevicesView()
    {
        InitializeComponent();
        DeviceGrid.CellPointerPressed += OnCellPointerPressed;
        PaneSplitter.DragCompleted += (_, _) => _tasks?.CommitPaneHeight(LayoutRoot.RowDefinitions[TasksRow].ActualHeight);
        if (this.TryFindResource("Oadm.DevicesCardMinHeight", out object? min) && min is double minHeight)
        {
            LayoutRoot.RowDefinitions[DevicesRow].MinHeight = minHeight;
        }

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tasks is not null)
        {
            _tasks.PropertyChanged -= OnTasksPropertyChanged;
            _tasks = null;
        }

        if (_liveView is not null)
        {
            _liveView.PropertyChanged -= OnLiveViewPropertyChanged;
            _liveView = null;
        }

        if (DataContext is DevicesViewModel vm)
        {
            DeviceGridLayoutBinder.Attach(DeviceGrid, vm.Columns);
            _tasks = vm.Tasks;
            _tasks.PropertyChanged += OnTasksPropertyChanged;
            ApplyPaneRow();
            _liveView = vm.LiveView;
            _liveView.PropertyChanged += OnLiveViewPropertyChanged;
            ApplyLiveViewColumns();
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

    private void OnTasksPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TasksViewModel.IsExpanded) or nameof(TasksViewModel.PaneHeight))
        {
            ApplyPaneRow();
        }
    }

    /// <summary>Expanded: the tasks row has the persisted pixel height the splitter changes. Collapsed: header only.</summary>
    private void ApplyPaneRow()
    {
        if (_tasks is null)
        {
            return;
        }

        RowDefinition row = LayoutRoot.RowDefinitions[TasksRow];
        if (_tasks.IsExpanded)
        {
            row.MinHeight = TasksViewModel.MinPaneHeight;
            row.Height = new GridLength(_tasks.PaneHeight, GridUnitType.Pixel);
        }
        else
        {
            row.MinHeight = 0;
            row.Height = GridLength.Auto;
        }
    }

    /// <summary>Right-click on an unselected row selects it first, like ADM and Explorer.</summary>
    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.PointerPressedEventArgs.GetCurrentPoint(DeviceGrid).Properties.IsRightButtonPressed
            && e.Row.DataContext is DeviceRowViewModel row
            && !DeviceGrid.SelectedItems.Contains(row))
        {
            DeviceGrid.SelectedItem = row;
        }
    }
}
