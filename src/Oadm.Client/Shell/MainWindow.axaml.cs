using Avalonia.Controls;

namespace Oadm.Client.Shell;

public partial class MainWindow : Window
{
    private readonly List<PluginWindow> _pluginWindows = [];

    /// <summary>
    /// The view model this window listens to. The view model lives for the whole app, a new main window comes with every
    /// login: the handlers are removed again when the DataContext changes or the window closes.
    /// </summary>
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        Closed += (_, _) =>
        {
            Attach(null);
            ClosePluginWindows(null);
        };
    }

    /// <summary>The plugin windows of this main window that are open (tests).</summary>
    internal IReadOnlyList<PluginWindow> PluginWindows => _pluginWindows;

    private void Attach(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.PluginWindowRequested -= OnPluginWindowRequested;
            _vm.ClosePluginWindowsRequested -= OnClosePluginWindowsRequested;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.PluginWindowRequested += OnPluginWindowRequested;
            vm.ClosePluginWindowsRequested += OnClosePluginWindowsRequested;
        }
    }

    private void OnPluginWindowRequested(object? sender, CorePluginPageViewModel page) => OpenPluginWindow(page);

    private void OnClosePluginWindowsRequested(object? sender, string? pluginId) => ClosePluginWindows(pluginId);

    /// <summary>A plugin page in a window of its own (not modal: it can stay open next to the main window).</summary>
    private void OpenPluginWindow(CorePluginPageViewModel page)
    {
        var window = new PluginWindow { DataContext = page };
        window.Closed += (_, _) =>
        {
            _pluginWindows.Remove(window);
            // The window's copy of the page lets go of its plugin context (device changes) and view model.
            page.Dispose();
        };
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
