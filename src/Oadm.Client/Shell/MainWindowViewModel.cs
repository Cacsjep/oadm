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
        ILiveVideoAccess? liveVideo = null)
    {
        _liveVideo = liveVideo;
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

        // A role change (session stream): the toolbar, the rail and the task menu follow what the role allows now.
        Devices.Toolbar.ApplyPermissions(session.Has);
        session.PermissionsChanged += (_, _) =>
        {
            Devices.Toolbar.ApplyPermissions(session.Has);
            SyncEventRulesItem();
            if (session.IsLoggedIn)
            {
                _ = ReloadPluginsAsync();
            }
        };

        _usersItem = new NavItemViewModel(HostPages.Users, "Users", "users", users);
        _credentialsItem = new NavItemViewModel(HostPages.Credentials, "Credentials", "key", credentials);
        _pluginsItem = pluginList is null ? null : new NavItemViewModel(HostPages.Plugins, "Plugins", "plugin", pluginList);
        _logsItem = new NavItemViewModel(HostPages.Logs, "Logs", "logs", logs);
        _settingsItem = new NavItemViewModel(HostPages.Settings, "Settings", "settings", settings);
        _aboutItem = new NavItemViewModel(HostPages.About, "About", "info", about);
        SyncBottomNavItems();
        UpdateNavGroups();

        connection.Connected += (_, _) => _ = OnConnectedAsync();
        devices.NavigateRequested += (_, key) => Navigate(NavItems.Concat(BottomNavItems).FirstOrDefault(n => n.Key == key));
        Navigate(_devicesItem);
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
            Navigate(_usersItem);
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
        Navigate(_devicesItem);
        Connection.Start();
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

    [RelayCommand]
    private void Navigate(NavItemViewModel? item)
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
            Navigate(_devicesItem);
        }
    }

    private async Task OnConnectedAsync()
    {
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
            SyncCorePluginPages(corePlugins);
        }
        catch (Exception ex)
        {
            LogCorePluginsFailed(_logger, ex.Message);
        }

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

    /// <summary>A plugin package was turned on or off: rail pages and task menus follow the server.</summary>
    private async Task ReloadPluginsAsync()
    {
        try
        {
            await _catalog.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            SyncCorePluginPages(await _api.ListCorePluginsAsync(CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            LogCorePluginsFailed(_logger, ex.Message);
        }
    }

    /// <summary>The Event rules entry shows while the role may see event rules; a hidden page that was open falls back to Devices.</summary>
    private void SyncEventRulesItem()
    {
        if (_eventRulesItem is null)
        {
            return;
        }

        bool wanted = Session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.EventsView);
        bool shown = NavItems.Contains(_eventRulesItem);
        if (wanted && !shown)
        {
            NavItems.Insert(Math.Min(1, NavItems.Count), _eventRulesItem);
            OrderCorePluginPages();
        }
        else if (!wanted && shown)
        {
            NavItems.Remove(_eventRulesItem);
            if (_eventRulesItem.IsSelected)
            {
                Navigate(_devicesItem);
            }

            UpdateNavGroups();
        }
    }

    /// <summary>One navigation entry per core plugin with a page (<c>no_page</c> unset), below "Devices".</summary>
    internal void SyncCorePluginPages(IReadOnlyList<CorePluginInfo> corePlugins)
    {
        corePlugins = [.. corePlugins.Where(p => !p.NoPage)];
        foreach (NavItemViewModel stale in NavItems.Where(n => n.Page is CorePluginPageViewModel && !corePlugins.Any(p => "plugin:" + p.Id == n.Key)).ToList())
        {
            NavItems.Remove(stale);
            var stalePage = (CorePluginPageViewModel)stale.Page;
            ClosePluginWindowsRequested?.Invoke(this, stalePage.PluginId);
            if (stale.IsSelected)
            {
                Navigate(_devicesItem);
            }

            // The removed page's view and view model must not stay reachable from the Devices page context.
            stalePage.Dispose();
        }

        foreach (CorePluginInfo plugin in corePlugins.Where(p => NavItems.All(n => n.Key != "plugin:" + p.Id)))
        {
            object? view = null;
            string? error = null;
            CorePluginClientContext? context = null;
            ICorePluginPage? page = _plugins.FindPage(plugin.Id);
            if (page is not null)
            {
                context = new CorePluginClientContext(_api, plugin.Id, Devices.ToolbarContext, _liveVideo, _settings, PluginOn, id => _packages?.LicenseOf(id));
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

        OrderCorePluginPages();
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
