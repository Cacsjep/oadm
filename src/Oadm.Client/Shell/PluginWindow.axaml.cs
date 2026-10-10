using Avalonia.Controls;

namespace Oadm.Client.Shell;

/// <summary>A plugin page in a window of its own (see <see cref="MainWindowViewModel.CreatePluginWindowPage"/>).</summary>
public partial class PluginWindow : Window
{
    public PluginWindow()
    {
        InitializeComponent();
    }
}
