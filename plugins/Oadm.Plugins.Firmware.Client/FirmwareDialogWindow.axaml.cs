using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.Firmware.Client;

public partial class FirmwareDialogWindow : Window
{
    public FirmwareDialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FirmwareDialogViewModel vm)
            {
                vm.CloseRequested += (_, payload) => Close(payload);
            }
        };

        // The grid virtualizes its rows: the status of a device is read only when its row is shown.
        this.FindControl<DataGrid>("DevicesGrid")!.LoadingRow += (_, e) =>
        {
            if (DataContext is FirmwareDialogViewModel vm && e.Row.DataContext is FirmwareDeviceRow row)
            {
                _ = vm.EnsureStatusAsync(row);
            }
        };
    }
}
