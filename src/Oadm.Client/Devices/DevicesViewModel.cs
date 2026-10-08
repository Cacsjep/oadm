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
using Oadm.Client.Infrastructure;
using Oadm.Client.LiveView;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Devices;

/// <summary>Entry of the device context menu. Header "-" renders as a separator.</summary>
public sealed class MenuEntryViewModel
{
    public required string Header { get; init; }
    public string? IconKey { get; init; }
    public System.Windows.Input.ICommand? Command { get; init; }
    public object? CommandParameter { get; init; }
    public bool IsEnabled { get; init; } = true;

    /// <summary>Submenu entries (task groups); null for a plain entry.</summary>
    public IReadOnlyList<MenuEntryViewModel>? Items { get; init; }

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
        FilteredDevices.ReplaceAll(store.Devices);

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
    public RangeObservableCollection<DeviceRowViewModel> FilteredDevices { get; } = [];

    /// <summary>Kept in sync with the grid selection by the view.</summary>
    public RangeObservableCollection<DeviceRowViewModel> SelectedDevices { get; } = [];

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

    /// <summary>Context menu "Refresh": the same flow as the Refresh toolbar plugin (one call for the selection).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RefreshAsync() => RefreshToolbarPlugin.RefreshSelectionAsync(ToolbarContext);

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
            case HostPages.ExportDevices:
                return ExportDevicesAsync();
            case HostPages.AddImport:
                return ImportDevicesAsync();
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

    /// <summary>CSV files of the export and the import.</summary>
    internal static readonly FileType CsvFile = new("CSV file", "csv", "text/csv");

