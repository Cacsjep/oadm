using Avalonia.Controls;

namespace Oadm.Plugins.EdgeStorage.Client;

/// <summary>Edge storage dialog window. View only: wired to its view model by <see cref="EdgeStorageView"/>.</summary>
public partial class RetentionWindow : Window
{
    public RetentionWindow()
    {
        InitializeComponent();
    }
}
