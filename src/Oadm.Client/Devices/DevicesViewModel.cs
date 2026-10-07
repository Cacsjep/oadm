using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices.Toolbar;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Client.LiveView;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Devices;

/// <summary>Entry of the device context menu. Header "-" renders as a separator.</summary>
public sealed class MenuEntryViewModel
{
    public required string Header { get; init; }
    public string? IconKey { get; init; }
    public System.Windows.Input.ICommand? Command { get; init; }
    public object? CommandParameter { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsSeparator => Header == "-";

    public static MenuEntryViewModel Separator() => new() { Header = "-" };
}

/// <summary>
/// The "Manage devices" page: toolbar (toolbar plugins, see <see cref="DeviceToolbar"/>), search,
/// device grid, context menu. Opens the host pages the toolbar asks for (add page, navigation).
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly DeviceStore _store;
    private readonly TaskPluginCatalog _catalog;
    private readonly TaskPluginRunner _runner;
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IUrlLauncher _launcher;
    private readonly Func<AddDevicesMode, AddDevicesViewModel> _addPageFactory;
    private readonly ILogger<DevicesViewModel> _logger;

    public DevicesViewModel(
        DeviceStore store,
        TaskPluginCatalog catalog,
        TaskPluginRunner runner,
        IOadmApi api,
        IDialogService dialogs,
        IUrlLauncher launcher,
        Func<AddDevicesMode, AddDevicesViewModel> addPageFactory,
        DeviceToolbar toolbar,
        ColumnLayoutViewModel columns,
        TasksViewModel tasks,
        LiveViewViewModel liveView,
        ILogger<DevicesViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        _store = store;
        _catalog = catalog;
        _runner = runner;
        _api = api;
        _dialogs = dialogs;
        _launcher = launcher;
        _addPageFactory = addPageFactory;
        _logger = logger;
        Toolbar = toolbar;
        ToolbarContext = new ToolbarContext(store, SelectedDevices, catalog, runner, api, dialogs, OpenHostPageAsync, () => tasks.ShowTasksCommand.Execute(null));
        Columns = columns;
        Tasks = tasks;
        LiveView = liveView;

        store.Devices.CollectionChanged += OnDevicesChanged;
        store.Changed += (_, _) => catalog.RequestRefresh();
        catalog.Changed += (_, _) => RebuildPluginActions();
        SelectedDevices.CollectionChanged += (_, _) => OnSelectionChanged();
        foreach (DeviceRowViewModel row in store.Devices)
        {
            FilteredDevices.Add(row);
        }

        RebuildPluginActions();
    }

    /// <summary>Toolbar plugins of the page; the view attaches their controls.</summary>
    public DeviceToolbar Toolbar { get; }

    /// <summary>What toolbar plugins see and can do.</summary>
    public ToolbarContext ToolbarContext { get; }

    /// <summary>A toolbar plugin asked for a navigation page (<see cref="HostPages"/> key); the shell navigates.</summary>
    public event EventHandler<string>? NavigateRequested;

    public ColumnLayoutViewModel Columns { get; }
    public TasksViewModel Tasks { get; }

    /// <summary>Live view side panel, opened from the device icon.</summary>
    public LiveViewViewModel LiveView { get; }

    /// <summary>Rows shown in the grid (search applied). Sorting is done by the grid.</summary>
    public ObservableCollection<DeviceRowViewModel> FilteredDevices { get; } = [];

    /// <summary>Kept in sync with the grid selection by the view.</summary>
    public ObservableCollection<DeviceRowViewModel> SelectedDevices { get; } = [];

