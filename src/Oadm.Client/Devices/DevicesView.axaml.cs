using System.ComponentModel;

using Avalonia.Controls;

using Oadm.Client.Tasks;

namespace Oadm.Client.Devices;

public partial class DevicesView : UserControl
{
    private const int DevicesRow = 1;
    private const int TasksRow = 3;

    private TasksViewModel? _tasks;

    public DevicesView()
    {
        InitializeComponent();
        DeviceGrid.CellPointerPressed += OnCellPointerPressed;
        PaneSplitter.DragCompleted += (_, _) => _tasks?.CommitPaneHeight(LayoutRoot.RowDefinitions[TasksRow].ActualHeight);
        if (this.TryFindResource("Oadm.DevicesCardMinHeight", out object? min) && min is double minHeight)
        {
            LayoutRoot.RowDefinitions[DevicesRow].MinHeight = minHeight;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tasks is not null)
        {
            _tasks.PropertyChanged -= OnTasksPropertyChanged;
            _tasks = null;
        }

        if (DataContext is DevicesViewModel vm)
        {
            DeviceGridLayoutBinder.Attach(DeviceGrid, vm.Columns);
            _tasks = vm.Tasks;
            _tasks.PropertyChanged += OnTasksPropertyChanged;
            ApplyPaneRow();
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
