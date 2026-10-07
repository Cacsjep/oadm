using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

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

/// <summary>The "Manage devices" page: toolbar, search, device grid, context menu.</summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly DeviceStore _store;
    private readonly TaskPluginCatalog _catalog;
    private readonly TaskPluginRunner _runner;
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IUrlLauncher _launcher;
    private readonly Func<AddDevicesMode, AddDevicesWizardViewModel> _wizardFactory;
    private readonly ILogger<DevicesViewModel> _logger;

    public DevicesViewModel(
        DeviceStore store,
        TaskPluginCatalog catalog,
        TaskPluginRunner runner,
        IOadmApi api,
        IDialogService dialogs,
        IUrlLauncher launcher,
        Func<AddDevicesMode, AddDevicesWizardViewModel> wizardFactory,
        ColumnLayoutViewModel columns,
        TasksViewModel tasks,
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
        _wizardFactory = wizardFactory;
        _logger = logger;
        Columns = columns;
        Tasks = tasks;

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

    public ColumnLayoutViewModel Columns { get; }
    public TasksViewModel Tasks { get; }

    /// <summary>Rows shown in the grid (search applied). Sorting is done by the grid.</summary>
    public ObservableCollection<DeviceRowViewModel> FilteredDevices { get; } = [];

    /// <summary>Kept in sync with the grid selection by the view.</summary>
    public ObservableCollection<DeviceRowViewModel> SelectedDevices { get; } = [];

    /// <summary>Task plugins with ShowInToolbar.</summary>
    public ObservableCollection<TaskPluginInfo> ToolbarActions { get; } = [];

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

    [RelayCommand]
    private Task AddDevicesAsync() => RunWizardAsync(AddDevicesMode.ZeroConf);

    [RelayCommand]
    private Task AddFromRangeAsync() => RunWizardAsync(AddDevicesMode.IpRange);

    private bool HasSelection => SelectedDevices.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        List<DeviceRowViewModel> selected = SelectedDevices.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        string what = selected.Count == 1 ? $"the device {selected[0].DisplayAddress} ({selected[0].Serial})" : $"{selected.Count} devices";
        if (!await _dialogs.ConfirmAsync("Remove devices", $"Remove {what} from OADM? The devices themselves are not changed.", "Remove").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.RemoveDevicesAsync(selected.Select(d => d.Id).ToList(), CancellationToken.None).ConfigureAwait(true);
            LogRemoved(_logger, selected.Count);
        }
        catch (Exception ex)
        {
            LogActionFailed(_logger, ex, "remove");
            await _dialogs.ShowMessageAsync("Remove devices", "The devices could not be removed: " + ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>Refreshes the selected devices, or all devices when nothing is selected.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        List<string> ids = (SelectedDevices.Count > 0 ? SelectedDevices : _store.Devices).Select(d => d.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await _api.RefreshDevicesAsync(ids, CancellationToken.None).ConfigureAwait(true);
            LogRefreshed(_logger, ids.Count);
        }
        catch (Exception ex)
        {
            LogActionFailed(_logger, ex, "refresh");
            await _dialogs.ShowMessageAsync("Refresh", "The devices could not be refreshed: " + ex.Message).ConfigureAwait(true);
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

        string? taskId = await _runner.RunAsync(plugin, SelectedDevices.ToList(), CancellationToken.None).ConfigureAwait(true);
        if (taskId is not null)
        {
            Tasks.ShowTasksCommand.Execute(null);
        }
    }

    private bool CanRunPlugin(TaskPluginInfo? plugin) =>
        plugin is not null && TaskPluginCatalog.RunnableFor([plugin], SelectedIds()).Any();

    // ------------------------------------------------------------ internals

    private async Task RunWizardAsync(AddDevicesMode mode)
    {
        AddDevicesWizardViewModel wizard = _wizardFactory(mode);
        await using (wizard.ConfigureAwait(true))
        {
            CommitReply? reply = await _dialogs.ShowAddDevicesWizardAsync(wizard).ConfigureAwait(true);
            if (reply is not null)
            {
                Tasks.ShowTasksCommand.Execute(null);
            }
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
        ToolbarActions.Clear();
        foreach (TaskPluginInfo plugin in _catalog.Plugins.Where(p => p.ShowInToolbar))
        {
            ToolbarActions.Add(plugin);
        }

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
        ContextMenuEntries.Add(new MenuEntryViewModel { Header = "Refresh", IconKey = "refresh", Command = RefreshCommand });
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Refresh requested for {Count} device(s)")]
    private static partial void LogRefreshed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not {Action} devices")]
    private static partial void LogActionFailed(ILogger logger, Exception ex, string action);
}
