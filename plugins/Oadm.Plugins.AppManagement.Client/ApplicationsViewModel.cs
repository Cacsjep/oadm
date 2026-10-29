using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Acap;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>The page's windows (the view implements it, so the view model stays testable).</summary>
public interface IApplicationDialogs
{
    Task ShowLogAsync(AppLogViewModel log);

    Task ShowSettingsAsync(AppSettingsViewModel settings);

    /// <summary>Install applications; true when the tasks were started.</summary>
    Task<bool> ShowInstallAsync(InstallAppsViewModel install);

    /// <summary>Install licenses; true when the tasks were started.</summary>
    Task<bool> ShowLicensesAsync(InstallLicensesViewModel licenses);
}

/// <summary>
/// The ⋯ menu of an application row besides Show log: Settings, or else Open app page, or neither. <paramref name="WebViewOff"/>:
/// the application has its own page but Web View is off, so Open app page shows greyed out with
/// <see cref="AppSettingsViewModel.WebViewOffTip"/>.
/// </summary>
public sealed record AppRowMenu(bool Settings, bool AppPage, bool WebViewOff = false);

/// <summary>One entry of the status filter.</summary>
public sealed record StatusChoice(AppStatusFilter Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Page "Applications" (docs/spec/app-management.md). Nothing is read before a device choice (<see cref="DeviceChoice"/>):
/// the chosen devices are read when the page is shown with a choice and the server has no rows of this session yet, after
/// a new choice and on Refresh. The rows of the server arrive as events and pages (<see cref="AppIndex"/>, whole devices);
/// the device grid and the applications of the selected devices are rebuilt from the index in one pass per change. The actions never write themselves: Install and Install
/// licenses open the page's own dialogs (<see cref="InstallAppsViewModel"/>, <see cref="InstallLicensesViewModel"/>), which, like Start, Stop and Remove, ask
/// the server part to start the Applications plugin's tasks.
/// </summary>
public sealed partial class ApplicationsViewModel : ObservableObject, IDisposable
{
    public const string ChooseFirst = "Choose devices to see their applications.";
    public const string Reading = "Reading the applications.";
    public const string NothingMatches = "No application matches the filters.";
    public const string NoViewRight = "Your role cannot view applications.";

    public static IReadOnlyList<StatusChoice> StatusChoices { get; } =
    [
        new(AppStatusFilter.All, "All"),
        new(AppStatusFilter.NotRunning, "Not running"),
        new(AppStatusFilter.LicenseMissing, "License missing"),
        new(AppStatusFilter.LicenseExpiresSoon, "License expires soon"),
        new(AppStatusFilter.ReadFailed, "Read failed"),
    ];

    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private readonly AppIndex _index = new();
    private CancellationTokenSource? _active;
    private IReadOnlyList<AppRow> _selectedRows = [];
    private readonly List<Guid> _selectedDevices = [];
    private readonly HashSet<Guid> _selectedDeviceSet = [];
    private bool _rebuilding; // the grids reset: their emptied selections are not the user's
    private readonly Dictionary<(Guid Device, string App), bool> _hasSettings = []; // learned when a row's menu opened
    private ReadState? _state;

    public ApplicationsViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
        DeviceChoice = new PageDeviceChoice(ctx);
        DeviceChoice.PropertyChanged += (_, _) =>
        {
            RefreshCommand.NotifyCanExecuteChanged();
            UpdateSummary();
        };
        ctx.PermissionsChanged += OnPermissionsChanged;
    }

    /// <summary>Wait before watching the events again after the stream broke.</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the page asks for the progress while a read runs (its events may be missed).</summary>
    public static TimeSpan StatePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The devices the page reads (the command button first in the toolbar).</summary>
    public PageDeviceChoice DeviceChoice { get; }

    /// <summary>Opens the device picker (set by the view); true when a choice was made.</summary>
    public Func<Task<bool>>? PickDevices { get; set; }

    /// <summary>Opens the log and settings windows (set by the view).</summary>
    public IApplicationDialogs? Dialogs { get; set; }

    /// <summary>Every shown row: one per device and application (the source of the device grid).</summary>
    public RangeObservableCollection<AppRow> DeviceRows { get; } = [];

    /// <summary>The upper grid: one row per device with shown rows.</summary>
    public RangeObservableCollection<AppDeviceRow> Devices { get; } = [];

    /// <summary>The lower grid: the applications of the selected devices.</summary>
    public RangeObservableCollection<AppRow> SelectedApps { get; } = [];

    /// <summary>The device grid was rebuilt (the view selects <see cref="SelectedDeviceIds"/> again).</summary>
    public event EventHandler? DevicesRebuilt;

    /// <summary>The selected devices of the upper grid, kept over a rebuild.</summary>
    public IReadOnlyCollection<Guid> SelectedDeviceIds => _selectedDeviceSet;

    public const string SelectDevicesFirst = "Select devices to see their applications.";

    /// <summary>The line in the empty lower grid: what to do first, or that the search matches nothing; null while it has rows.</summary>
    public string? AppsEmptyText => SelectedApps.Count > 0 ? null
        : _selectedDevices.Count == 0 ? SelectDevicesFirst
        : string.IsNullOrWhiteSpace(AppsSearchText) ? AppRow.NoApplications
        : NothingMatches;

    public bool ShowAppsEmpty => SelectedApps.Count == 0;

    /// <summary>The lower grid's search: application, vendor, version, address (the selection of the rows it hides goes).</summary>
    [ObservableProperty]
    public partial string AppsSearchText { get; set; } = string.Empty;

    partial void OnAppsSearchTextChanged(string value)
    {
        FillSelectedApps([.. _selectedRows.Select(r => (r.DeviceId, r.AppKey))]);
        DevicesRebuilt?.Invoke(this, EventArgs.Empty);
        SelectionChanged();
    }

    /// <summary>The selected devices of the upper grid (set by the view in one step); the lower grid shows their applications.</summary>
    public void SetSelectedDevices(IEnumerable<AppDeviceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_rebuilding)
        {
            return;
        }

        _selectedDevices.Clear();
        _selectedDeviceSet.Clear();
        foreach (var row in rows)
        {
            if (_selectedDeviceSet.Add(row.DeviceId))
            {
                _selectedDevices.Add(row.DeviceId);
            }
        }

        FillSelectedApps(null);
        SelectionChanged();
    }

    /// <summary>Several devices are selected: the lower grid groups its rows by device (the view does the grouping).</summary>
    [ObservableProperty]
    public partial bool GroupApps { get; private set; }

    /// <summary>The selected application rows of the lower grid (the view selects them again after a rebuild).</summary>
    public IReadOnlyList<AppRow> SelectedRows => _selectedRows;

    /// <summary>The lower grid from the selected devices; <paramref name="keep"/>: the application rows to keep selected (device, package name).</summary>
    private void FillSelectedApps(HashSet<(Guid, string?)>? keep)
    {
        var search = (AppsSearchText ?? string.Empty).Trim();
        var apps = new List<AppRow>();
        foreach (var device in Devices)
        {
            if (_selectedDeviceSet.Contains(device.DeviceId))
            {
                apps.AddRange(search.Length == 0 ? device.Rows : device.Rows.Where(r => r.AppHaystack.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }
        }

        GroupApps = _selectedDevices.Count > 1;
        _rebuilding = true; // the grid reset empties its selection: not the user's
        try
        {
            SelectedApps.ReplaceAll(apps);
        }
        finally
        {
            _rebuilding = false;
        }

        _selectedRows = keep is null ? [] : [.. apps.Where(r => keep.Contains((r.DeviceId, r.AppKey)))];
        OnPropertyChanged(nameof(AppsEmptyText));
        OnPropertyChanged(nameof(ShowAppsEmpty));
    }

    /// <summary>The first load after <see cref="Activate"/> (the session's rows, then a read when needed; tests wait for it).</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The rows the server sent (tests).</summary>
    internal AppIndex Index => _index;

    public bool MayView => _ctx.Has(PermissionKeys.Of(AppManagementInfo.PluginId, AppManagementInfo.ViewPermission));

    public bool MayInstall => MayView && _ctx.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Install));

    public bool MayLicense => MayView && _ctx.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.License));

    public bool MayStartStop => MayView && _ctx.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.StartStop));

    public bool MayRemove => MayView && _ctx.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Remove));

    public bool ShowInstallTools => MayInstall || MayLicense;

    public bool ShowRunTools => MayStartStop || MayRemove;

    /// <summary>Hides preinstalled apps and apps named AXIS (on by default).</summary>
    [ObservableProperty]
    public partial bool HideAxisApps { get; set; } = true;

    partial void OnHideAxisAppsChanged(bool value) => Rebuild();

    [ObservableProperty]
    public partial StatusChoice SelectedStatus { get; set; } = StatusChoices[0];

    partial void OnSelectedStatusChanged(StatusChoice value) => Rebuild();

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => Rebuild();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string EmptyText { get; private set; } = ChooseFirst;

    public bool ShowEmpty => Devices.Count == 0;

    /// <summary>Why the actions are disabled (tooltip), or null.</summary>
    public string? ToolsBlockedReason => TargetApplications().Count > 0 ? null : "Select applications.";

    /// <summary>Why Install is disabled (tooltip), or null.</summary>
    public string? InstallBlockedReason => TargetDevices().Count > 0 ? null : "Select devices.";

    /// <summary>The page is shown: watch the events, take the session's rows, read the choice when there are none yet.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        if (!MayView)
        {
            UpdateSummary();
            return;
        }

        _ = _ctx.RunAsync(HandleEvent, ReconnectDelay, token);
        Loading = LoadSessionAsync(token);
    }

    /// <summary>The page is hidden: no watch, no polls.</summary>
    public void Deactivate()
    {
        if (_active is not { } active)
        {
            return;
        }

        _active = null;
        active.Cancel();
        active.Dispose();
    }

    public void Dispose()
    {
        Deactivate();
        _ctx.PermissionsChanged -= OnPermissionsChanged;
    }

    /// <summary>The selected rows of the lower grid (set by the view in one step).</summary>
    public void SetDeviceSelection(IEnumerable<AppRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_rebuilding)
        {
            return;
        }

        _selectedRows = [.. rows];
        SelectionChanged();
    }

    /// <summary>The devices Install opens its dialog for: the devices selected in the upper grid (a device action; the application rows below do not narrow it).</summary>
    public IReadOnlyList<Guid> TargetDevices() => [.. _selectedDevices];

    /// <summary>Start, Stop and Remove: one item per application with the devices of its selected rows.</summary>
    public IReadOnlyList<RunItem> TargetApplications()
    {
        return
        [
            .. _selectedRows.Where(r => r.AppKey is not null)
                .GroupBy(r => r.AppKey!, StringComparer.Ordinal)
                .Select(g => new RunItem { Application = g.Key, DisplayName = g.First().AppName, DeviceIds = [.. g.Select(r => r.DeviceId).Distinct()] }),
        ];
    }

    /// <summary>A row's Show log: the application's log lines on that device.</summary>
    [RelayCommand]
    private async Task ShowLogAsync(AppRow? row)
    {
        if (row?.AppKey is not { } key || Dialogs is not { } dialogs)
        {
            return;
        }

        var log = new AppLogViewModel(_ctx, row.DeviceId, key, row.AppName, row.Address);
        _ = log.RefreshAsync();
        await dialogs.ShowLogAsync(log).ConfigureAwait(true);
        log.Dispose();
    }

    /// <summary>Settings of a row's application: on its device, or on every selected device with it when the row is selected.</summary>
    /// <summary>How long the menu waits for the device to say whether the application has settings.</summary>
    public static TimeSpan MenuProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ⋯ menu of a row: Settings when the application registered settings on the row's device, else Open app page when
    /// it has its own page (greyed out while Web View is off); Show log always. The device is asked once (one read, the settings window's)
    /// and the answer kept until the device is read again; when it cannot be asked, Settings stays (the window says more).
    /// </summary>
    public async Task<AppRowMenu> MenuForAsync(AppRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.AppKey is not { } key)
        {
            return new AppRowMenu(false, false);
        }

        if (!_hasSettings.TryGetValue((row.DeviceId, key), out var has))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MenuProbeTimeout);
            try
            {
                var json = await _ctx.InvokeAsync(AppManagementMethods.GetSettings,
                    AppManagementJson.Serialize(new SettingsRequest { Application = key, DeviceIds = [row.DeviceId] }), timeout.Token).ConfigureAwait(true);
                var devices = AppManagementJson.Deserialize<SettingsReply>(json).Devices;
                var device = devices.Count > 0 ? devices[0] : null;
                if (device is null || device.Error is not null)
                {
                    return new AppRowMenu(true, false);
                }

                has = device.Settings.Count > 0;
                _hasSettings[(row.DeviceId, key)] = has;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new AppRowMenu(true, false);
            }
