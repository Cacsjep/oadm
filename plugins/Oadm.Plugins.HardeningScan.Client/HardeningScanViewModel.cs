using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.HardeningScan.Client;

/// <summary>One check of the selected device in the detail pane.</summary>
public sealed class DetailItem(LevelColumn column, CheckState state, string? value, string? detail)
{
    public string Title => column.Check.Title;

    public string Section => column.Check.Section;

    public string Recommendation => column.Check.Recommendation + (column.Check.Note is { } note ? " " + note : string.Empty);

    public string StateText => CheckStateCodes.ToLabel(state);

    public bool IsOk => state == CheckState.Pass;

    public bool IsWarning => state == CheckState.Warn;

    public bool IsError => state is CheckState.Fail or CheckState.Error;

    /// <summary>Sort key of the Result column: failed, read error, warning, information / does not apply, passed, not scanned.</summary>
    public int SortRank => state switch
    {
        CheckState.Fail => 0,
        CheckState.Error => 1,
        CheckState.Warn => 2,
        CheckState.Pass => 4,
        CheckState.NotScanned => 5,
        _ => 3,
    };

    /// <summary>The value found, plus the detail when the server sent one.</summary>
    public string Found => string.IsNullOrEmpty(detail) ? value ?? string.Empty : $"{value} · {detail}";
}

/// <summary>
/// The Hardening scan page: one row per managed device, one icon column per check of the shown level (Basic / Extended),
/// Scan / Stop, the progress and summary lines, a status filter and search, CSV export, and the selected device's checks.
/// Results arrive as plugin events (<see cref="HardeningMethods.ResultsTopic"/>) and are applied per changed row: the
/// summary and the per-column counters follow only the changed rows (5,000 devices).
/// </summary>
public sealed partial class HardeningScanViewModel : ObservableObject, IDisposable
{
    public const string FilterAll = "All devices";
    public const string FilterFailed = "Failed";
    public const string FilterWarnings = "Warnings";
    public const string FilterNotScanned = "Not scanned";

    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, HardeningRow> _byId = [];
    private readonly Dictionary<Guid, List<DeviceResult>> _orphans = [];
    private readonly int[] _kindCounts = new int[5];
    private int[,] _columnCounts = new int[0, 8];
    private List<HardeningRow> _all = [];
    private CancellationTokenSource? _active;
    private string _search = string.Empty;
    private bool _applying;
    private HardeningRow? _selectedRow;
    private string? _jobId;

    public HardeningScanViewModel(ICorePluginClientContext ctx, HardeningClientSettingsStore? settingsStore = null, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
        SettingsStore = settingsStore ?? new HardeningClientSettingsStore();
        Settings = SettingsStore.Load();
        Level = Settings.Level == ScanLevel.Extended ? ScanLevel.Extended : ScanLevel.Basic;
        Columns = BuildColumns(Level);
        _columnCounts = new int[Columns.Count, 8];
        _ctx.DevicesChanged += OnDevicesChanged;
        SyncDevices();
    }

    /// <summary>Wait before watching again after the event stream ended or failed (also the polling interval).</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised after <see cref="Columns"/> changed (the view rebuilds the check columns).</summary>
    public event EventHandler? ColumnsChanged;

    public HardeningClientSettingsStore SettingsStore { get; }

    public HardeningClientSettings Settings { get; }

    /// <summary>The visible rows (filter and search applied), by IPv4 address.</summary>
    public RangeObservableCollection<HardeningRow> Rows { get; } = [];

    /// <summary>All rows (tests, export).</summary>
    public IReadOnlyList<HardeningRow> AllRows => _all;

    /// <summary>The check columns of the shown level.</summary>
    public IReadOnlyList<LevelColumn> Columns { get; private set; }


    public IReadOnlyList<string> Filters { get; } = [FilterAll, FilterFailed, FilterWarnings, FilterNotScanned];

    /// <summary>The checks of the selected device.</summary>
    public RangeObservableCollection<DetailItem> DetailItems { get; } = [];

