using Avalonia.Controls;

namespace Oadm.Client.MyAxis;

/// <summary>
/// The MyAxis sign-in window (see <see cref="MyAxisSignInViewModel"/>). The view owns the native browser: created when
/// the window opens unless sign-in is not available, disposed when it closes.
/// </summary>
public partial class MyAxisSignInWindow : Window
{
    private MyAxisSignInViewModel? _vm;
    private Control? _browser;

    public MyAxisSignInWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MyAxisSignInViewModel);
        Closed += (_, _) => Attach(null);
    }

    /// <summary>Creates the browser on Axis's login page; the tests replace it (the native browser needs a real window).</summary>
    internal static Func<MyAxisSignInViewModel, Control> BrowserFactory { get; set; } = vm => new MyAxisBrowser(vm);

    /// <summary>The browser while the login page is shown (tests).</summary>
    internal Control? Browser => _browser;

    private void Attach(MyAxisSignInViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
        }

        RemoveBrowser();
        _vm = vm;
        if (vm is null)
        {
            return;
        }

        vm.CloseRequested += OnCloseRequested;
        if (vm.IsNotAvailable)
        {
            return;
        }

        try
        {
            _browser = BrowserFactory(vm);
            BrowserHost.Children.Add(_browser);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeLoadException or InvalidOperationException or PlatformNotSupportedException)
        {
            vm.SetNotAvailable();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void RemoveBrowser()
    {
        if (_browser is { } browser)
        {
            _browser = null;
            BrowserHost.Children.Remove(browser);
            (browser as IDisposable)?.Dispose(); // the native browser, its processes and data go with the window
        }
    }
}