#pragma warning disable CA1031 // Unknown: the menu keeps Settings, the window shows the error.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                return new AppRowMenu(true, false);
            }
        }

        if (has)
        {
            return new AppRowMenu(true, false);
        }

        return AppPageOf(row) is not null ? new AppRowMenu(false, true)
            : new AppRowMenu(false, false, row.App?.ConfigurationPath is not null && _ctx.IsPluginOn(AppSettingsViewModel.WebViewId) == false);
    }

    /// <summary>The row's application page when Web View can open it, else null.</summary>
    private string? AppPageOf(AppRow row) =>
        row.App?.ConfigurationPath is { } path && _ctx.IsPluginOn(AppSettingsViewModel.WebViewId) == true ? path : null;

    /// <summary>The application's own page on the row's device, in the device's Web View tab.</summary>
    [RelayCommand]
    private void OpenAppPage(AppRow? row)
    {
        if (row is null || AppPageOf(row) is not { } path)
        {
            return;
        }

        if (!_ctx.OpenDevicePage(row.DeviceId, path))
        {
            ErrorText = AppSettingsViewModel.AppPageNotOpened;
        }
    }

    private void ForgetSettings(IReadOnlyCollection<Guid> devices)
    {
        if (_hasSettings.Count == 0 || devices.Count == 0)
        {
            return;
        }

        var ids = devices.ToHashSet();
        foreach (var key in _hasSettings.Keys.Where(k => ids.Contains(k.Device)).ToList())
        {
            _hasSettings.Remove(key);
        }
    }

    [RelayCommand]
    private async Task ShowSettingsAsync(AppRow? row)
    {
        var settings = SettingsFor(row);
        if (settings is null || Dialogs is not { } dialogs)
        {
            return;
        }

        var load = settings.LoadAsync(_active?.Token ?? CancellationToken.None);
        await dialogs.ShowSettingsAsync(settings).ConfigureAwait(true);
        await load.ConfigureAwait(true);
    }

    /// <summary>
    /// The settings window of a row: its device, or every selected device with the row's application when the row is one
    /// of the selected rows. Null for a row without an application or too many devices (the page says so).
    /// </summary>
    internal AppSettingsViewModel? SettingsFor(AppRow? row)
    {
        if (row?.AppKey is not { } key)
        {
            return null;
        }

        IReadOnlyList<Guid> ids = _selectedRows.Contains(row)
            ? [.. _selectedRows.Where(r => string.Equals(r.AppKey, key, StringComparison.Ordinal)).Select(r => r.DeviceId).Distinct()]
            : [row.DeviceId];
        var where = ids.Count == 1 ? row.Address : Count(ids.Count, "device", "devices");
        var name = row.AppName;

        if (ids.Count > AppManagementInfo.MaxSettingsDevices)
        {
            ErrorText = AppManagementInfo.TooManySettingsDevices;
            return null;
        }

        // The application's own page as list.cgi names it (the settings window offers it for one device).
        var page = ids.Select(id => _index.Find(id)?.Applications.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.Ordinal))?.ConfigurationPath)
            .FirstOrDefault(p => p is not null);
        return new AppSettingsViewModel(_ctx, key, name, ids, where, page);
    }

    /// <summary>The device choice button: choose, then read the chosen devices.</summary>
    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (PickDevices is { } pick && await pick().ConfigureAwait(true))
        {
            await ReadAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
    }

    private bool CanRefresh => MayView && !IsReading && !DeviceChoice.IsEmpty;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReadAsync(_active?.Token ?? CancellationToken.None);

    private bool CanInstall => MayInstall && TargetDevices().Count > 0;

    private bool CanLicense => MayLicense && TargetApplications().Count > 0;

    /// <summary>Opens Install applications for the target devices (the files, the check per device, the tasks).</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (InstallDialog() is { } install && Dialogs is { } dialogs)
        {
            await dialogs.ShowInstallAsync(install).ConfigureAwait(true);
        }
    }

    /// <summary>Opens Install licenses for the selected application rows (the key files, the key per row, the tasks).</summary>
    [RelayCommand(CanExecute = nameof(CanLicense))]
    private async Task InstallLicensesAsync()
    {
        if (LicensesDialog() is { } licenses && Dialogs is { } dialogs)
        {
            await dialogs.ShowLicensesAsync(licenses).ConfigureAwait(true);
        }
    }

    /// <summary>Install applications for <see cref="TargetDevices"/> as the page read them; null without a target.</summary>
    internal InstallAppsViewModel? InstallDialog()
    {
        var devices = TargetDevices().Select(id => _index.Find(id)).OfType<DeviceApps>().ToList();
        return devices.Count == 0 ? null : new InstallAppsViewModel(_ctx, devices);
    }

    /// <summary>Install licenses for the selected application rows; null without one.</summary>
    internal InstallLicensesViewModel? LicensesDialog()
    {
        var rows = _selectedRows.Where(r => r.App is not null).ToList();
        return rows.Count == 0 ? null : new InstallLicensesViewModel(_ctx, rows, Today);
    }

    private bool CanStartStop => MayStartStop && TargetApplications().Count > 0;

    private bool CanRemove => MayRemove && TargetApplications().Count > 0;

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private Task StartAsync() => RunAsync(AcapAction.Start);

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private Task StopAsync() => RunAsync(AcapAction.Stop);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => RunAsync(AcapAction.Remove);

    /// <summary>Applies one pushed event (tests call it directly).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Topic)
        {
            case AppManagementMethods.RowsTopic:
                var rows = AppManagementJson.Deserialize<RowsEvent>(item.PayloadJson);
                ApplyRows(rows.Devices ?? [], rows.Removed);
                break;
            case AppManagementMethods.StateTopic:
                ApplyState(AppManagementJson.Deserialize<ReadState>(item.PayloadJson));
                break;
        }
    }

    /// <summary>Changed devices replace their rows, removed devices lose them; the grid is rebuilt once.</summary>
    public void ApplyRows(IReadOnlyList<DeviceApps> devices, IReadOnlyList<Guid>? removed = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ForgetSettings([.. devices.Select(d => d.DeviceId), .. removed ?? []]);
        if (_index.Apply(devices, removed, Today()))
        {
            Rebuild();
        }
    }

    /// <summary>The progress of the read.</summary>
    public void ApplyState(ReadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        IsReading = state.Reading;
        ProgressValue = state.Total == 0 ? 0 : 100.0 * state.Done / state.Total;
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"Reading devices {state.Done:N0} of {state.Total:N0}");
        UpdateSummary();
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>The session's rows first; a read only when there are none yet and devices are chosen.</summary>
    private async Task LoadSessionAsync(CancellationToken ct)
    {
        try
        {
            var state = await LoadRowsAsync(ct).ConfigureAwait(true);
            if (state is { Reading: true })
            {
                await FollowReadAsync(ct).ConfigureAwait(true);
            }
            else if (_index.Count == 0 && !DeviceChoice.IsEmpty)
            {
                await ReadAsync(ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    /// <summary>Reads the chosen devices; the rows of devices no longer chosen go at once.</summary>
    private async Task ReadAsync(CancellationToken ct)
    {
        if (!MayView)
        {
            return;
        }

        _hasSettings.Clear();
        var devices = DeviceChoice.Resolve();
        var chosen = devices.Select(d => d.Id).ToHashSet();
        if (_index.KeepOnly(chosen))
        {
            Rebuild();
        }

        if (devices.Count == 0)
        {
            _state = null;
            IsReading = false;
            UpdateSummary();
            return;
        }

        try
        {
            var request = DeviceChoice.Choice.Kind == DeviceChoiceKind.All
                ? new ReadRequest { All = true }
                : new ReadRequest { DeviceIds = [.. chosen] };
            var json = await _ctx.InvokeAsync(AppManagementMethods.Read, AppManagementJson.Serialize(request), ct).ConfigureAwait(true);
            ApplyState(AppManagementJson.Deserialize<ReadState>(json));
            ErrorText = null;
            await FollowReadAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
            IsReading = false;
            UpdateSummary();
        }
    }

    /// <summary>
    /// The end of a read comes as an event; one sent before the watch was connected is lost, so the page also asks for the
    /// progress until the read is done, then takes every row once more.
    /// </summary>
    private async Task FollowReadAsync(CancellationToken ct)
    {
        while (IsReading)
        {
            await Task.Delay(StatePollInterval, ct).ConfigureAwait(true);
            var json = await _ctx.InvokeAsync(AppManagementMethods.GetState, null, ct).ConfigureAwait(true);
            ApplyState(AppManagementJson.Deserialize<ReadState>(json));
        }

        await LoadRowsAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Every row of the session, page by page (no device access); the index is replaced once.</summary>
    private async Task<ReadState?> LoadRowsAsync(CancellationToken ct)
    {
        var all = new List<DeviceApps>();
        int? skip = 0;
        ReadState? state = null;
        while (skip is { } next)
        {
            var json = await _ctx.InvokeAsync(AppManagementMethods.GetRows, AppManagementJson.Serialize(new RowsRequest { Skip = next }), ct).ConfigureAwait(true);
            var page = AppManagementJson.Deserialize<RowsPage>(json);
            all.AddRange(page.Devices ?? []);
            state = page.State;
            skip = page.Next;
        }

        _index.Clear();
        _index.Apply(all, null, Today());
        Rebuild();
        if (state is not null)
        {
            ApplyState(state);
        }

        return state;
    }

    /// <summary>Confirms with the application and device count, then asks the server part to start the tasks.</summary>
    private async Task RunAsync(AcapAction action)
    {
        var items = TargetApplications();
        if (items.Count == 0)
        {
            return;
        }

        var devices = items.SelectMany(i => i.DeviceIds).Distinct().Count();
        var apps = items.Count == 1 ? items[0].DisplayName ?? items[0].Application : Count(items.Count, "application", "applications");
        var where = Count(devices, "device", "devices");
        var (title, message, confirm) = action switch
        {
            AcapAction.Start => ("Start applications", $"Start {apps} on {where}?", "Start"),
            AcapAction.Stop => ("Stop applications", $"Stop {apps} on {where}?", "Stop"),
            _ => ("Remove applications", $"Remove {apps} from {where}? The applications and their settings are deleted from the devices.", "Remove"),
        };
        if (!await _ctx.ConfirmAsync(title, message, confirm).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var request = new RunRequest { Action = action, Items = items, Owner = _ctx.OwnerName };
            await _ctx.InvokeAsync(AppManagementMethods.Run, AppManagementJson.Serialize(request), _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            ErrorText = null;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    /// <summary>
    /// The grids from the index after the filters, one reset each; the selected devices and application rows stay selected
    /// while they are shown (a task's end reads the device again, the user keeps working on the same rows).
    /// </summary>
    private void Rebuild()
    {
        var view = _index.Build(new AppFilter(HideAxisApps, SelectedStatus.Value, SearchText ?? string.Empty));
        var keep = _selectedRows.Select(r => (r.DeviceId, r.AppKey)).ToHashSet();
        _rebuilding = true;
        try
        {
            DeviceRows.ReplaceAll(view.Rows);
            BuildDevices(view.Rows, keep);
        }
        finally
        {
            _rebuilding = false;
        }

        DevicesRebuilt?.Invoke(this, EventArgs.Empty);
        SelectionChanged();
        UpdateSummary(view);
    }

    /// <summary>One device row per device of the shown rows (they come per device), then the lower grid.</summary>
    private void BuildDevices(IReadOnlyList<AppRow> rows, HashSet<(Guid, string?)> keep)
    {
        var devices = new List<AppDeviceRow>();
        var i = 0;
        while (i < rows.Count)
        {
            var first = rows[i];
            var own = new List<AppRow>();
            while (i < rows.Count && rows[i].DeviceId == first.DeviceId)
            {
                if (rows[i].AppKey is not null)
                {
                    own.Add(rows[i]);
                }

                i++;
            }

            devices.Add(new AppDeviceRow(first.Device, own, first.AddressSortKey));
        }

        Devices.ReplaceAll(devices);

        _selectedDeviceSet.IntersectWith(devices.Select(d => d.DeviceId));
        _selectedDevices.RemoveAll(id => !_selectedDeviceSet.Contains(id));
        FillSelectedApps(keep);
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(ToolsBlockedReason));
        OnPropertyChanged(nameof(InstallBlockedReason));
        OnPropertyChanged(nameof(ShowEmpty));
        InstallCommand.NotifyCanExecuteChanged();
        InstallLicensesCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The line in an empty grid. The toolbar is the only row above the grid: failed reads show in their rows.</summary>
    private void UpdateSummary(AppView? view = null)
    {
        _ = view;
        EmptyText = !MayView ? NoViewRight
            : DeviceChoice.IsEmpty ? ChooseFirst
            : IsReading && _index.Count == 0 ? Reading
            : _index.Count == 0 ? (_state is null ? ChooseFirst : "None of the chosen devices could be read.")
            : NothingMatches;
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MayView));
        OnPropertyChanged(nameof(MayInstall));
        OnPropertyChanged(nameof(MayLicense));
        OnPropertyChanged(nameof(MayStartStop));
        OnPropertyChanged(nameof(MayRemove));
        OnPropertyChanged(nameof(ShowInstallTools));
        OnPropertyChanged(nameof(ShowRunTools));
        RefreshCommand.NotifyCanExecuteChanged();
        SelectionChanged();
        UpdateSummary();
    }

    private static string Count(int n, string one, string many) =>
        string.Create(CultureInfo.CurrentCulture, $"{n:N0} {(n == 1 ? one : many)}");
}
