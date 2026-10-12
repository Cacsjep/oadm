using System.Collections.Specialized;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.WebView.Client;

/// <summary>
/// The Web View page. The view owns the native browsers: one per tab, created when the tab opens and kept while it is
/// hidden (switching tabs keeps the page as it was), disposed when the tab closes. They follow <see cref="WebTab.Address"/>.
/// </summary>
public sealed partial class WebViewPageView : UserControl
{
    private readonly Dictionary<WebTab, Control> _pages = [];
    private WebViewPageViewModel? _vm;

    public WebViewPageView()
    {
        InitializeComponent();
        Tree.DeviceInvoked += (_, row) => _vm?.OpenDeviceCommand.Execute(row);
        Tree.RowsChanged += (_, _) => SelectActiveRow();
        CustomPathBox.KeyDown += OnCustomPathKeyDown;
        CustomPathBox.LostFocus += (_, _) => _vm?.ApplyCustomPathCommand.Execute(null);
    }

    /// <summary>Creates the browser of a tab; the tests replace it (the native browser needs a real window).</summary>
    internal static Func<WebTab, Control> BrowserFactory { get; set; } = tab => new WebTabBrowser(tab);

    /// <summary>A browser's history changed: Back follows it.</summary>
    internal void OnHistoryChanged() => _vm?.RefreshGoBack();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.GoBackRequested -= OnGoBackRequested;
        }

        RemoveAllPages();
        _vm = DataContext as WebViewPageViewModel;
        if (_vm is not null)
        {
            _vm.Tabs.CollectionChanged += OnTabsChanged;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.GoBackRequested += OnGoBackRequested;
            foreach (var tab in _vm.Tabs)
            {
                AddPage(tab);
            }
        }
    }


    private void OnGoBackRequested(object? sender, WebTab tab)
    {
        if (_pages.TryGetValue(tab, out var page) && page is WebTabBrowser browser)
        {
            browser.GoBack();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebViewPageViewModel.ActiveTab))
        {
            SelectActiveRow();
        }
    }

    /// <summary>The tree shows the active tab's row selected.</summary>
    private void SelectActiveRow()
    {
        if (_vm?.ActiveTab is not { } tab)
        {
            Tree.SelectedRow = null;
            return;
        }

        Tree.SelectedRow = _vm.TreeRows.OfType<DeviceTreeDevice>().FirstOrDefault(r => r.Device.Id == tab.Device.Id && r.GroupKey == tab.GroupKey);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RemoveAllPages();
            return;
        }

        foreach (var tab in e.OldItems?.OfType<WebTab>() ?? [])
        {
            RemovePage(tab);
        }

        foreach (var tab in e.NewItems?.OfType<WebTab>() ?? [])
        {
            AddPage(tab);
        }
    }

    private void AddPage(WebTab tab)
    {
        if (_vm is null || !_vm.Runtime.IsAvailable || _pages.ContainsKey(tab))
        {
            return;
        }

        var page = BrowserFactory(tab);
        if (page is WebTabBrowser browser)
        {
            browser.HistoryChanged += OnHistoryChanged;
        }

        _pages[tab] = page;
        Pages.Children.Add(page);
    }

    private void RemovePage(WebTab tab)
    {
        if (_pages.Remove(tab, out var page))
        {
            Pages.Children.Remove(page);
            if (page is WebTabBrowser browser)
            {
                browser.HistoryChanged -= OnHistoryChanged;
            }

            (page as IDisposable)?.Dispose();
        }
    }

    private void RemoveAllPages()
    {
        foreach (var tab in _pages.Keys.ToList())
        {
            RemovePage(tab);
        }
    }

    private void OnCustomPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.ApplyCustomPathCommand.Execute(null);
            e.Handled = true;
        }
    }
}

/// <summary>
/// The page of one tab: the native browser, or the reason the device cannot be opened, or "Opening" while the relay starts.
/// </summary>
internal sealed class WebTabBrowser : Panel, IDisposable
{
    private readonly WebTab _tab;
    private readonly NativeWebView _browser = new();
    private Uri? _lastPage;
    private bool _shown;
    private DateTime _lastRestart = DateTime.MinValue;
    private bool _disposed;

    /// <summary>The message the page sends when its history changed (<see cref="HistoryHook"/>).</summary>
    private const string HistoryMessage = "oadm:history";

