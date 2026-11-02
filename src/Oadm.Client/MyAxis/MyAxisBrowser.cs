using System.Net;

using Avalonia.Controls;
using Avalonia.Platform;

namespace Oadm.Client.MyAxis;

/// <summary>
/// The embedded browser of the MyAxis sign-in window on Axis's login page. It keeps nothing on disk (WebView2 InPrivate,
/// WKWebView a non-persistent store, WebKitGTK ephemeral, WPE a temporary folder deleted with the window) and reads its
/// cookies through the package's cookie manager, on WebKitGTK through <see cref="WebKitGtkCookies"/>.
/// </summary>
internal sealed class MyAxisBrowser : Panel, ICookieSource, IDisposable
{
    /// <summary>A cookie read that takes longer counts as no cookie.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly MyAxisSignInViewModel _vm;
    private readonly NativeWebView _browser = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _wpeFolder = Path.Combine(Path.GetTempPath(), "oadm-myaxis-" + Guid.NewGuid().ToString("N"));
    private bool _disposed;

    public MyAxisBrowser(MyAxisSignInViewModel vm)
    {
        _vm = vm;
        _browser.EnvironmentRequested += (_, e) =>
        {
            e.EnableDevTools = false;
            switch (e)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs windows:
                    // The folder of Help and Web View (one folder, one set of environment options); InPrivate keeps
                    // this window's cookies and data out of it.
                    windows.UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oadm", "WebView2");
                    windows.IsInPrivateModeEnabled = true;
                    break;
                case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                    apple.NonPersistentDataStore = true;
                    break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.EphemeralDataManager = true;
                    break;
                case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                    // The package has no ephemeral session for WPE: a folder of this window only, deleted when it closes.
                    wpe.DataDirectory = Path.Combine(_wpeFolder, "data");
                    wpe.CacheDirectory = Path.Combine(_wpeFolder, "cache");
                    break;
            }
        };
        _browser.NavigationStarted += OnNavigationStarted;
        _browser.NavigationCompleted += OnNavigationCompleted;
        _browser.NewWindowRequested += OnNewWindowRequested;
        _vm.NavigateRequested += OnNavigateRequested;
        Children.Add(_browser);
        _browser.Navigate(MyAxisSignInViewModel.StartAddress);
    }

    /// <summary>Before the page shows: the page after the login is covered at once (the download listing never shows).</summary>
    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (!_disposed)
        {
            _vm.NavigationStarting(e.Request);
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!_disposed)
        {
            _ = _vm.PageLoadedAsync(e.Request ?? _browser.Source, this, _cts.Token, e.IsSuccess);
        }
    }

    /// <summary>Links that ask for a new window open in the same one.</summary>
    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_disposed && e.Request is { } uri)
        {
            _browser.Navigate(uri);
        }
    }

    private void OnNavigateRequested(object? sender, Uri page)
    {
        if (!_disposed)
        {
            _browser.Navigate(page);
        }
    }

    public async Task<IReadOnlyList<Cookie>?> ReadAsync(Uri address, CancellationToken ct)
    {
        if (_disposed)
        {
            return [];
        }

        try
        {
            if (_browser.TryGetCookieManager() is { } manager)
            {
                return await manager.GetCookiesAsync().WaitAsync(ReadTimeout, TimeProvider.System, ct).ConfigureAwait(true);
            }

            if (OperatingSystem.IsLinux() && _browser.TryGetPlatformHandle() is IGtkWebViewPlatformHandle { WebKitWebView: var view } && view != IntPtr.Zero)
            {
                return await WebKitGtkCookies.ReadAsync(view, address, ReadTimeout, TimeProvider.System, ct).ConfigureAwait(true);
            }

            return null; // no way to the cookies
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // any browser problem counts as "no cookie"
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _vm.NavigateRequested -= OnNavigateRequested;
        _browser.NavigationStarted -= OnNavigationStarted;
        _browser.NavigationCompleted -= OnNavigationCompleted;
        _browser.NewWindowRequested -= OnNewWindowRequested;
        Children.Remove(_browser);
        (_browser as IDisposable)?.Dispose();
        try
        {
            if (Directory.Exists(_wpeFolder))
            {
                Directory.Delete(_wpeFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use by the closing browser process; the system cleans its temp folder
        }
    }
}
