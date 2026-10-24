using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Plugins;
using Oadm.Client.Settings;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Shell;

/// <summary>Shell: navigation rail (collapsible), current page, connection state.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IOadmApi _api;
    private readonly ILiveVideoAccess? _liveVideo;
    private readonly IClientPluginRegistry _plugins;
    private readonly IUiDispatcher? _ui;

    /// <summary>Counts the syncs of the plugin pages: a sync that creates its pages one by one stops when a newer one started.</summary>
    private int _pageSyncGeneration;
    private readonly TaskPluginCatalog _catalog;
    private readonly PluginPackageStore? _packages;
    private bool _loadingPackagesOnConnect;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IClientSettingsStore _settings;
    private readonly NavItemViewModel _devicesItem;
    private readonly NavItemViewModel _usersItem;
    private readonly NavItemViewModel _credentialsItem;
    private readonly NavItemViewModel? _pluginsItem;
    private readonly NavItemViewModel? _eventRulesItem;
    private readonly NavItemViewModel? _complianceItem;
    private readonly NavItemViewModel? _sitesItem;
    private readonly NavItemViewModel _logsItem;
    private readonly NavItemViewModel _settingsItem;
    private readonly NavItemViewModel _aboutItem;

    public MainWindowViewModel(
        ServerConnection connection,
        DevicesViewModel devices,
        LogsViewModel logs,
        SettingsViewModel settings,
        UsersViewModel users,
        CredentialsViewModel credentials,
        AboutViewModel about,
        TaskPluginCatalog catalog,
        IClientPluginRegistry plugins,
        IOadmApi api,
        IClientSettingsStore clientSettings,
        UserSession session,
        ILogger<MainWindowViewModel> logger,
        PluginPackageStore? packages = null,
        PluginsViewModel? pluginList = null,
        EventRules.EventRulesViewModel? eventRules = null,
        ILiveVideoAccess? liveVideo = null,
        Compliance.ComplianceViewModel? compliance = null,
        Sites.SitesViewModel? sites = null,
        IUiDispatcher? ui = null)
    {
        _liveVideo = liveVideo;
        _ui = ui;
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clientSettings);
        Connection = connection;
        Devices = devices;
        Session = session;
        session.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(UserTooltip));
            OnPropertyChanged(nameof(UserInitial));
            if (e.PropertyName == nameof(UserSession.IsAdmin))
            {
                SyncBottomNavItems();
            }
        };
        _api = api;
        _plugins = plugins;
        _catalog = catalog;
        _packages = packages;
        if (packages is not null)
        {
            packages.Changed += (_, _) =>
            {
                Devices.Toolbar.ApplyPackageStates(packages.IsEnabled);
                if (!_loadingPackagesOnConnect)
                {
                    _ = ReloadPluginsAsync();
                }
            };
        }

        _settings = clientSettings;
        _logger = logger;
        IsNavExpanded = clientSettings.Current.NavRailExpanded;

        // Top: Devices, then one entry per core plugin page (added on connect).
        // Bottom: Users, Credentials and Plugins (administrators only), Logs, Settings, About.
        _devicesItem = new NavItemViewModel(HostPages.Devices, "Devices", "devices", devices);
        NavItems.Add(_devicesItem);
        if (compliance is not null)
        {
            // Built-in page right below Devices (no group), shown while the role may see compliance.
            _complianceItem = new NavItemViewModel(HostPages.Compliance, "Compliance", "squareActivity", compliance);
            SyncComplianceItem();
        }

        if (eventRules is not null)
        {
            // Built-in page in the Automation group, before the plugin pages of that group; shown while the role may see
            // event rules.
            _eventRulesItem = new NavItemViewModel(HostPages.EventRules, "Event rules", "zap", eventRules)
            {
                GroupTitle = CorePluginGroup.Automation.ToString(),
                GroupOrder = (int)CorePluginGroup.Automation,
            };
            SyncEventRulesItem();
        }

        if (sites is not null)
        {
            // Built-in page right below Compliance (no group), shown while the role may see sites.
            _sitesItem = new NavItemViewModel(HostPages.Sites, "Sites", "server", sites);
            SyncSitesItem();
        }

        // A role change (session stream): the toolbar, the rail and the task menu follow what the role allows now.
        Devices.Toolbar.ApplyPermissions(session.Has);
        session.PermissionsChanged += (_, _) =>
        {
            Devices.Toolbar.ApplyPermissions(session.Has);
            SyncEventRulesItem();
            SyncComplianceItem();
            SyncSitesItem();
            if (session.IsLoggedIn)
            {
                _ = ReloadPluginsAsync();
            }
        };

        _usersItem = new NavItemViewModel(HostPages.Users, "Permissions", "users", users);
        _credentialsItem = new NavItemViewModel(HostPages.Credentials, "Credentials", "key", credentials);
        _pluginsItem = pluginList is null ? null : new NavItemViewModel(HostPages.Plugins, "Plugins", "plugin", pluginList);
        _logsItem = new NavItemViewModel(HostPages.Logs, "Logs", "logs", logs);
        _settingsItem = new NavItemViewModel(HostPages.Settings, "Settings", "settings", settings);
        _aboutItem = new NavItemViewModel(HostPages.About, "About", "info", about);
        SyncBottomNavItems();
        UpdateNavGroups();

        connection.Connected += (_, _) => _ = OnConnectedAsync();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UserSession.HasAllDevices) or nameof(UserSession.DeviceGroupName))
            {
                _ = RefreshMyDevicesAsync();
            }
        };
        devices.NavigateRequested += (_, key) => NavigateTo(key);
        if (pluginList is not null)
        {
            pluginList.NavigateRequested += (_, key) => NavigateTo(key);
        }

        devices.OpenInWebViewRequested += (_, deviceId) => OpenInWebView(deviceId);
        OpenItem(_devicesItem);
    }

    public ServerConnection Connection { get; }
    public DevicesViewModel Devices { get; }

    /// <summary>The logged-in user (title bar, with Log out).</summary>
    public UserSession Session { get; }

    /// <summary>First letter of the user name, upper case: the avatar at the bottom of the rail.</summary>
    public string UserInitial => string.IsNullOrEmpty(Session.UserName)
        ? "?"
        : char.ToUpperInvariant(Session.UserName[0]).ToString();

    /// <summary>"Logged in as anna (Administrator) on https://localhost:5080".</summary>
    public string UserTooltip => Session.IsLoggedIn
        ? $"Logged in as {Session.UserName} ({Session.RoleText}) on {Session.ServerAddress}"
        : "Not logged in";

    /// <summary>Raised with a loaded "Your role" view model; the window shows it (a click on the user in the rail).</summary>
    public event EventHandler<MyRoleViewModel>? MyRoleRequested;

    /// <summary>The user in the rail: what the user's role allows.</summary>
    [RelayCommand]
    private async Task ShowMyRoleAsync()
    {
        var role = new MyRoleViewModel(_api);
        await role.LoadAsync().ConfigureAwait(true);
        MyRoleRequested?.Invoke(this, role);
    }

    public static string AppName => "OADM";

    public ObservableCollection<NavItemViewModel> NavItems { get; } = [];
    public ObservableCollection<NavItemViewModel> BottomNavItems { get; } = [];

    [ObservableProperty] public partial object? CurrentPage { get; private set; }

    [ObservableProperty] public partial NavItemViewModel? CurrentItem { get; private set; }

    /// <summary>Rail shows icon + label when true, icons only (with tooltips) when false. Persisted per client.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavToggleText))]
    public partial bool IsNavExpanded { get; set; }

    public string NavToggleText => IsNavExpanded ? "Collapse" : "Expand";

    /// <summary>Banner text after the server replaced its master key (production hardening 3); null = no banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyNotice))]
    public partial string? KeyNoticeText { get; private set; }

    public bool HasKeyNotice => KeyNoticeText is not null;

    private string? _keyNoticeId;

    /// <summary>Hides the key notice on this client for good.</summary>
    [RelayCommand]
    private void DismissKeyNotice()
    {
        if (_keyNoticeId is { } id && !_settings.Current.DismissedKeyNotices.Contains(id))
        {
            _settings.Current.DismissedKeyNotices.Add(id);
            _settings.Save();
        }

        KeyNoticeText = null;
    }

    /// <summary>Shows the server's key replacement notice unless this client dismissed it.</summary>
    internal void ApplyKeyNotice(KeyReplacedNotice? notice)
    {
        if (notice is null || string.IsNullOrEmpty(notice.Id) || _settings.Current.DismissedKeyNotices.Contains(notice.Id))
        {
            _keyNoticeId = null;
            KeyNoticeText = null;
            return;
        }

        _keyNoticeId = notice.Id;
        KeyNoticeText = notice.Message;
    }

    /// <summary>
    /// Banner for administrators after the upgrade to roles: what the Operator role no longer has, with a link to the Users
    /// page; null = no banner. Dismissed per client like the key notice.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRolesNotice))]
    public partial string? RolesNoticeText { get; private set; }

    public bool HasRolesNotice => RolesNoticeText is not null;

    private string? _rolesNoticeId;

    [RelayCommand]
    private void DismissRolesNotice()
    {
        if (_rolesNoticeId is { } id && !_settings.Current.DismissedKeyNotices.Contains(id))
        {
            _settings.Current.DismissedKeyNotices.Add(id);
            _settings.Save();
        }

        RolesNoticeText = null;
    }

    /// <summary>The banner's "Check roles": the Users page with the permission matrix.</summary>
    [RelayCommand]
    private void OpenRoles()
    {
        if (BottomNavItems.Contains(_usersItem))
        {
            OpenItem(_usersItem);
        }
    }

    /// <summary>Shows the notice of the upgrade to roles (administrators only get it) unless this client dismissed it.</summary>
    internal void ApplyRolesNotice(RolesUpgradeNotice? notice)
    {
        if (notice is null || string.IsNullOrEmpty(notice.Id) || notice.OperatorLost.Count == 0 || _settings.Current.DismissedKeyNotices.Contains(notice.Id))
        {
            _rolesNoticeId = null;
            RolesNoticeText = null;
            return;
        }

        _rolesNoticeId = notice.Id;
        RolesNoticeText = "With roles, operators no longer have: " + string.Join(", ", notice.OperatorLost) + ".";
    }

    partial void OnIsNavExpandedChanged(bool value)
    {
        _settings.Current.NavRailExpanded = value;
        _settings.Save();
        UpdateNavGroups();
    }

    [RelayCommand]
    private void ToggleNav() => IsNavExpanded = !IsNavExpanded;

    /// <summary>Starts the server streams. Call after the window is shown (again after every login).</summary>
    public void Start()
    {
        OpenItem(_devicesItem);
        _pendingStartPage = Session.StartPage is "" or HostPages.Devices ? null : Session.StartPage;
        OpenStartPage();
        Connection.Start();
    }

    /// <summary>
    /// The role's start page, from the login until the first connect loaded the plugin pages: opened as soon as its rail
    /// entry exists. Null once it opened, the user opened a page, or the page is not there (no right, plugin off: Devices).
    /// </summary>
    private string? _pendingStartPage;

    private void OpenStartPage()
    {
        if (_pendingStartPage is { } key && FindTarget(key) is { } target)
        {
            _pendingStartPage = null;
            OpenTarget(target);
        }
    }

    /// <summary>
    /// A page opened by its key (<c>OpenAsync</c>, the Plugins page): a rail entry, or the page of a tab
    /// (<see cref="ICorePluginPage.TabOf"/>) on that tab; a page with tabs opens on its own first tab. Unknown keys do nothing.
    /// </summary>
    internal void NavigateTo(string key)
    {
        if (FindTarget(key) is { } target)
        {
            _pendingStartPage = null;
            OpenTarget(target);
        }
    }

    /// <summary>The rail entry of a key and the tab to show on it (null: none).</summary>
    private (NavItemViewModel Item, string? Tab)? FindTarget(string key)
    {
        if (NavItems.Concat(BottomNavItems).FirstOrDefault(n => n.Key == key) is { } item)
        {
            return item.Page is CorePluginPageViewModel page && _tabPages.Any(t => t.HostId == page.PluginId) ? (item, page.PluginId) : (item, null);
        }

        return _tabPages.FirstOrDefault(t => "plugin:" + t.PluginId == key) is { } tab
            && NavItems.FirstOrDefault(n => n.Key == "plugin:" + tab.HostId) is { } host
            ? (host, tab.PluginId)
            : null;
    }

    private void OpenTarget((NavItemViewModel Item, string? Tab) target)
    {
        OpenItem(target.Item);
        if (target.Tab is { } tab && target.Item.Page is CorePluginPageViewModel { PluginContext: { } context })
        {
            context.RequestOpenTab(tab);
        }
    }

    /// <summary>Logout: stops the streams, closes the live view and the plugin windows.</summary>
    public void Stop()
    {
        Connection.Stop();
        Devices.LiveView.CloseCommand.Execute(null);
        ClosePluginWindowsRequested?.Invoke(this, null);
    }

    /// <summary>A plugin page to show in a window of its own (the view opens it).</summary>
    public event EventHandler<CorePluginPageViewModel>? PluginWindowRequested;

    /// <summary>Close the plugin windows of one plugin (turned off) or all of them (null: logout).</summary>
    public event EventHandler<string?>? ClosePluginWindowsRequested;

    /// <summary>
    /// Another copy of a plugin page for a window of its own: the plugin creates a new view with its own context, so
    /// streams and state are independent of the page in the rail.
    /// </summary>
    internal CorePluginPageViewModel? CreatePluginWindowPage(NavItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Page is not CorePluginPageViewModel rail || _plugins.FindPage(rail.PluginId) is not { } page)
        {
            return null;
        }

        var context = new CorePluginClientContext(_api, rail.PluginId, Devices.ToolbarContext, _liveVideo, _settings, PluginOn, id => _packages?.LicenseOf(id));
        try
        {
            // The window's own copies of the tab views (a control has one parent); disposed with the window's context.
            var copies = _tabPages.Where(t => t.HostId == rail.PluginId).Select(t => CreateTabPage(t.PluginId, t.Title)).ToList();
            context.SetTabs([.. copies.Select(c => c.Tab)], [.. copies.Select(c => c.Page)]);
            var view = page.CreateView(context);
            return new CorePluginPageViewModel(rail.PluginId, rail.Title, view, page.HasOwnCards,
                page.ShowTasksPane ? Devices.Tasks : null, context: context);
        }
        catch (Exception ex)
        {
            context.Dispose();
            LogPageFailed(_logger, ex, rail.PluginId);
            _ = ReportPageFailureAsync(rail.Title, ex);
            return null;
        }
    }

    private void OpenPluginWindow(NavItemViewModel item)
    {
        if (CreatePluginWindowPage(item) is { } page)
        {
            PluginWindowRequested?.Invoke(this, page);
        }
    }

    [RelayCommand]
    private void Logout() => Session.RequestLogout(null);

    /// <summary>The user opens a page: a start page that is still loading no longer takes over.</summary>
    [RelayCommand]
    private void Navigate(NavItemViewModel? item)
    {
        _pendingStartPage = null;
        OpenItem(item);
    }

    private void OpenItem(NavItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (NavItemViewModel nav in NavItems.Concat(BottomNavItems))
        {
            nav.IsSelected = nav == item;
        }

        CurrentItem = item;
        CurrentPage = item.Page;

        // The add page and other clients change the credential list: read it again whenever the page opens.
        if (item.Page is CredentialsViewModel credentials)
        {
            _ = credentials.LoadAsync();
        }

        // The plugin list is read again when the page opens: OADM PRO may have ended or been imported on another client.
        if (item.Page is PluginsViewModel plugins)
        {
            _ = plugins.RefreshAsync();
        }

        // The server log is read when the page opens (newest page first).
        if (item.Page is Logging.LogsViewModel logs)
        {
            _ = logs.OnShownAsync();
        }
    }

    /// <summary>
    /// The bottom group follows the role of the logged-in user: administrators see Users, Credentials and Plugins, operators
    /// do not (the server refuses those calls for them). A hidden page that was open falls back to Devices.
    /// </summary>
    private void SyncBottomNavItems()
    {
        // Plugins first, with a divider below it: turning plugins on and off changes what the whole client offers.
        NavItemViewModel[] adminItems = _pluginsItem is null ? [_usersItem, _credentialsItem] : [_pluginsItem, _usersItem, _credentialsItem];
        NavItemViewModel[] wanted = Session.IsAdmin
            ? [.. adminItems, _logsItem, _settingsItem, _aboutItem]
            : [_logsItem, _settingsItem, _aboutItem];
        if (BottomNavItems.SequenceEqual(wanted))
        {
            return;
        }

        bool lostCurrent = CurrentItem is not null && BottomNavItems.Contains(CurrentItem) && !wanted.Contains(CurrentItem);
        BottomNavItems.Clear();
        foreach (NavItemViewModel item in wanted)
        {
            BottomNavItems.Add(item);
        }

        if (lostCurrent)
        {
            OpenItem(_devicesItem);
        }
    }

    private async Task OnConnectedAsync()
    {
        _ = RefreshMyDevicesAsync();
        if (_packages is not null)
        {
            _loadingPackagesOnConnect = true;
            try
            {
                await _packages.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                _loadingPackagesOnConnect = false;
            }
        }

        await _catalog.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            IReadOnlyList<CorePluginInfo> corePlugins = await _api.ListCorePluginsAsync(CancellationToken.None).ConfigureAwait(true);
            await SyncCorePluginPagesAsync(corePlugins).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogCorePluginsFailed(_logger, ex.Message);
        }

        // Every page this connect brings is there now: a start page that did not come is not waited for.
        OpenStartPage();
        _pendingStartPage = null;

        try
        {
            ServerSettings settings = await _api.GetSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            ApplyKeyNotice(settings.KeyReplaced);
            ApplyRolesNotice(settings.RolesNotice);
        }
        catch (Exception ex)
        {
            LogKeyNoticeFailed(_logger, ex.Message);
        }
    }

    /// <summary>
    /// A user with a device group: reads Your role's devices, so the Add entries know whether the group can add devices
    /// (<see cref="UserSession.AddDevicesRefusal"/>). After every connect (a changed group ends the streams) and when the
    /// user's devices change.
    /// </summary>
    internal async Task RefreshMyDevicesAsync()
    {
        if (!Session.IsLoggedIn || Session.HasAllDevices)
        {
            Session.ApplyDevices(null);
            return;
        }

        try
        {
            MyRole role = await _api.GetMyRoleAsync(CancellationToken.None).ConfigureAwait(true);
            Session.ApplyDevices(role.Devices);
        }
        catch (Exception ex)
        {
            LogMyDevicesFailed(_logger, ex.Message);
        }
    }

    /// <summary>A plugin package was turned on or off: rail pages and task menus follow the server.</summary>
    private async Task ReloadPluginsAsync()
    {
        try
        {
            await _catalog.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            await SyncCorePluginPagesAsync(await _api.ListCorePluginsAsync(CancellationToken.None).ConfigureAwait(true)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogCorePluginsFailed(_logger, ex.Message);
        }
    }

    /// <summary>The Compliance entry (right below Devices) shows while the role may see compliance.</summary>
    private void SyncComplianceItem()
    {
        if (_complianceItem is null)
        {
            return;
        }

        bool wanted = Session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.ComplianceView);
        bool shown = NavItems.Contains(_complianceItem);
        if (wanted && !shown)
        {
            NavItems.Insert(Math.Min(NavItems.IndexOf(_devicesItem) + 1, NavItems.Count), _complianceItem);
            OpenStartPage();
        }
        else if (!wanted && shown)
        {
            NavItems.Remove(_complianceItem);
            if (_complianceItem.IsSelected)
            {
                OpenItem(_devicesItem);
            }
        }

        UpdateNavGroups();
    }

    /// <summary>
    /// The Event rules entry shows while the role may see event rules and the user has all devices (event rules, the event
    /// log and Test are for users with all devices only, docs/spec/device-scope.md); a hidden page that was open falls back
    /// to Devices.
    /// </summary>
    private void SyncEventRulesItem()
    {
        if (_eventRulesItem is null)
        {
            return;
        }

        bool wanted = Session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.EventsView) && Session.HasAllDevices;
        bool shown = NavItems.Contains(_eventRulesItem);
        if (wanted && !shown)
        {
            NavItems.Insert(Math.Min(1, NavItems.Count), _eventRulesItem);
            OrderCorePluginPages();
            OpenStartPage();
        }
        else if (!wanted && shown)
        {
            NavItems.Remove(_eventRulesItem);
            if (_eventRulesItem.IsSelected)
            {
                OpenItem(_devicesItem);
            }

            UpdateNavGroups();
        }
    }

    /// <summary>The Sites entry (right below Compliance, else Devices) shows while the role may see sites; a hidden page that was open falls back to Devices.</summary>
    private void SyncSitesItem()
    {
        if (_sitesItem is null)
        {
            return;
        }

        bool wanted = Session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.SitesView);
        bool shown = NavItems.Contains(_sitesItem);
        if (wanted && !shown)
        {
            var above = _complianceItem is not null && NavItems.Contains(_complianceItem) ? _complianceItem : _devicesItem;
            NavItems.Insert(Math.Min(NavItems.IndexOf(above) + 1, NavItems.Count), _sitesItem);
            UpdateNavGroups();
            OpenStartPage();
        }
        else if (!wanted && shown)
        {
            NavItems.Remove(_sitesItem);
            if (_sitesItem.IsSelected)
            {
                OpenItem(_devicesItem);
            }

            UpdateNavGroups();
        }
    }

    /// <summary>One navigation entry per core plugin with a page (<c>no_page</c> unset), below "Devices".</summary>
    internal void SyncCorePluginPages(IReadOnlyList<CorePluginInfo> corePlugins)
    {
        _pageSyncGeneration++;
        corePlugins = RemoveStalePluginPages(corePlugins);
        foreach (CorePluginInfo plugin in corePlugins.Where(p => !IsShown(p.Id)))
        {
            AddCorePluginPage(plugin, corePlugins);
        }

        OrderCorePluginPages();
        SyncWebViewAccess();
        OpenStartPage();
    }

    /// <summary>
    /// <see cref="SyncCorePluginPages"/> with one new page per UI job below the priority of rendering: a plugin creating
    /// its view (XAML, the first run of its code) can take 100 ms, and this runs on connect while the start splash animates,
    /// which then keeps its frames. A newer sync takes over (this one stops); every job checks the page again first.
    /// </summary>
    internal async Task SyncCorePluginPagesAsync(IReadOnlyList<CorePluginInfo> corePlugins)
    {
        if (_ui is null)
        {
            SyncCorePluginPages(corePlugins);
            return;
        }

        int generation = ++_pageSyncGeneration;
        corePlugins = RemoveStalePluginPages(corePlugins);
        OrderCorePluginPages();
        SyncWebViewAccess();
        foreach (CorePluginInfo plugin in corePlugins)
        {
            await _ui.RunInBackgroundAsync(() =>
            {
                if (generation == _pageSyncGeneration && !IsShown(plugin.Id))
                {
                    AddCorePluginPage(plugin, corePlugins);
                    OrderCorePluginPages();
                    SyncWebViewAccess();
                    OpenStartPage();
                }
            }).ConfigureAwait(true);
            if (generation != _pageSyncGeneration)
            {
                return;
            }
        }
    }

    /// <summary>The plugin's page is in the rail or a tab of another page.</summary>
    private bool IsShown(string pluginId) => NavItems.Any(n => n.Key == "plugin:" + pluginId) || _tabPages.Any(t => t.PluginId == pluginId);

    /// <summary>The page the plugin's page is a tab of (<see cref="ICorePluginPage.TabOf"/>) when that page is listed; else null (a rail page).</summary>
    private string? HostOf(string pluginId, IReadOnlyList<CorePluginInfo> listed)
    {
        string? host;
        try
        {
            host = _plugins.FindPage(pluginId)?.TabOf;
        }
#pragma warning disable CA1031 // A plugin that throws here gets a rail page.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        return host is { Length: > 0 } && !string.Equals(host, pluginId, StringComparison.Ordinal)
            && listed.Any(p => p.Id == host) && _plugins.FindPage(host) is not null
            ? host
            : null;
    }

    /// <summary>Pages of core plugins shown as tabs of another plugin's page (tests).</summary>
    internal IReadOnlyList<TabPage> TabPages => _tabPages;

    private readonly List<TabPage> _tabPages = [];

    /// <summary>A page shown as a tab: its plugin, the page it is a tab of, its title, its page (view and context) and the tab the host page gets.</summary>
    internal sealed record TabPage(string PluginId, string HostId, string Title, CorePluginPageViewModel Page, CorePluginTab Tab);

    /// <summary>Removes the rail pages and tabs of plugins not in the list (or now in the other place); returns the plugins with a page.</summary>
    private List<CorePluginInfo> RemoveStalePluginPages(IReadOnlyList<CorePluginInfo> all)
    {
        List<CorePluginInfo> corePlugins = [.. all.Where(p => !p.NoPage)];
        var changedHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (TabPage stale in _tabPages.Where(t => !corePlugins.Any(p => p.Id == t.PluginId) || HostOf(t.PluginId, corePlugins) != t.HostId).ToList())
        {
            _tabPages.Remove(stale);
            changedHosts.Add(stale.HostId);
            stale.Page.Dispose();
        }

        foreach (string host in changedHosts)
        {
            PushTabs(host);
        }

        foreach (NavItemViewModel stale in NavItems.Where(n => n.Page is CorePluginPageViewModel page
                     && (!corePlugins.Any(p => "plugin:" + p.Id == n.Key) || HostOf(page.PluginId, corePlugins) is not null)).ToList())
        {
            NavItems.Remove(stale);
            var stalePage = (CorePluginPageViewModel)stale.Page;
            ClosePluginWindowsRequested?.Invoke(this, stalePage.PluginId);
            if (stale.IsSelected)
            {
                OpenItem(_devicesItem);
            }

            // The removed page's view and view model must not stay reachable from the Devices page context.
            stalePage.Dispose();
        }

        return corePlugins;
    }

    /// <summary>The host page's context gets its tabs now (in the server's order); its windows close, their tabs changed.</summary>
    private void PushTabs(string hostId)
    {
        if (NavItems.FirstOrDefault(n => n.Key == "plugin:" + hostId)?.Page is CorePluginPageViewModel { PluginContext: { } context })
        {
            context.SetTabs([.. _tabPages.Where(t => t.HostId == hostId).Select(t => t.Tab)]);
        }

        ClosePluginWindowsRequested?.Invoke(this, hostId);
    }

    /// <summary>The page of a plugin shown as a tab: its view with its own context, or the error in its place.</summary>
    private (CorePluginPageViewModel Page, CorePluginTab Tab) CreateTabPage(string pluginId, string title)
    {
        var context = new CorePluginClientContext(_api, pluginId, Devices.ToolbarContext, _liveVideo, _settings, PluginOn, id => _packages?.LicenseOf(id));
        Avalonia.Controls.Control view;
        CorePluginPageViewModel page;
        try
        {
            view = _plugins.FindPage(pluginId)!.CreateView(context);
            page = new CorePluginPageViewModel(pluginId, title, view, context: context);
        }
        catch (Exception ex)
        {
            context.Dispose();
            LogPageFailed(_logger, ex, pluginId);
            _ = ReportPageFailureAsync(title, ex);
            page = new CorePluginPageViewModel(pluginId, title, null, error: ex.Message);
            var text = new Avalonia.Controls.TextBlock { Text = page.NoViewText, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            text.Classes.Add("secondary");
            view = text;
        }

        return (page, new CorePluginTab(pluginId, title, view));
    }

    /// <summary>Creates the page of one core plugin (its view, or the error) and adds it to the rail, or as a tab of its host page.</summary>
    private void AddCorePluginPage(CorePluginInfo plugin, IReadOnlyList<CorePluginInfo> listed)
    {
        if (HostOf(plugin.Id, listed) is { } hostId)
        {
            var (tabPage, tab) = CreateTabPage(plugin.Id, plugin.DisplayName);
            _tabPages.Add(new TabPage(plugin.Id, hostId, plugin.DisplayName, tabPage, tab));
            _tabPages.Sort((a, b) => IndexIn(listed, a.PluginId).CompareTo(IndexIn(listed, b.PluginId)));
            PushTabs(hostId);
            return;
        }

        object? view = null;
        string? error = null;
        CorePluginClientContext? context = null;
        ICorePluginPage? page = _plugins.FindPage(plugin.Id);
        if (page is not null)
        {
            context = new CorePluginClientContext(_api, plugin.Id, Devices.ToolbarContext, _liveVideo, _settings, PluginOn, id => _packages?.LicenseOf(id));
            context.SetTabs([.. _tabPages.Where(t => t.HostId == plugin.Id).Select(t => t.Tab)]);
            try
            {
                view = page.CreateView(context);
            }
            catch (Exception ex)
            {
                context.Dispose();
                context = null;
                // The page shows the error instead of its view; the client keeps running (production hardening 4).
                LogPageFailed(_logger, ex, plugin.Id);
                error = ex.Message;
                _ = ReportPageFailureAsync(plugin.DisplayName, ex);
            }
        }

        CorePluginGroup group = Enum.TryParse(plugin.Group, ignoreCase: true, out CorePluginGroup parsed) && Enum.IsDefined(parsed)
            ? parsed
            : CorePluginGroup.Extensions;
        NavItems.Add(new NavItemViewModel("plugin:" + plugin.Id, plugin.DisplayName,
            string.IsNullOrEmpty(plugin.IconKey) ? "plugin" : plugin.IconKey,
            new CorePluginPageViewModel(plugin.Id, plugin.DisplayName, view, page?.HasOwnCards == true,
                page?.ShowTasksPane == true && view is not null ? Devices.Tasks : null, error, context))
        {
            GroupTitle = group.ToString(),
            GroupOrder = (int)group,
            OpenInWindow = OpenPluginWindow,
        });
    }

    private static int IndexIn(IReadOnlyList<CorePluginInfo> listed, string pluginId)
    {
        for (var i = 0; i < listed.Count; i++)
        {
            if (listed[i].Id == pluginId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>The permission to open devices in Web View.</summary>
    internal const string WebViewOpenPermission = "oadm.web-view:open";

    /// <summary>
    /// Devices at a site open in Web View while its page is in the rail and the role may open devices; otherwise the reason
    /// comes from the package list: off ("Turn on Web View ...") or on but not for this role.
    /// </summary>
    private void SyncWebViewAccess()
    {
        bool page = NavItems.Any(n => n.Key == WebViewPageKey);
        bool mayOpen = Session.Has(WebViewOpenPermission);
        bool? on = PluginOn(WebViewPackageId);
        Devices.WebViewAccess = page && mayOpen ? SiteWebAccess.Available
            : on == true || (on is null && page) ? SiteWebAccess.NotAllowed
            : SiteWebAccess.Off;
    }

    /// <summary>The Web View plugin package.</summary>
    internal const string WebViewPackageId = "oadm.web-view";

    /// <summary>The rail key of the Web View page (plugin oadm.web-view).</summary>
    internal const string WebViewPageKey = "plugin:oadm.web-view";

    /// <summary>A device at a site: its web interface opens in the Web View page (docs/spec/sites.md, Client).</summary>
    private void OpenInWebView(string deviceId)
    {
        if (NavItems.FirstOrDefault(n => n.Key == WebViewPageKey) is not { Page: CorePluginPageViewModel page } item)
        {
            return;
        }

        Navigate(item);
        page.PluginContext?.RequestOpenDevice(deviceId);
    }

    /// <summary>Core plugin pages sorted by group (<see cref="CorePluginGroup"/> order), the server's order inside a group.</summary>
    private void OrderCorePluginPages()
    {
        var pages = NavItems.Where(n => n.GroupTitle is not null).ToList();
        var ordered = pages.OrderBy(n => n.GroupOrder).ToList(); // stable
        if (!pages.SequenceEqual(ordered))
        {
            foreach (NavItemViewModel page in pages)
            {
                NavItems.Remove(page);
            }

            foreach (NavItemViewModel page in ordered)
            {
                NavItems.Add(page);
            }
        }

        UpdateNavGroups();
    }

    /// <summary>
    /// Group starts of the core plugin pages: only groups with pages appear; expanded rail = a
    /// small header above the first page of each group (and the separator under Devices), collapsed rail = a separator.
    /// </summary>
    private void UpdateNavGroups()
    {
        string? previous = null;
        var first = true;
        foreach (NavItemViewModel item in NavItems.Where(n => n.GroupTitle is not null))
        {
            bool start = !string.Equals(item.GroupTitle, previous, StringComparison.Ordinal);
            item.ShowGroupHeader = start && IsNavExpanded;
            item.HasSeparatorBefore = first || (start && !IsNavExpanded);
            previous = item.GroupTitle;
            first = false;
        }
    }

    private async Task ReportPageFailureAsync(string pageName, Exception ex)
    {
        try
        {
            await Devices.ToolbarContext.ShowMessageAsync(pageName, $"The {pageName} page failed: {ex.Message}").ConfigureAwait(true);
        }
        catch (Exception showFailed)
        {
            LogPageFailed(_logger, showFailed, pageName);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list core plugins: {Reason}")]
    private static partial void LogCorePluginsFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the user's devices failed: {Message}")]
    private static partial void LogMyDevicesFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the server settings: {Reason}")]
    private static partial void LogKeyNoticeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Core plugin page {PluginId} failed to create its view")]
    private static partial void LogPageFailed(ILogger logger, Exception ex, string pluginId);
    /// <summary>Whether a plugin package is on, for <see cref="ICorePluginClientContext.IsPluginOn"/>; null when not listed.</summary>
    private bool? PluginOn(string packageId) =>
        _packages?.Packages.FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase)) is { } package ? package.Enabled : null;

}

/// <summary>
/// Bridges a core plugin page to PluginService.Invoke; devices, selection, dialogs and host pages come from the
/// Devices page's <see cref="IToolbarContext"/> (same data and dialogs as toolbar plugins).
/// </summary>
/// <remarks>
/// The plugin's <see cref="DevicesChanged"/> handlers stay on this context; the context holds one handler on the
/// long-lived host. <see cref="Dispose"/> (page removed, plugin window closed) detaches it, so a closed page view and its
/// view model are no longer reachable from the host, whatever the plugin subscribed.
/// </remarks>
internal sealed class CorePluginClientContext : ICorePluginClientContext, IDisposable
{
    private readonly IOadmApi _api;
    private readonly string _pluginId;
    private readonly IToolbarContext _host;
    private EventHandler? _devicesChanged;
    private readonly List<EventHandler?> _permissionHandlers = [];
    private readonly ILiveVideoAccess? _liveVideoHost;
    private Oadm.Client.DeviceWeb.DeviceWebAccess? _deviceWeb;
    private Oadm.Client.LiveView.PageLiveVideoAccess? _liveVideo;
    private bool _hostSubscribed;
    private bool _disposed;

    public CorePluginClientContext(IOadmApi api, string pluginId, IToolbarContext host, ILiveVideoAccess? liveVideo = null, IClientSettingsStore? settings = null,
        Func<string, bool?>? pluginOn = null, Func<string, Oadm.Sdk.Licensing.PluginLicenseInfo?>? licenseOf = null)
    {
        _licenseOf = licenseOf;
        _api = api;
        _pluginId = pluginId;
        _host = host;
        _liveVideoHost = liveVideo;
        _settings = settings;
        _pluginOn = pluginOn;
    }

    private readonly Func<string, bool?>? _pluginOn;

    public bool? IsPluginOn(string packageId) => _pluginOn?.Invoke(packageId);

    public event EventHandler<string>? OpenDeviceRequested;

    private IReadOnlyList<CorePluginTab> _tabs = [];
    private IReadOnlyList<IDisposable> _ownedTabs = [];

    public IReadOnlyList<CorePluginTab> Tabs => _tabs;

    public event EventHandler? TabsChanged;

    public event EventHandler<string>? OpenTabRequested;

    /// <summary>
    /// The pages shown as tabs of this page (<see cref="ICorePluginPage.TabOf"/>); <paramref name="owned"/> are disposed with
    /// this context or the next call (the copies of a plugin window). Raises <see cref="TabsChanged"/> when the list changed.
    /// </summary>
    internal void SetTabs(IReadOnlyList<CorePluginTab> tabs, IReadOnlyList<IDisposable>? owned = null)
    {
        var old = _ownedTabs;
        _ownedTabs = owned ?? [];
        foreach (IDisposable page in old)
        {
            page.Dispose();
        }

        if (_disposed || _tabs.SequenceEqual(tabs))
        {
            return;
        }

        _tabs = tabs;
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The host asks the page to show a tab (a key of the tab's plugin, or the page's own id).</summary>
    internal void RequestOpenTab(string pluginId)
    {
        if (!_disposed)
        {
            OpenTabRequested?.Invoke(this, pluginId);
        }
    }

    /// <summary>The host asks the page to open a device (Web View: a device at a site, from the Devices page).</summary>
    internal void RequestOpenDevice(string deviceId)
    {
        if (!_disposed)
        {
            OpenDeviceRequested?.Invoke(this, deviceId);
        }
    }

    private readonly Func<string, Oadm.Sdk.Licensing.PluginLicenseInfo?>? _licenseOf;

    public Oadm.Sdk.Licensing.PluginLicenseInfo? LicenseOf(string packageId) => _licenseOf?.Invoke(packageId);

    /// <summary>Most bytes of one value a plugin keeps on this computer.</summary>
    public const int MaxLocalSettingLength = 64 * 1024;

    private readonly IClientSettingsStore? _settings;

    public string? GetLocalSetting(string key) =>
        _settings?.Current.PluginSettings.GetValueOrDefault(_pluginId + "/" + key);

    public void SetLocalSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_settings is null || (value?.Length ?? 0) > MaxLocalSettingLength)
        {
            return;
        }

        var name = _pluginId + "/" + key;
        if (value is null ? _settings.Current.PluginSettings.Remove(name) : _settings.Current.PluginSettings.GetValueOrDefault(name) != value)
        {
            if (value is not null)
            {
                _settings.Current.PluginSettings[name] = value;
            }

            _settings.Save();
        }
    }

    public IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> Devices => _host.Devices;

    public IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> SelectedDevices => _host.SelectedDevices;

    public bool Has(string permission) => _host.Has(permission);

    public bool HasAllDevices => _host.HasAllDevices;

    /// <summary>The host's permission changes, while the page lives (a closed page unsubscribes on Dispose).</summary>
    public event EventHandler? PermissionsChanged
    {
        add
        {
            if (!_disposed)
            {
                _host.PermissionsChanged += value;
                _permissionHandlers.Add(value);
            }
        }

        remove
        {
            _host.PermissionsChanged -= value;
            _permissionHandlers.Remove(value);
        }
    }

    public event EventHandler? DevicesChanged
    {
        add
        {
            if (_disposed)
            {
                return;
            }

            _devicesChanged += value;
            if (!_hostSubscribed)
            {
                _host.DevicesChanged += OnHostDevicesChanged;
                _hostSubscribed = true;
            }
        }
        remove => _devicesChanged -= value;
    }

    /// <summary>True while the context forwards the host's device changes (tests).</summary>
    internal bool IsAttached => _hostSubscribed;

    private void OnHostDevicesChanged(object? sender, EventArgs e) => _devicesChanged?.Invoke(this, e);

    /// <summary>Drops every subscription of the plugin and the one on the host.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _devicesChanged = null;
        TabsChanged = null;
        OpenTabRequested = null;
        foreach (IDisposable page in _ownedTabs)
        {
            page.Dispose();
        }

        _ownedTabs = [];
        _tabs = [];
        foreach (EventHandler? handler in _permissionHandlers)
        {
            _host.PermissionsChanged -= handler;
        }

        _permissionHandlers.Clear();
        _deviceWeb?.Dispose();
        _deviceWeb = null;
        _liveVideo?.Dispose();
        _liveVideo = null;
        if (_hostSubscribed)
        {
            _host.DevicesChanged -= OnHostDevicesChanged;
            _hostSubscribed = false;
        }
    }

    public string OwnerName => TaskPluginRunner.OwnerName;

    public Avalonia.Controls.Window? Owner => _host.Owner;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        _api.InvokeCorePluginAsync(_pluginId, method, payloadJson, ct);

    public async IAsyncEnumerable<Oadm.Sdk.Plugins.PluginEvent> WatchEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _api.WatchCorePluginAsync(_pluginId, ct).ConfigureAwait(true))
        {
            yield return new Oadm.Sdk.Plugins.PluginEvent(item.Topic, string.IsNullOrEmpty(item.PayloadJson) ? null : item.PayloadJson);
        }
    }

    public Task<Oadm.Sdk.Plugins.PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) =>
        _api.DownloadCorePluginAsync(_pluginId, method, payloadJson, ct);

    public Task ShowMessageAsync(string title, string message) => _host.ShowMessageAsync(title, message);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => _host.ConfirmAsync(title, message, confirmText);

    public Task OpenAsync(string hostPage) => _host.OpenAsync(hostPage);

    /// <summary>The page's relays of device web interfaces; stopped when the page closes.</summary>
    public IDeviceWebAccess? DeviceWeb =>
        _disposed ? null : _deviceWeb ??= new Oadm.Client.DeviceWeb.DeviceWebAccess(_api, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    /// <summary>The page's live videos; disposed when the page closes.</summary>
    public ILiveVideoAccess? LiveVideo =>
        _disposed || _liveVideoHost is null ? null : _liveVideo ??= new Oadm.Client.LiveView.PageLiveVideoAccess(_liveVideoHost);
}
