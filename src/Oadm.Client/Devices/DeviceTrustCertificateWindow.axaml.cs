using Avalonia.Controls;

namespace Oadm.Client.Devices;

/// <summary>The "Trust new certificate" dialog window (see <see cref="DeviceTrustCertificateViewModel"/>). View only: wiring.</summary>
public partial class DeviceTrustCertificateWindow : Window
{
    public DeviceTrustCertificateWindow()
    {
        InitializeComponent();
    }

    public void Attach(DeviceTrustCertificateViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
        Opened += (_, _) => _ = viewModel.LoadAsync(CancellationToken.None);
    }
}
