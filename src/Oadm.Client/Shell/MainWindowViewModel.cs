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

    public MainWindowViewModel(
        ServerConnection connection,
        DevicesViewModel devices,
        LogsViewModel logs,
        SettingsViewModel settings,
        TaskPluginCatalog catalog,
        IClientPluginRegistry plugins,
        IOadmApi api,
        IClientSettingsStore clientSettings,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clientSettings);
        Connection = connection;
        Devices = devices;
        _api = api;
        _plugins = plugins;
        _catalog = catalog;
        _settings = clientSettings;
        _logger = logger;
        IsNavExpanded = clientSettings.Current.NavRailExpanded;

        // Top: Devices, then one entry per core plugin page (added on connect). Bottom: Logs, Settings.
        _devicesItem = new NavItemViewModel("devices", "Devices", "devices", devices);
        NavItems.Add(_devicesItem);
        BottomNavItems.Add(new NavItemViewModel("logs", "Logs", "logs", logs));
        BottomNavItems.Add(new NavItemViewModel("settings", "Settings", "settings", settings));

        connection.Connected += (_, _) => _ = OnConnectedAsync();
        Navigate(_devicesItem);
    }

    public ServerConnection Connection { get; }
    public DevicesViewModel Devices { get; }

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

    partial void OnIsNavExpandedChanged(bool value)
    {
        _settings.Current.NavRailExpanded = value;
        _settings.Save();
    }

    [RelayCommand]
    private void ToggleNav() => IsNavExpanded = !IsNavExpanded;

    /// <summary>Starts the server streams. Call once after the window is shown.</summary>
    public void Start() => Connection.Start();

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
    }

    /// <summary>One navigation entry per core plugin, below "Devices".</summary>
    internal void SyncCorePluginPages(IReadOnlyList<CorePluginInfo> corePlugins)
    {
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
            ICorePluginPage? page = _plugins.FindPage(plugin.Id);
            if (page is not null)
            {
                try
                {
                    view = page.CreateView(new CorePluginClientContext(_api, plugin.Id));
                }
                catch (Exception ex)
                {
                    LogPageFailed(_logger, ex, plugin.Id);
                }
            }

            NavItems.Add(new NavItemViewModel("plugin:" + plugin.Id, plugin.DisplayName,
                string.IsNullOrEmpty(plugin.IconKey) ? "plugin" : plugin.IconKey,
                new CorePluginPageViewModel(plugin.Id, plugin.DisplayName, view))
            {
                HasSeparatorBefore = first,
            });
            first = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list core plugins: {Reason}")]
    private static partial void LogCorePluginsFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Core plugin page {PluginId} failed to create its view")]
    private static partial void LogPageFailed(ILogger logger, Exception ex, string pluginId);
}

/// <summary>Bridges a core plugin page to PluginService.Invoke.</summary>
internal sealed class CorePluginClientContext(IOadmApi api, string pluginId) : ICorePluginClientContext
{
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        api.InvokeCorePluginAsync(pluginId, method, payloadJson, ct);
}
