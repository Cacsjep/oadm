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

namespace Oadm.Client.Shell;

/// <summary>Shell: navigation rail (collapsible), current page, connection state.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IOadmApi _api;
    private readonly IClientPluginRegistry _plugins;
    private readonly TaskPluginCatalog _catalog;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IClientSettingsStore _settings;
    private readonly NavItemViewModel _devicesItem;
    private readonly NavItemViewModel _usersItem;
    private readonly NavItemViewModel _credentialsItem;
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
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clientSettings);
        Connection = connection;
        Devices = devices;
        Session = session;
        session.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(UserTooltip));
            if (e.PropertyName == nameof(UserSession.IsAdmin))
            {
                SyncBottomNavItems();
            }
        };
        _api = api;
        _plugins = plugins;
        _catalog = catalog;
        _settings = clientSettings;
        _logger = logger;
        IsNavExpanded = clientSettings.Current.NavRailExpanded;

        // Top: Devices, then one entry per core plugin page (added on connect).
        // Bottom: Users and Credentials (administrators only), Logs, Settings, About.
        _devicesItem = new NavItemViewModel(HostPages.Devices, "Devices", "devices", devices);
        NavItems.Add(_devicesItem);
        _usersItem = new NavItemViewModel(HostPages.Users, "Users", "users", users);
        _credentialsItem = new NavItemViewModel(HostPages.Credentials, "Credentials", "key", credentials);
        _logsItem = new NavItemViewModel(HostPages.Logs, "Logs", "logs", logs);
        _settingsItem = new NavItemViewModel(HostPages.Settings, "Settings", "settings", settings);
        _aboutItem = new NavItemViewModel(HostPages.About, "About", "info", about);
        SyncBottomNavItems();

        connection.Connected += (_, _) => _ = OnConnectedAsync();
        devices.NavigateRequested += (_, key) => Navigate(NavItems.Concat(BottomNavItems).FirstOrDefault(n => n.Key == key));
        Navigate(_devicesItem);
    }

    public ServerConnection Connection { get; }
    public DevicesViewModel Devices { get; }

    /// <summary>The logged-in user (bottom of the rail, with Log out).</summary>
    public UserSession Session { get; }

    /// <summary>"Logged in as anna (Administrator) on https://localhost:5080".</summary>
    public string UserTooltip => Session.IsLoggedIn
        ? $"Logged in as {Session.UserName} ({Session.RoleText}) on {Session.ServerAddress}"
        : "Not logged in";

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

    partial void OnIsNavExpandedChanged(bool value)
    {
        _settings.Current.NavRailExpanded = value;
        _settings.Save();
    }

    [RelayCommand]
    private void ToggleNav() => IsNavExpanded = !IsNavExpanded;

    /// <summary>Starts the server streams. Call after the window is shown (again after every login).</summary>
    public void Start()
    {
        Navigate(_devicesItem);
        Connection.Start();
    }

    /// <summary>Logout: stops the streams and closes the live view.</summary>
    public void Stop()
    {
        Connection.Stop();
        Devices.LiveView.CloseCommand.Execute(null);
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
    }

    /// <summary>
    /// The bottom group follows the role of the logged-in user: administrators see Users and Credentials, operators
    /// do not (the server refuses those calls for them). A hidden page that was open falls back to Devices.
    /// </summary>
    private void SyncBottomNavItems()
    {
        NavItemViewModel[] wanted = Session.IsAdmin
            ? [_usersItem, _credentialsItem, _logsItem, _settingsItem, _aboutItem]
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
        }
        catch (Exception ex)
        {
            LogKeyNoticeFailed(_logger, ex.Message);
        }
    }

    /// <summary>One navigation entry per core plugin with a page (<c>no_page</c> unset), below "Devices".</summary>
    internal void SyncCorePluginPages(IReadOnlyList<CorePluginInfo> corePlugins)
    {
        corePlugins = [.. corePlugins.Where(p => !p.NoPage)];
        foreach (NavItemViewModel stale in NavItems.Where(n => n.Page is CorePluginPageViewModel && !corePlugins.Any(p => "plugin:" + p.Id == n.Key)).ToList())
        {
            NavItems.Remove(stale);
            if (stale.IsSelected)
            {
                Navigate(_devicesItem);
            }
        }

        bool first = !NavItems.Any(n => n.Page is CorePluginPageViewModel);
        foreach (CorePluginInfo plugin in corePlugins.Where(p => NavItems.All(n => n.Key != "plugin:" + p.Id)))
        {
            object? view = null;
            string? error = null;
            ICorePluginPage? page = _plugins.FindPage(plugin.Id);
            if (page is not null)
            {
                try
                {
                    view = page.CreateView(new CorePluginClientContext(_api, plugin.Id, Devices.ToolbarContext));
                }
                catch (Exception ex)
                {
                    // The page shows the error instead of its view; the client keeps running (production hardening 4).
                    LogPageFailed(_logger, ex, plugin.Id);
                    error = ex.Message;
                    _ = ReportPageFailureAsync(plugin.DisplayName, ex);
                }
            }

            NavItems.Add(new NavItemViewModel("plugin:" + plugin.Id, plugin.DisplayName,
                string.IsNullOrEmpty(plugin.IconKey) ? "plugin" : plugin.IconKey,
                new CorePluginPageViewModel(plugin.Id, plugin.DisplayName, view, page?.HasOwnCards == true,
                    page?.ShowTasksPane == true && view is not null ? Devices.Tasks : null, error))
            {
                HasSeparatorBefore = first,
            });
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
}

/// <summary>
/// Bridges a core plugin page to PluginService.Invoke; devices, selection, dialogs and host pages come from the
/// Devices page's <see cref="IToolbarContext"/> (same data and dialogs as toolbar plugins).
/// </summary>
internal sealed class CorePluginClientContext(IOadmApi api, string pluginId, IToolbarContext host) : ICorePluginClientContext
{
    public IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> Devices => host.Devices;

    public IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> SelectedDevices => host.SelectedDevices;

    public event EventHandler? DevicesChanged
    {
        add => host.DevicesChanged += value;
        remove => host.DevicesChanged -= value;
    }

    public string OwnerName => TaskPluginRunner.OwnerName;

    public Avalonia.Controls.Window? Owner => host.Owner;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        api.InvokeCorePluginAsync(pluginId, method, payloadJson, ct);

    public async IAsyncEnumerable<Oadm.Sdk.Plugins.PluginEvent> WatchEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in api.WatchCorePluginAsync(pluginId, ct).ConfigureAwait(true))
        {
            yield return new Oadm.Sdk.Plugins.PluginEvent(item.Topic, string.IsNullOrEmpty(item.PayloadJson) ? null : item.PayloadJson);
        }
    }

    public Task ShowMessageAsync(string title, string message) => host.ShowMessageAsync(title, message);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => host.ConfirmAsync(title, message, confirmText);

    public Task OpenAsync(string hostPage) => host.OpenAsync(hostPage);
}
