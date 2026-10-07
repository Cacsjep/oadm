using Avalonia.Controls;

namespace Oadm.Client.Discovery;

public partial class AddDevicesWizardWindow : Window
{
    public AddDevicesWizardWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AddDevicesWizardViewModel vm)
        {
            vm.CloseRequested += (_, added) => Close(added);
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is AddDevicesWizardViewModel vm)
        {
            await vm.OpenAsync();
        }
    }
}
