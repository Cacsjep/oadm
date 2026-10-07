using Avalonia.Controls;

namespace Oadm.Client.Devices;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
        DeviceGrid.CellPointerPressed += OnCellPointerPressed;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DevicesViewModel vm)
        {
            DeviceGridLayoutBinder.Attach(DeviceGrid, vm.Columns);
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
