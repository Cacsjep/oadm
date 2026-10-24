using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>The certificate choice of Revoke. View only: wiring to the view model, the certificate window.</summary>
public partial class RevokePickerWindow : Window, IIssuedCertificateListDialogs
{
    public RevokePickerWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="viewModel"/>; <paramref name="closed"/> gets the chosen rows (null = Cancel).</summary>
    public void Attach(RevokePickerViewModel viewModel, Action<IReadOnlyList<IssuedCertificateRow>?> closed)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(closed);
        DataContext = viewModel;
        viewModel.Dialogs = this;
        viewModel.CloseRequested += (_, rows) =>
        {
            closed(rows);
            Close();
        };
    }

    public Task ShowCertificateAsync(CertificateDetails certificate) => CertificateWindow.ShowAsync(certificate, this);
}
