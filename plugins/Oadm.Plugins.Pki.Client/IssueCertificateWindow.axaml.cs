using Avalonia.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>Enable HTTPS / Enable IEEE 802.1X. View only: wired to its view model by <see cref="PkiDialogs.Attach"/>.</summary>
public partial class IssueCertificateWindow : Window
{
    public IssueCertificateWindow()
    {
        InitializeComponent();
    }
}
