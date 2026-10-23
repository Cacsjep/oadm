using Avalonia.Controls;

namespace Oadm.Plugins.Est.Client;

/// <summary>The certificate an enrollment issued. View only: closes when its view model asks.</summary>
public partial class EstCertificateWindow : Window
{
    public EstCertificateWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="certificate"/> over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(EstCertificateViewModel certificate, Window owner)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var window = new EstCertificateWindow { DataContext = certificate };
        certificate.CloseRequested += (_, _) => window.Close();
        return window.ShowDialog(owner);
    }
}
