using System.ComponentModel;

using Avalonia.Controls;

namespace Oadm.Client.Shell;

/// <summary>
/// The Help window (see <see cref="HelpWindowViewModel"/>). The view owns the native browser: created once the docs server
/// runs, disposed when the window closes.
/// </summary>
public partial class HelpWindow : Window
{
    private HelpWindowViewModel? _vm;
    private Control? _browser;

    public HelpWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as HelpWindowViewModel);
        Closed += (_, _) => Attach(null);
    }

    /// <summary>Creates the browser on the docs; the tests replace it (the native browser needs a real window).</summary>
    internal static Func<HelpWindowViewModel, Uri, Control> BrowserFactory { get; set; } = (vm, start) => new HelpBrowser(vm, start);

    /// <summary>The browser while the docs are shown (tests).</summary>
    internal Control? Browser => _browser;

    private void Attach(HelpWindowViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        RemoveBrowser();
        _vm = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnViewModelChanged;
            ShowBrowser();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HelpWindowViewModel.StartAddress))
        {
            ShowBrowser();
        }
    }

    private void ShowBrowser()
    {
        if (_vm?.StartAddress is not { } start)
        {
            RemoveBrowser();
            return;
        }

        if (_browser is null)
        {
            _browser = BrowserFactory(_vm, start);
            BrowserHost.Children.Add(_browser);
        }
    }

    private void RemoveBrowser()
    {
        if (_browser is { } browser)
        {
            _browser = null;
            BrowserHost.Children.Remove(browser);
            (browser as IDisposable)?.Dispose(); // the native browser and its processes go with the window
        }
    }
}

/// <summary>
/// The embedded browser of the Help window on the docs server. Links that leave the docs (another host, mail) open in the
/// system browser; links that ask for a new window open in the same one.
/// </summary>
internal sealed class HelpBrowser : Panel, IDisposable
{
    private readonly HelpWindowViewModel _vm;
    private readonly NativeWebView _browser = new();
    private bool _disposed;

    public HelpBrowser(HelpWindowViewModel vm, Uri start)
    {
        _vm = vm;
        _browser.EnvironmentRequested += (_, e) =>
        {
            // The same environment as the Web View page (one user data folder may only have one set of options).
            e.EnableDevTools = true;
            // WebView2 keeps its data next to the exe by default, which users cannot write in Program Files (access
            // denied). Keep it in the user's own OADM folder instead.
            if (e is Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.UserDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oadm", "WebView2");
            }
        };
        _browser.NavigationStarted += OnNavigationStarted;
        _browser.NewWindowRequested += OnNewWindowRequested;
        Children.Add(_browser);
        _browser.Navigate(start);
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (!_disposed && _vm.LeavesDocs(e.Request))
        {
            e.Cancel = true;
            _ = _vm.OpenOutsideAsync(e.Request!);
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_disposed || e.Request is not { } uri)
        {
            return;
        }

        if (_vm.LeavesDocs(uri))
        {
            _ = _vm.OpenOutsideAsync(uri);
        }
        else
        {
            _browser.Navigate(uri);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _browser.NavigationStarted -= OnNavigationStarted;
        _browser.NewWindowRequested -= OnNewWindowRequested;
        Children.Remove(_browser);
        (_browser as IDisposable)?.Dispose();
    }
}