    /// <summary>
    /// The web interfaces are single-page apps: moving around adds history entries without a page load, and the native
    /// browser raises no event the control passes on. After every page load this hook in the page reports pushState,
    /// replaceState, back / forward and hash changes through the control's message channel (invokeCSharpAction on every
    /// platform), and Back is checked then. Installed once per document.
    /// </summary>
    private const string HistoryHook =
        "(function(){if(window.__oadmHistory){return;}window.__oadmHistory=1;"
        + "var post=function(){try{invokeCSharpAction('" + HistoryMessage + "');}catch(e){}};"
        + "['pushState','replaceState'].forEach(function(n){var o=history[n];history[n]=function(){var r=o.apply(this,arguments);post();return r;};});"
        + "window.addEventListener('popstate',post);window.addEventListener('hashchange',post);})();";
    private readonly StatusChip _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxLines = 4 };

    public WebTabBrowser(WebTab tab)
    {
        _tab = tab;
        DataContext = tab;
        // Developer tools (F12, right click > Inspect): technicians and app developers see what a page does.
        _browser.EnvironmentRequested += (_, e) => e.EnableDevTools = true;
        _browser.NewWindowRequested += OnNewWindowRequested;
        _browser.NavigationCompleted += OnNavigationCompleted;
        Children.Add(_browser);
        Children.Add(_status);
        _tab.PropertyChanged += OnTabChanged;
        _tab.NavigateRequested += OnNavigateRequested;
        _browser.WebMessageReceived += OnWebMessageReceived;
        Apply();
        if (_tab.Address is { } address)
        {
            _browser.Navigate(address);
        }
    }

    /// <summary>The page changed: Back may have become possible.</summary>
    public event Action? HistoryChanged;

    /// <summary>The browser's Back, within the device's pages (the first page is the relay's start address).</summary>
    public void GoBack()
    {
        if (_browser.CanGoBack)
        {
            _browser.GoBack();
        }
    }

    /// <summary>The page left the screen and came back (another rail page in between).</summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_shown && _browser.Source is null && (_lastPage is { } last ? _tab.StartAddressFor(last) : _tab.Address) is { } page)
        {
            // The native browser was created again: open the page it showed, with a fresh session cookie.
            _browser.Navigate(page);
        }

        _shown = true;
    }

    private void UpdateHistory()
    {
        if (!_tab.IsActive)
        {
            return;
        }

        var canGoBack = _browser.CanGoBack;
        if (canGoBack != _tab.CanGoBack)
        {
            _tab.CanGoBack = canGoBack;
            HistoryChanged?.Invoke();
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (e.Body == HistoryMessage)
        {
            UpdateHistory();
        }
    }

    private void OnNavigateRequested(object? sender, Uri page)
    {
        if (!_disposed)
        {
            _browser.Navigate(page);
        }
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        UpdateHistory();
        if (e.Request is not { } page)
        {
            return;
        }

        try
        {
            // The relay refused the page because the browser lost its session cookie: open it again through the start
            // address (at most once every 10 s, so a relay that keeps refusing cannot loop).
            var title = await _browser.InvokeScript("document.title").ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (title?.Contains(DeviceWebPages.SessionLostTitle, StringComparison.Ordinal) == true)
            {
                if (DateTime.UtcNow - _lastRestart > TimeSpan.FromSeconds(10) && _tab.StartAddressFor(page) is { } again)
                {
                    _lastRestart = DateTime.UtcNow;
                    _browser.Navigate(again);
                }

                return;
            }

            await _browser.InvokeScript(HistoryHook).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A browser callback must never reach the crash handler (the tab may close meanwhile).
        catch (Exception)
#pragma warning restore CA1031
        {
            // The browser closed meanwhile, or cannot run scripts: nothing to repair, Back follows page loads only.
        }

        if (e.IsSuccess)
        {
            _lastPage = page;
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebTab.Address) && _tab.Address is { } address)
        {
            _browser.Navigate(address);
        }

        if (e.PropertyName == nameof(WebTab.IsActive))
        {
            UpdateHistory();
        }

        Apply();
    }

    private void Apply()
    {
        IsVisible = _tab.IsActive;
        _browser.IsVisible = _tab.Address is not null && !_tab.HasError;
        _status.IsVisible = _tab.HasError || _tab.IsLoading;
        _status.IsError = _tab.HasError;
        _status.IsAccent = !_tab.HasError && _tab.IsLoading;
        _status.Text = _tab.Error ?? (_tab.IsLoading ? "Opening" : string.Empty);
    }

    /// <summary>Links that open a new window (target="_blank") open in the same tab: the device's page stays in OADM.</summary>
    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { } uri && _tab.Address is { } current && uri.Authority == current.Authority)
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
        _browser.WebMessageReceived -= OnWebMessageReceived;
        _tab.PropertyChanged -= OnTabChanged;
        _tab.NavigateRequested -= OnNavigateRequested;
        _browser.NewWindowRequested -= OnNewWindowRequested;
        _browser.NavigationCompleted -= OnNavigationCompleted;
        Children.Remove(_browser);
        (_browser as IDisposable)?.Dispose(); // the native browser and its processes go with the tab
    }
}
