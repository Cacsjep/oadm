using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeStorage.Client;

/// <summary>The dialogs of the page (the view opens them; tests answer them).</summary>
public interface IStorageDialogs
{
    /// <summary>The retention in days (0 = no age limit), or null when cancelled.</summary>
    Task<int?> AskRetentionAsync(int? current, int cameras);

    /// <summary>The passphrase for encryption, or null when cancelled.</summary>
    Task<string?> AskPassphraseAsync(int cameras);
}

/// <summary>
/// Page "Edge storage". The cameras come from the page's device choice (<see cref="DeviceChoice"/>: the Devices page selection,
/// else the last choice on this computer). <see cref="Activate"/> when the page is shown watches the pushed rows and reads the
/// SD cards of the chosen cameras once; Refresh reads again. The tools work on the selected rows that have a card, one task per camera (tasks pane below); each
/// asks first (format, repair and encryption delete the recordings). The tools follow the role live: "manage" (check,
/// retention) and "format" (format, repair, encryption); the server checks again.
/// </summary>
public sealed partial class EdgeStorageViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly SweepRows<StorageRow, StorageRowViewModel> _rows =
        new(r => r.DeviceId, r => new StorageRowViewModel(r), (vm, r) => vm.Apply(r), vm => vm.SearchText, vm => vm.AddressSortKey);

    private IReadOnlyList<StorageRowViewModel> _selected = [];
    private CancellationTokenSource? _active;
    private StorageState? _state;

    public EdgeStorageViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.PermissionsChanged += OnPermissionsChanged;
        DeviceChoice = new PageDeviceChoice(ctx, "cameras", HasSlot);
        DeviceChoice.PropertyChanged += (_, _) => RefreshCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The cameras the page reads: "Selected cameras: Tag Building A" (the purple button).</summary>
    public PageDeviceChoice DeviceChoice { get; }

    /// <summary>Opens the device picker (set by the view); true when a choice was made.</summary>
    public Func<Task<bool>>? PickDevices { get; set; }

    /// <summary>Cameras with an SD card slot (the server's check, from the cached API list).</summary>
    internal static bool HasSlot(IDeviceInfo device) => device.Apis.Supports("disk-management", "1.0");

    /// <summary>How often the page asks for the state while a read runs (its progress events may be missed).</summary>
    public static TimeSpan StatePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Wait before watching the events again after the stream broke (tests shorten it).</summary>
    public static TimeSpan ReconnectDelay { get; set; } = PluginEventWatch.DefaultRetryDelay;

    /// <summary>Set by the view; null in tests that do not open dialogs.</summary>
    public IStorageDialogs? Dialogs { get; set; }

    public RangeObservableCollection<StorageRowViewModel> Rows => _rows.Shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty] public partial double ProgressValue { get; private set; }

    [ObservableProperty] public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string OkText { get; private set; } = "0";

    [ObservableProperty] public partial string WornText { get; private set; } = "0";

    [ObservableProperty] public partial string ProblemsText { get; private set; } = "0";

    [ObservableProperty] public partial string NoCardText { get; private set; } = "0";

    [ObservableProperty] public partial string FailedText { get; private set; } = "0";

    [ObservableProperty] public partial bool HasOk { get; private set; }

    [ObservableProperty] public partial bool HasWorn { get; private set; }

    [ObservableProperty] public partial bool HasProblems { get; private set; }

    [ObservableProperty] public partial bool HasFailed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string EmptyText { get; private set; } = "Reading the SD cards.";

    public bool ShowEmpty => Rows.Count == 0;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        _rows.Filter(value);
        OnPropertyChanged(nameof(ShowEmpty));
        UpdateSummary();
    }

    /// <summary>Check the SD cards and set the retention.</summary>
    public bool MayManage => _ctx.Has(PermissionKeys.Of(EdgeStorageInfo.PluginId, EdgeStorageMethods.ManagePermission));

    /// <summary>Format, repair, encryption on or off.</summary>
    public bool MayFormat => _ctx.Has(PermissionKeys.Of(EdgeStorageInfo.PluginId, EdgeStorageMethods.FormatPermission));

    /// <summary>The selected rows with a card (the tools' targets).</summary>
    public IReadOnlyList<StorageRowViewModel> Targets => [.. _selected.Where(r => r.HasCard)];

    /// <summary>Why the tools are disabled (tooltip), or null.</summary>
    public string? ToolsBlockedReason => Targets.Count switch
    {
        > 0 => null,
        _ when _selected.Count > 0 => "The selected cameras have no SD card.",
        _ => "Select cameras with an SD card.",
    };

    /// <summary>The page is shown: watch the events and read once.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        _ = _ctx.RunAsync(HandleEvent, ReconnectDelay, token);
        _ = ReadAsync(token);
    }

    /// <summary>The purple button: choose the cameras, then read their SD cards.</summary>
    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (PickDevices is { } pick && await pick().ConfigureAwait(true))
        {
            await ReadAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>The page is hidden: no watch.</summary>
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

    /// <summary>The grid's selection (the view hands it over on every change).</summary>
    public void SetSelection(IEnumerable<StorageRowViewModel> selected)
    {
        _selected = [.. selected];
        OnPropertyChanged(nameof(Targets));
        OnPropertyChanged(nameof(ToolsBlockedReason));
        NotifyTools();
    }

    private bool CanRefresh => !IsReading && !DeviceChoice.IsEmpty;

    /// <summary>Reads the SD cards of the chosen cameras again.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReadAsync(_active?.Token ?? CancellationToken.None);

    private bool CanManage => MayManage && Targets.Count > 0;

    private bool CanFormat => MayFormat && Targets.Count > 0;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckAsync()
    {
        var n = Targets.Count;
        if (await _ctx.ConfirmAsync("Check SD cards",
            $"Check the SD card of {Cameras(n)}? Recording stops until the check is finished.", "Check").ConfigureAwait(true))
        {
            await RunAsync(EdgeStorageMethods.Change, new RunRequest(StorageActions.Check, Ids())).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SetRetentionAsync()
    {
        var targets = Targets;
        var current = targets.Select(r => r.Row.RetentionDays).Distinct().Count() == 1 ? targets[0].Row.RetentionDays : null;
        if (Dialogs is not null && await Dialogs.AskRetentionAsync(current, targets.Count).ConfigureAwait(true) is { } days)
        {
            await RunAsync(EdgeStorageMethods.Change, new RunRequest(StorageActions.Retention, Ids(), RetentionDays: days)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private async Task FormatAsync()
    {
        var n = Targets.Count;
        if (await _ctx.ConfirmAsync("Format SD cards",
            $"Format the SD card of {Cameras(n)}? Every recording on {(n == 1 ? "it" : "them")} is deleted.", "Format").ConfigureAwait(true))
        {
            await RunAsync(EdgeStorageMethods.Format, new RunRequest(StorageActions.Format, Ids())).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private async Task RepairAsync()
    {
        var n = Targets.Count;
        if (await _ctx.ConfirmAsync("Repair SD cards",
            $"Repair the SD card of {Cameras(n)}? Damaged recordings can be lost. Recording stops until the repair is finished.", "Repair").ConfigureAwait(true))
        {
            await RunAsync(EdgeStorageMethods.Format, new RunRequest(StorageActions.Repair, Ids())).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private async Task EncryptAsync()
    {
        if (Dialogs is not null && await Dialogs.AskPassphraseAsync(Targets.Count).ConfigureAwait(true) is { } passphrase)
        {
            await RunAsync(EdgeStorageMethods.Format, new RunRequest(StorageActions.EncryptionOn, Ids(), Passphrase: passphrase)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private async Task DecryptAsync()
    {
        var n = Targets.Count;
        if (await _ctx.ConfirmAsync("Turn off encryption",
            $"Turn off encryption on the SD card of {Cameras(n)}? The card{(n == 1 ? " is" : "s are")} formatted: every recording is deleted.", "Format").ConfigureAwait(true))
        {
            await RunAsync(EdgeStorageMethods.Format, new RunRequest(StorageActions.EncryptionOff, Ids())).ConfigureAwait(true);
        }
    }

    /// <summary>Applies one pushed event (tests call it directly).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Topic)
        {
            case EdgeStorageMethods.RowsTopic:
                var rows = EdgeStorageJson.Deserialize<RowsEvent>(item.PayloadJson);
                if (_rows.Apply(rows.Rows ?? [], rows.Removed))
                {
                    OnPropertyChanged(nameof(ShowEmpty));
                }

                SetSelection(_selected.Where(r => _rows.Find(r.DeviceId) is not null)); // a row may have got or lost its card, or left
                UpdateSummary();
                break;
            case EdgeStorageMethods.StateTopic:
                ApplyState(EdgeStorageJson.Deserialize<StorageState>(item.PayloadJson), replaceRows: false);
                break;
        }
    }

    /// <summary>A full state (read / getState reply), or the progress only.</summary>
    public void ApplyState(StorageState state, bool replaceRows)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        if (replaceRows)
        {
            _rows.Replace(state.Rows);
            OnPropertyChanged(nameof(ShowEmpty));
        }

        IsReading = state.Reading;
        ProgressValue = state.Total == 0 ? 0 : 100.0 * state.Done / state.Total;
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"Reading SD cards {state.Done:N0} of {state.Total:N0}");
        UpdateSummary();
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var cameras = DeviceChoice.Resolve();
        _rows.Scope = cameras.Select(d => d.Id).ToHashSet();
        OnPropertyChanged(nameof(ShowEmpty));
        if (cameras.Count == 0)
        {
            _state = null;
            IsReading = false;
            UpdateSummary();
            return;
        }

        try
        {
            var json = await _ctx.InvokeAsync(EdgeStorageMethods.Read, EdgeStorageJson.Serialize(new ReadRequest([.. cameras.Select(d => d.Id)])), ct).ConfigureAwait(true);
            ApplyState(EdgeStorageJson.Deserialize<StorageState>(json), replaceRows: true);
            ErrorText = null;

            // The rows and the end of the read come as events; one sent before the watch was connected is lost, so the
            // page also asks for the state (no device access) until the read is done.
            while (IsReading)
            {
                await Task.Delay(StatePollInterval, ct).ConfigureAwait(true);
                json = await _ctx.InvokeAsync(EdgeStorageMethods.GetState, null, ct).ConfigureAwait(true);
                ApplyState(EdgeStorageJson.Deserialize<StorageState>(json), replaceRows: true);
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
            IsReading = false;
            UpdateSummary();
        }
    }

    /// <summary>Starts the tasks; their progress shows in the tasks pane, the rows are read again when each ends.</summary>
    private async Task RunAsync(string method, RunRequest request)
    {
        try
        {
            await _ctx.InvokeAsync(method, EdgeStorageJson.Serialize(request with { Owner = _ctx.OwnerName }), _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
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

    private Guid[] Ids() => [.. Targets.Select(r => r.DeviceId)];

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(MayFormat));
        NotifyTools();
    }

    private void NotifyTools()
    {
        CheckCommand.NotifyCanExecuteChanged();
        SetRetentionCommand.NotifyCanExecuteChanged();
        FormatCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        EncryptCommand.NotifyCanExecuteChanged();
        DecryptCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSummary()
    {
        var all = _rows.All;
        int Count(string state) => all.Count(r => r.Row.State == state);
        var worn = Count(CardStates.Warning);
        var problems = Count(CardStates.Problem);
        var failed = Count(CardStates.Error);
        var ok = Count(CardStates.Ok);
        OkText = Number(ok);
        HasOk = ok > 0;
        WornText = Number(worn);
        ProblemsText = Number(problems);
        NoCardText = Number(Count(CardStates.NoCard));
        FailedText = Number(failed);
        HasWorn = worn > 0;
        HasProblems = problems > 0;
        HasFailed = failed > 0;

        EmptyText = HasError && _rows.Count == 0 ? string.Empty
            : DeviceChoice.IsEmpty ? "Choose cameras to read their SD cards."
            : DeviceChoice.Resolve().Count == 0 ? "None of the chosen cameras has an SD card slot."
            : _state is null || _state.Reading
            ? "Reading the SD cards."
            : _rows.Count == 0 ? "No camera with an SD card slot yet." : "No camera matches the search.";
    }

    private static string Number(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    private static string Cameras(int n) => n == 1 ? "1 camera" : string.Create(CultureInfo.CurrentCulture, $"{n:N0} cameras");
}
