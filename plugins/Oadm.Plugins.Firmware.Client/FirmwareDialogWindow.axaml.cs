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
    }
}
