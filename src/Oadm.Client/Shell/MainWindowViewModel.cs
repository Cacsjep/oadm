using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Plugins;
using Oadm.Client.Settings;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Shell;

/// <summary>Shell: navigation rail, current page, connection state.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IOadmApi _api;
    private readonly IClientPluginRegistry _plugins;
    private readonly TaskPluginCatalog _catalog;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly NavItemViewModel _devicesItem;
    private readonly NavItemViewModel _tasksItem;

    public MainWindowViewModel(
        ServerConnection connection,
        DevicesViewModel devices,
        TasksViewModel tasks,
        SettingsViewModel settings,
        TaskPluginCatalog catalog,
        IClientPluginRegistry plugins,
        IOadmApi api,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(api);
        Connection = connection;
        Devices = devices;
        Tasks = tasks;
        _api = api;
        _plugins = plugins;
        _catalog = catalog;
        _logger = logger;

        _devicesItem = new NavItemViewModel("devices", "Manage devices", "devices", devices);
        _tasksItem = new NavItemViewModel("tasks", "Tasks", "tasks", tasks) { HasSeparatorBefore = true };
        NavItems.Add(_devicesItem);
        NavItems.Add(_tasksItem);
        BottomNavItems.Add(new NavItemViewModel("settings", "Settings", "settings", settings));
        BottomNavItems.Add(new NavItemViewModel("about", "About", "about", new AboutViewModel(api.ServerAddress)));

        tasks.PropertyChanged += OnTasksPropertyChanged;
        connection.Connected += (_, _) => _ = OnConnectedAsync();
        UpdateTaskBadge();
        Navigate(_devicesItem);
    }

    public ServerConnection Connection { get; }
    public DevicesViewModel Devices { get; }
    public TasksViewModel Tasks { get; }

    public static string AppName => "OADM";

    public ObservableCollection<NavItemViewModel> NavItems { get; } = [];
    public ObservableCollection<NavItemViewModel> BottomNavItems { get; } = [];

    [ObservableProperty] public partial object? CurrentPage { get; private set; }

    [ObservableProperty] public partial NavItemViewModel? CurrentItem { get; private set; }

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

    private void OnTasksPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TasksViewModel.ActiveCount))
        {
            UpdateTaskBadge();
        }
    }

    private void UpdateTaskBadge() =>
        _tasksItem.Badge = Tasks.ActiveCount > 0 ? Tasks.ActiveCount.ToString(CultureInfo.CurrentCulture) : null;

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

    /// <summary>One navigation entry per core plugin, after "Tasks".</summary>
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
