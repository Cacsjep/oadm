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
        Tree.AddHandler(TappedEvent, OnTreeTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.KeyDown += OnTreeKeyDown;
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
            Tree.SelectedItem = null;
            return;
        }

        Tree.SelectedItem = _vm.TreeRows.OfType<WebTreeDevice>().FirstOrDefault(r => r.Device.Id == tab.Device.Id && r.GroupKey == tab.GroupKey);
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

    private void OnTreeTapped(object? sender, TappedEventArgs e) => OpenRow((e.Source as Control)?.DataContext as WebTreeRow);

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            OpenRow(Tree.SelectedItem as WebTreeRow);
            e.Handled = true;
        }
    }

    private void OpenRow(WebTreeRow? row)
    {
        switch (row)
        {
            case WebTreeGroup group:
                _vm?.ToggleGroupCommand.Execute(group);
                SelectActiveRow();
                break;
            case WebTreeDevice device:
                _vm?.OpenDeviceCommand.Execute(device);
                break;
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

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WebTab tab && _vm is not null)
        {
            _vm.ActiveTab = tab;
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

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _tab.CanGoBack = _browser.CanGoBack;
        HistoryChanged?.Invoke();
        if (e.Request is not { } page)
        {
            return;
        }

        try
        {
            // The relay refused the page because the browser lost its session cookie: open it again through the start
            // address (at most once every 10 s, so a relay that keeps refusing cannot loop).
            var title = await _browser.InvokeScript("document.title").ConfigureAwait(true);
            if (title?.Contains(DeviceWebPages.SessionLostTitle, StringComparison.Ordinal) == true)
            {
                if (DateTime.UtcNow - _lastRestart > TimeSpan.FromSeconds(10) && _tab.StartAddressFor(page) is { } again)
                {
                    _lastRestart = DateTime.UtcNow;
                    _browser.Navigate(again);
                }

                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException)
        {
            // The browser closed meanwhile, or cannot run scripts: nothing to repair.
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
        _tab.PropertyChanged -= OnTabChanged;
        _browser.NewWindowRequested -= OnNewWindowRequested;
        _browser.NavigationCompleted -= OnNavigationCompleted;
    }
}
