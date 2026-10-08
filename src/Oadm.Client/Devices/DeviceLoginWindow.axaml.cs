using Avalonia.Controls;

namespace Oadm.Client.Devices;

/// <summary>The "Log in" dialog window (see <see cref="DeviceLoginViewModel"/>). View only: wiring to the view model.</summary>
public partial class DeviceLoginWindow : Window
{
    public DeviceLoginWindow()
    {
        InitializeComponent();
    }

    public void Attach(DeviceLoginViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
    }

    /// <summary>Prefills the stored user name, then focuses the password (the user name is usually right).</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PasswordInput.Focus();
        if (DataContext is DeviceLoginViewModel vm)
        {
            await vm.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }
}