    /// <summary>
    /// Export devices: the selected devices, or every device the search shows when none is selected, as
    /// CSV (<see cref="DeviceListCsv"/>), in the grid's unsorted order. O(n).
    /// </summary>
    internal async Task ExportDevicesAsync()
    {
        IReadOnlyList<DeviceRowViewModel> rows = ExportRows();
        if (rows.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Export devices", "There are no devices to export.").ConfigureAwait(true);
            return;
        }

        try
        {
            byte[] content = DeviceListCsv.ToBytes(rows);
            string? saved = await _dialogs.SaveFileAsync("Export devices", DeviceListCsv.DefaultFileName(DateTime.Now), CsvFile, content).ConfigureAwait(true);
            if (saved is not null)
            {
                LogExported(_logger, rows.Count, saved);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogActionFailed(_logger, ex, "export");
            await _dialogs.ShowMessageAsync("Export devices", "The file could not be saved: " + ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Import devices: asks for a CSV file (<see cref="DeviceImportFile"/>) and opens the add page with
    /// every address of it. A file that cannot be used at all is explained in the message window; problems
    /// of single lines show in their rows.
    /// </summary>
    internal async Task ImportDevicesAsync()
    {
        DeviceImportFile file;
        try
        {
            PickedFile? picked = await _dialogs.OpenFileAsync("Import devices", CsvFile, DeviceImportFile.MaxBytes).ConfigureAwait(true);
            if (picked is null)
            {
                return;
            }

            file = DeviceImportFile.Parse(picked.Name, picked.Content, picked.IsTooLarge);
        }
        catch (DeviceImportException ex)
        {
            await _dialogs.ShowMessageAsync("Import devices", ex.Message).ConfigureAwait(true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync("Import devices", "The file could not be read: " + ex.Message).ConfigureAwait(true);
            return;
        }

        LogImporting(_logger, file.Lines.Count, file.FileName);
        AddDevicesViewModel page = _addPageFactory(AddDevicesMode.Import);
        page.SetImport(file);
        await using (page.ConfigureAwait(true))
        {
            await _dialogs.ShowAddDevicesAsync(page).ConfigureAwait(true);
        }
    }

    /// <summary>The devices an export writes: the selection when there is one, else the search result.</summary>
    internal IReadOnlyList<DeviceRowViewModel> ExportRows()
    {
        if (SelectedDevices.Count == 0)
        {
            return [.. FilteredDevices];
        }

        var selected = SelectedDevices.ToHashSet();
        return _store.Devices.Where(selected.Contains).ToList();
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
                // Reset (a snapshot or a batch of several adds/removes): filter again, drop removed rows from the selection.
                ApplyFilter();
                RemoveGoneFromSelection();
                break;
        }

        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Search over all devices: O(n) matching and at most one collection notification (Reset), none when
    /// the result did not change. The former version used List.Contains inside loops (O(n^2), 25 million
    /// comparisons for 5,000 devices) and one event per added or removed row.
    /// </summary>
    internal void ApplyFilter()
    {
        string search = SearchText;
        FilteredDevices.ReplaceAll(string.IsNullOrWhiteSpace(search)
            ? _store.Devices
            : _store.Devices.Where(d => d.Matches(search)).ToList());
    }

    private void RemoveGoneFromSelection()
    {
        if (SelectedDevices.Count > 0)
        {
            SelectedDevices.RemoveAll(row => _store.Find(row.Id) != row);
        }

        if (LiveView.Device is { } shown && _store.Find(shown.Id) is null)
        {
            LiveView.CloseCommand.Execute(null);
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(StatusLine));
        RemoveCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
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
        ContextMenuEntries.Add(new MenuEntryViewModel { Header = "Refresh", IconKey = "refresh", Command = RefreshCommand });
        ContextMenuEntries.Add(new MenuEntryViewModel { Header = "Remove", IconKey = "remove", Command = RemoveCommand });

        var runnable = TaskPluginCatalog.RunnableFor(_catalog.Plugins, SelectedIds()).ToList();
        if (runnable.Count > 0)
        {
            ContextMenuEntries.Add(MenuEntryViewModel.Separator());
            foreach (MenuEntryViewModel group in TaskMenuGroups(runnable, RunPluginCommand))
            {
                ContextMenuEntries.Add(group);
            }
        }
    }

    /// <summary>
    /// One submenu per task group (sorted by name), even with a single entry (user decision); inside, the
    /// tasks sorted by name with their icons. Names never end with "..." (stripped defensively) and are
    /// shortened to <see cref="TaskPluginNames.MaxDisplayNameLength"/> characters.
    /// </summary>
    internal static IEnumerable<MenuEntryViewModel> TaskMenuGroups(IEnumerable<TaskPluginInfo> plugins, System.Windows.Input.ICommand run) =>
        plugins
            .GroupBy(p => TaskPluginNames.NormalizeGroup(p.Group), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new MenuEntryViewModel
            {
                Header = g.Key,
                IconKey = GroupIconKey(g.Key),
                Items = g
                    .Select(p => new MenuEntryViewModel
                    {
                        Header = TaskPluginNames.Normalize(p.DisplayName),
                        IconKey = string.IsNullOrEmpty(p.IconKey) ? "plugin" : p.IconKey,
                        Command = run,
                        CommandParameter = p,
                    })
                    .OrderBy(e => e.Header, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
            });

    /// <summary>Icon of a well-known group; other groups show the plugin icon.</summary>
    internal static string GroupIconKey(string group) => group switch
    {
        TaskGroups.Applications => "app",
        TaskGroups.Maintenance => "settings",
        TaskGroups.Network => "network",
        TaskGroups.Security => "key",
        TaskGroups.Users => "users",
        TaskGroups.Video => "video",
        _ => "plugin",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} device(s)")]
    private static partial void LogRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Importing {Count} address line(s) from {FileName}")]
    private static partial void LogImporting(ILogger logger, int count, string fileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exported {Count} device(s) to {FileName}")]
    private static partial void LogExported(ILogger logger, int count, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not {Action} devices")]
    private static partial void LogActionFailed(ILogger logger, Exception ex, string action);
}
