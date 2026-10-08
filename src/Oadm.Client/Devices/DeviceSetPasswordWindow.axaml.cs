using Avalonia.Controls;

namespace Oadm.Client.Devices;

/// <summary>The "Set password" dialog window (see <see cref="DeviceSetPasswordViewModel"/>). View only: wiring to the view model.</summary>
public partial class DeviceSetPasswordWindow : Window
{
    public DeviceSetPasswordWindow()
    {
        InitializeComponent();
    }

    public void Attach(DeviceSetPasswordViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
    }

    /// <summary>Focuses the password, then reads the devices' passphrase policies for the hint.</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PasswordInput.Focus();
        if (DataContext is DeviceSetPasswordViewModel vm)
        {
            await vm.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }
}
