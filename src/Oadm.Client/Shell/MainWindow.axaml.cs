using Avalonia.Controls;

namespace Oadm.Client.Shell;

public partial class MainWindow : Window
{
    private readonly List<PluginWindow> _pluginWindows = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PluginWindowRequested += (_, page) => OpenPluginWindow(page);
                vm.ClosePluginWindowsRequested += (_, pluginId) => ClosePluginWindows(pluginId);
            }
        };
        Closed += (_, _) => ClosePluginWindows(null);
    }

    /// <summary>A plugin page in a window of its own (not modal: it can stay open next to the main window).</summary>
    private void OpenPluginWindow(CorePluginPageViewModel page)
    {
        var window = new PluginWindow { DataContext = page };
        window.Closed += (_, _) => _pluginWindows.Remove(window);
        _pluginWindows.Add(window);
        window.Show();
    }

    private void ClosePluginWindows(string? pluginId)
    {
        foreach (var window in _pluginWindows.Where(w => pluginId is null || (w.DataContext as CorePluginPageViewModel)?.PluginId == pluginId).ToList())
        {
            window.Close();
        }
    }
}
