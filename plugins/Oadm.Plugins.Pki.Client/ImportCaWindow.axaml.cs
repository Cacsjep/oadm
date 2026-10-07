using Avalonia.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>PKI dialog window. View only: wired to its view model by <see cref="PkiDialogs.Attach"/>.</summary>
public partial class ImportCaWindow : Window
{
    public ImportCaWindow()
    {
        InitializeComponent();
    }
}
