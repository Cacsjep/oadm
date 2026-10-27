using Avalonia.Controls;

using Oadm.Theme.Shell;

namespace Oadm.Client.Shell;

public partial class MainWindow : Window
{
    private readonly List<PluginWindow> _pluginWindows = [];
    private HelpWindow? _helpWindow;

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
            _helpWindow?.Close();
        };
    }

    /// <summary>The plugin windows of this main window that are open (tests).</summary>
    internal IReadOnlyList<PluginWindow> PluginWindows => _pluginWindows;

    /// <summary>The Help window of this main window while it is open (tests).</summary>
    internal HelpWindow? CurrentHelpWindow => _helpWindow;

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
            _vm.MyRoleRequested -= OnMyRoleRequested;
            _vm.HelpWindowRequested -= OnHelpWindowRequested;
            _vm.CloseHelpRequested -= OnCloseHelpRequested;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.PluginWindowRequested += OnPluginWindowRequested;
            vm.ClosePluginWindowsRequested += OnClosePluginWindowsRequested;
            vm.MyRoleRequested += OnMyRoleRequested;
            vm.HelpWindowRequested += OnHelpWindowRequested;
            vm.CloseHelpRequested += OnCloseHelpRequested;
        }
    }

    private void OnPluginWindowRequested(object? sender, CorePluginPageViewModel page) => OpenPluginWindow(page);

    /// <summary>"Your role": a modal dialog over the main window.</summary>
    private void OnMyRoleRequested(object? sender, MyRoleViewModel role) => _ = new MyRoleWindow { DataContext = role }.ShowDialog(this);

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
        WindowFit.CenterOn(window, this); // the monitor of the main window, not always the main monitor
        window.Show();
    }

    /// <summary>Help: a window of its own (not modal), one at a time; Help again brings the open one to the front.</summary>
    private void OnHelpWindowRequested(object? sender, HelpWindowViewModel help)
    {
        if (_helpWindow is { } open && ReferenceEquals(open.DataContext, help))
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var shell = (MainWindowViewModel)sender!;
        var window = new HelpWindow { DataContext = help };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_helpWindow, window))
            {
                _helpWindow = null;
            }

            shell.HelpClosed(help); // the docs server stops
        };
        _helpWindow = window;
        WindowFit.CenterOn(window, this);
        window.Show();
    }

    private void OnCloseHelpRequested(object? sender, HelpWindowViewModel help)
    {
        if (_helpWindow is { } open && ReferenceEquals(open.DataContext, help))
        {
            open.Close();
        }
    }

    private void ClosePluginWindows(string? pluginId)
    {
        foreach (var window in _pluginWindows.Where(w => pluginId is null || (w.DataContext as CorePluginPageViewModel)?.PluginId == pluginId).ToList())
        {
            window.Close();
        }
    }
}