    /// <summary>Context menu for the current selection: core actions plus runnable task plugins.</summary>
    public ObservableCollection<MenuEntryViewModel> ContextMenuEntries { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public string StatusLine =>
        string.Create(CultureInfo.CurrentCulture, $"{_store.Devices.Count} devices, {SelectedDevices.Count} selected");

    public bool HasSearch => SearchText.Length > 0;

    public bool IsEmpty => _store.Devices.Count == 0;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearch));
        ApplyFilter();
    }

    // ------------------------------------------------------------ commands

    /// <summary>Opens the add page in a mode (the Scan, Scan IP range and Add manually toolbar plugins).</summary>
    [RelayCommand]
    private Task OpenAddPageAsync(AddDevicesMode mode) => RunAddPageAsync(mode);

    private bool HasSelection => SelectedDevices.Count > 0;

    /// <summary>Context menu "Remove": the same flow as the Remove toolbar plugin.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        int count = SelectedDevices.Count;
        if (await RemoveToolbarPlugin.ConfirmAndRemoveAsync(ToolbarContext).ConfigureAwait(true))
        {
            LogRemoved(_logger, count);
        }
    }

    /// <summary>Host pages for <see cref="IToolbarContext.OpenAsync"/>.</summary>
    internal Task OpenHostPageAsync(string hostPage)
    {
        switch (hostPage)
        {
            case HostPages.AddScan:
                return RunAddPageAsync(AddDevicesMode.Scan);
            case HostPages.AddIpRange:
                return RunAddPageAsync(AddDevicesMode.IpRange);
            case HostPages.AddManually:
                return RunAddPageAsync(AddDevicesMode.Manual);
            default:
                NavigateRequested?.Invoke(this, hostPage);
                return Task.CompletedTask;
        }
    }

    [RelayCommand]
    private async Task OpenWebUiAsync(DeviceRowViewModel? device)
    {
        device ??= SelectedDevices.FirstOrDefault();
        if (device is null)
        {
            return;
        }

        try
        {
            string url = await _api.GetWebUiUrlAsync(device.Id, CancellationToken.None).ConfigureAwait(true);
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await _launcher.OpenAsync(uri).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            LogActionFailed(_logger, ex, "open web interface");
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand(CanExecute = nameof(CanRunPlugin))]
    private async Task RunPluginAsync(TaskPluginInfo? plugin)
    {
        if (plugin is null)
        {
            return;
        }

        IReadOnlyList<string>? taskIds = await _runner.RunAsync(plugin, SelectedDevices.ToList(), CancellationToken.None).ConfigureAwait(true);
        if (taskIds is { Count: > 0 })
        {
            Tasks.ShowTasksCommand.Execute(null);
        }
    }

    private bool CanRunPlugin(TaskPluginInfo? plugin) =>
        plugin is not null && TaskPluginCatalog.RunnableFor([plugin], SelectedIds()).Any();

    // ------------------------------------------------------------ internals

    private async Task RunAddPageAsync(AddDevicesMode mode)
    {
        AddDevicesViewModel page = _addPageFactory(mode);
        await using (page.ConfigureAwait(true))
        {
            await _dialogs.ShowAddDevicesAsync(page).ConfigureAwait(true);
        }
    }

    private List<string> SelectedIds() => SelectedDevices.Select(d => d.Id).ToList();

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (DeviceRowViewModel row in e.NewItems.OfType<DeviceRowViewModel>().Where(r => r.Matches(SearchText)))
                {
                    FilteredDevices.Add(row);
                }

                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (DeviceRowViewModel row in e.OldItems.OfType<DeviceRowViewModel>())
                {
                    FilteredDevices.Remove(row);
                    SelectedDevices.Remove(row);
                    if (LiveView.Device?.Id == row.Id)
                    {
                        LiveView.CloseCommand.Execute(null);
                    }
                }

                break;
            default:
                ApplyFilter();
                break;
        }

        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyFilter()
    {
        var matching = _store.Devices.Where(d => d.Matches(SearchText)).ToList();
        foreach (DeviceRowViewModel row in FilteredDevices.Where(r => !matching.Contains(r)).ToList())
        {
            FilteredDevices.Remove(row);
        }

        foreach (DeviceRowViewModel row in matching.Where(r => !FilteredDevices.Contains(r)))
        {
            FilteredDevices.Add(row);
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(StatusLine));
        RemoveCommand.NotifyCanExecuteChanged();
        RunPluginCommand.NotifyCanExecuteChanged();
        RebuildContextMenu();
    }

    private void RebuildPluginActions()
    {
        RunPluginCommand.NotifyCanExecuteChanged();
        RebuildContextMenu();
    }

    /// <summary>Core actions, then every task plugin whose CanRun is true for the whole selection.</summary>
    internal void RebuildContextMenu()
    {
        ContextMenuEntries.Clear();
        int count = SelectedDevices.Count;
        if (count == 0)
        {
            return;
        }

        ContextMenuEntries.Add(new MenuEntryViewModel
        {
            Header = "Open web interface",
            IconKey = "externalLink",
            Command = OpenWebUiCommand,
            IsEnabled = count == 1,
        });
        ContextMenuEntries.Add(new MenuEntryViewModel { Header = "Remove", IconKey = "remove", Command = RemoveCommand });

        var runnable = TaskPluginCatalog.RunnableFor(_catalog.Plugins, SelectedIds()).ToList();
        if (runnable.Count > 0)
        {
            ContextMenuEntries.Add(MenuEntryViewModel.Separator());
            foreach (TaskPluginInfo plugin in runnable)
            {
                ContextMenuEntries.Add(new MenuEntryViewModel
                {
                    Header = plugin.RequiresDialog ? plugin.DisplayName + "..." : plugin.DisplayName,
                    IconKey = string.IsNullOrEmpty(plugin.IconKey) ? "plugin" : plugin.IconKey,
                    Command = RunPluginCommand,
                    CommandParameter = plugin,
                });
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} device(s)")]
    private static partial void LogRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not {Action} devices")]
    private static partial void LogActionFailed(ILogger logger, Exception ex, string action);
}