    /// <summary>Saves the CSV export (set by the view: the save dialog). Returns false when cancelled.</summary>
    public Func<string, string, Task<bool>>? SaveCsv { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBasic))]
    [NotifyPropertyChangedFor(nameof(IsExtended))]
    public partial ScanLevel Level { get; set; }

    public bool IsBasic => Level == ScanLevel.Basic;

    public bool IsExtended => Level == ScanLevel.Extended;

    [ObservableProperty]
    public partial string SelectedFilter { get; set; } = FilterAll;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(ScanTip))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    /// <summary>The last problem talking to the server (shown under the toolbar), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = string.Empty;

    public bool HasError => ErrorText.Length > 0;

    public string ScanTip => IsScanning ? "A scan is running" : "Read the settings of every managed device and check them (read-only)";

    public string ScanSelectedText => string.Create(CultureInfo.InvariantCulture, $"Scan selected ({_ctx.SelectedDevices.Count:N0})");

    public string ScanSelectedTip => IsScanning ? "A scan is running"
        : _ctx.SelectedDevices.Count == 0 ? "Select devices on the Devices page first"
        : "Scan the devices selected on the Devices page";

    public HardeningRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (_applying && value is null)
            {
                return; // the grid pushes null during a reset: keep the selection
            }

            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(DetailTitle));
                RefreshDetail();
                _ = LoadDetailAsync(value);
            }
        }
    }

    public string DetailTitle => SelectedRow is { } row
        ? $"{row.Address} · {(row.Model.Length > 0 ? row.Model : row.Serial)}{(row.Status is { } s ? " · " + s : string.Empty)}"
        : "Select a device to see its checks";

    /// <summary>Rows per <see cref="RowKind"/> (tests).</summary>
    public int CountOf(RowKind kind) => _kindCounts[(int)kind];

    /// <summary>Column header tooltip: the guide section and the counts.</summary>
    public string HeaderTip(int column)
    {
        if (column < 0 || column >= Columns.Count)
        {
            return string.Empty;
        }

        var check = Columns[column].Check;
        return string.Create(CultureInfo.InvariantCulture,
            $"{check.Title} ({check.Section})\nPass {_columnCounts[column, (int)CheckState.Pass]:N0} · Warn {_columnCounts[column, (int)CheckState.Warn]:N0} · Fail {_columnCounts[column, (int)CheckState.Fail]:N0}");
    }

    /// <summary>Count of a state in a column of the shown level (tests).</summary>
    public int ColumnCount(int column, CheckState state) => _columnCounts[column, (int)state];

    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        SelectionChanged(); // the Devices page selection may have changed while the page was hidden
        _ = RunAsync(_active.Token);
    }

    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose()
    {
        Deactivate();
        _ctx.DevicesChanged -= OnDevicesChanged;
    }

    /// <summary>Reads the whole state (catalog, last results, running scan).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _ctx.InvokeAsync(HardeningMethods.GetState, null, ct).ConfigureAwait(true);
            if (json is not null)
            {
                ApplyState(HardeningJson.Deserialize<HardeningState>(json));
            }

            ErrorText = string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = "Cannot read the scan results: " + Message(ex);
        }
    }

    public void ApplyState(HardeningState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyResults(state.Results);
        if (state.Job is { } job)
        {
            ApplyProgress(job);
        }
    }

    /// <summary>Applies one live event (also used by tests).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.PayloadJson))
        {
            return;
        }

        switch (item.Topic)
        {
            case HardeningMethods.ResultsTopic:
                ApplyResults(HardeningJson.Deserialize<ResultsEvent>(item.PayloadJson).Results);
                break;
            case HardeningMethods.ProgressTopic:
                ApplyProgress(HardeningJson.Deserialize<ScanJobStatus>(item.PayloadJson));
                break;
        }
    }

    /// <summary>Applies results: only the changed rows are recomputed, the counters follow them.</summary>
    public void ApplyResults(IReadOnlyList<DeviceResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var changed = new HashSet<HardeningRow>();
        foreach (var result in results)
        {
            if (_byId.TryGetValue(result.DeviceId, out var row))
            {
                row.SetResult(result);
                changed.Add(row);
            }
            else
            {
                // Not in the client's device list (yet): kept until the device appears.
                if (!_orphans.TryGetValue(result.DeviceId, out var list))
                {
                    _orphans[result.DeviceId] = list = [];
                }

                list.Add(result);
            }
        }

        foreach (var row in changed)
        {
            Count(row, -1);
            row.Show(Level, Columns, now);
            Count(row, +1);
        }

        if (changed.Count > 0)
        {
            if (!IsUnfiltered)
            {
                ApplyFilter();
            }

            UpdateSummary();
            if (_selectedRow is not null && changed.Contains(_selectedRow))
            {
                OnPropertyChanged(nameof(DetailTitle));
                RefreshDetail();
                _ = LoadDetailAsync(_selectedRow);
            }
        }
    }

    public void ApplyProgress(ScanJobStatus job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobId = job.JobId;
        IsScanning = job.IsRunning;
        ProgressValue = job.Total == 0 ? 100 : 100.0 * job.Done / job.Total;
        ProgressText = job.IsRunning
            ? string.Create(CultureInfo.InvariantCulture, $"Scanning {job.Done:N0} of {job.Total:N0} devices ({(job.Level == ScanLevel.Extended ? "Extended" : "Basic")})")
            : string.Create(CultureInfo.InvariantCulture, $"{(job.State == ScanJobStates.Cancelled ? "Scan stopped" : "Scan finished")}: {job.Done:N0} of {job.Total:N0} devices");
    }

    [RelayCommand]
    private void ShowBasic() => Level = ScanLevel.Basic;

    [RelayCommand]
    private void ShowExtended() => Level = ScanLevel.Extended;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAllAsync() => StartScanAsync([]);

    [RelayCommand(CanExecute = nameof(CanScanSelected))]
    private Task ScanSelectedAsync() => StartScanAsync([.. _ctx.SelectedDevices.Select(d => d.Id)]);

    private bool CanScan() => !IsScanning && _all.Count > 0;

    private bool CanScanSelected() => !IsScanning && _ctx.SelectedDevices.Count > 0;

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private async Task StopAsync()
    {
        try
        {
            var json = await _ctx.InvokeAsync(HardeningMethods.CancelScan, HardeningJson.Serialize(new CancelScanRequest { JobId = _jobId }), CancellationToken.None).ConfigureAwait(true);
            if (json is not null)
            {
                ApplyProgress(HardeningJson.Deserialize<ScanJobStatus>(json));
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = "The scan could not be stopped: " + Message(ex);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SaveCsv is not { } save)
        {
            return;
        }

        var csv = CsvExport.Build(Columns, Rows);
        var name = string.Create(CultureInfo.InvariantCulture, $"hardening-scan-{(IsExtended ? "extended" : "basic")}-{_time.GetLocalNow():yyyy-MM-dd}.csv");
        try
        {
            await save(name, csv).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = "The export could not be saved: " + ex.Message;
        }
    }

    partial void OnLevelChanged(ScanLevel value)
    {
        Columns = BuildColumns(value);
        Settings.Level = value;
        SettingsStore.Save(Settings);
        RecomputeAll();
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        RefreshDetail();
    }

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value)
    {
        _search = value?.Trim().ToLowerInvariant() ?? string.Empty;
        ApplyFilter();
    }

    private bool IsUnfiltered => SelectedFilter == FilterAll && _search.Length == 0;

    private static List<LevelColumn> BuildColumns(ScanLevel level) =>
        [.. HardeningCatalog.ColumnsOf(level).Select(c => new LevelColumn(c, HardeningCatalog.ColumnIndex[c.Id]))];

    private async Task StartScanAsync(IReadOnlyList<Guid> deviceIds)
    {
        try
        {
            ErrorText = string.Empty;
            var json = await _ctx.InvokeAsync(HardeningMethods.StartScan, HardeningJson.Serialize(new StartScanRequest { Level = Level, DeviceIds = deviceIds }), CancellationToken.None).ConfigureAwait(true);
            if (json is not null)
            {
                ApplyProgress(HardeningJson.Deserialize<ScanJobStatus>(json));
            }
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = "The scan could not be started: " + Message(ex);
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => SyncDevices();

    /// <summary>One row per managed device (O(n) per device change), results of new devices taken from the waiting ones.</summary>
    private void SyncDevices()
    {
        var now = _time.GetUtcNow();
        var devices = _ctx.Devices;
        var seen = new HashSet<Guid>();
        var structural = false;
        foreach (var device in devices)
        {
            if (!seen.Add(device.Id))
            {
                continue;
            }

            if (_byId.TryGetValue(device.Id, out var row))
            {
                if (row.UpdateDevice(device))
                {
                    structural = true; // the address may have changed: sort again
                }

                continue;
            }

            row = new HardeningRow(device);
            if (_orphans.Remove(device.Id, out var waiting))
            {
                foreach (var result in waiting)
                {
                    row.SetResult(result);
                }
            }

            row.Show(Level, Columns, now);
            _byId[device.Id] = row;
            Count(row, +1);
            structural = true;
        }

        foreach (var gone in _byId.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            Count(_byId[gone], -1);
            _byId.Remove(gone);
            structural = true;
        }

        if (structural)
        {
            _all = [.. _byId.Values.OrderBy(r => r.AddressSortKey).ThenBy(r => r.Address, StringComparer.OrdinalIgnoreCase)];
            if (_selectedRow is not null && !_byId.ContainsKey(_selectedRow.DeviceId))
            {
                SelectedRow = null;
            }

            ApplyFilter();
        }

        UpdateSummary();
        OnPropertyChanged(nameof(ScanSelectedText));
        OnPropertyChanged(nameof(ScanSelectedTip));
        ScanAllCommand.NotifyCanExecuteChanged();
        ScanSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The Devices page selection changed (the host raises DevicesChanged for it too, or the view calls this).</summary>
    public void SelectionChanged()
    {
        OnPropertyChanged(nameof(ScanSelectedText));
        OnPropertyChanged(nameof(ScanSelectedTip));
        ScanSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RecomputeAll()
    {
        var now = _time.GetUtcNow();
        Array.Clear(_kindCounts);
        _columnCounts = new int[Columns.Count, 8];
        foreach (var row in _all)
        {
            row.Show(Level, Columns, now);
            Count(row, +1);
        }

        ApplyFilter();
        UpdateSummary();
    }

    private void Count(HardeningRow row, int sign)
    {
        _kindCounts[(int)row.Kind] += sign;
        var states = row.States;
        var columns = Math.Min(states.Length, _columnCounts.GetLength(0));
        for (var i = 0; i < columns; i++)
        {
            _columnCounts[i, states[i]] += sign;
        }
    }

    private bool Matches(HardeningRow row)
    {
        var filter = SelectedFilter switch
        {
            FilterFailed => row.Kind is RowKind.Fail,
            FilterWarnings => row.Kind is RowKind.Warn,
            FilterNotScanned => row.Kind is RowKind.NotScanned or RowKind.NotReachable,
            _ => true,
        };
        return filter && (_search.Length == 0 || row.SearchText.Contains(_search, StringComparison.Ordinal));
    }

    /// <summary>One reset of the visible rows (O(n)); the selection is kept while its row stays visible.</summary>
    private void ApplyFilter()
    {
        var selected = _selectedRow;
        _applying = true;
        try
        {
            Rows.ReplaceAll(IsUnfiltered ? _all : _all.Where(Matches).ToList());
        }
        finally
        {
            _applying = false;
        }

        if (selected is not null && !Matches(selected))
        {
            SelectedRow = null;
        }
        else
        {
            OnPropertyChanged(nameof(SelectedRow)); // the grid lost it in the reset: select it again
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var total = _all.Count;
        var notScanned = _kindCounts[(int)RowKind.NotScanned];
        var scanned = total - notScanned;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{total:N0} {(total == 1 ? "device" : "devices")} · {scanned:N0} scanned · {_kindCounts[(int)RowKind.Pass]:N0} pass · {_kindCounts[(int)RowKind.Warn]:N0} with warnings · {_kindCounts[(int)RowKind.Fail]:N0} failed · {_kindCounts[(int)RowKind.NotReachable]:N0} not reachable");
        if (!IsUnfiltered)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {Rows.Count:N0} shown");
        }

        SummaryText = text;
    }

    private void RefreshDetail()
    {
        if (_selectedRow is not { } row)
        {
            DetailItems.ReplaceAll([]);
            return;
        }

        DetailItems.ReplaceAll(Columns.Select((c, i) => new DetailItem(c, row.StateAt(i), i < row.Values.Length ? row.Values[i] : null, null)).ToList());
    }

    /// <summary>The longer texts of the selected device's checks (getDetail), merged into the detail list.</summary>
    private async Task LoadDetailAsync(HardeningRow? row)
    {
        if (row is null || row.Kind == RowKind.NotScanned)
        {
            return;
        }

        try
        {
            var json = await _ctx.InvokeAsync(HardeningMethods.GetDetail, HardeningJson.Serialize(new DetailRequest { DeviceIds = [row.DeviceId] }), CancellationToken.None).ConfigureAwait(true);
            if (json is null || !ReferenceEquals(row, _selectedRow))
            {
                return;
            }

            var reply = HardeningJson.Deserialize<DetailReply>(json);
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var device in reply.Devices.OrderBy(d => d.ScannedUtc))
            {
                foreach (var check in device.Checks.Where(c => c.Detail is not null && c.State != CheckState.NotScanned))
                {
                    details[check.Id] = check.Detail!; // the newest wins
                }
            }

            if (details.Count > 0)
            {
                DetailItems.ReplaceAll(Columns.Select((c, i) => new DetailItem(c, row.StateAt(i), i < row.Values.Length ? row.Values[i] : null, details.GetValueOrDefault(c.Check.Id))).ToList());
            }
        }
#pragma warning disable CA1031 // The short values are already shown; the details are optional.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = _ctx.WatchEventsAsync(ct).GetAsyncEnumerator(ct);
                try
                {
                    // Subscribe first, then read the state: nothing published in between is lost (older results are ignored).
                    var next = events.MoveNextAsync();
                    await LoadAsync(ct).ConfigureAwait(true);
                    while (await next.ConfigureAwait(true))
                    {
                        HandleEvent(events.Current);
                        next = events.MoveNextAsync();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The connection dropped: read again and watch again after a short delay.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(true);
                }

                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // page hidden
        }
    }

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without a Grpc reference.</summary>
    private static string Message(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }
}
