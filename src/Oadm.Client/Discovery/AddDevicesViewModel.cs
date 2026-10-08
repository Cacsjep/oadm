using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Discovery;

/// <summary>How the add page finds devices.</summary>
public enum AddDevicesMode
{
    /// <summary>Zero-configuration (mDNS), starts when the page opens.</summary>
    Scan,

    /// <summary>IPv4 range, starts on Enter or the Scan button.</summary>
    IpRange,

    /// <summary>One entered address (IP or host name, optional port and scheme) per search.</summary>
    Manual,

    /// <summary>Every address of an imported device list (<see cref="DeviceImportFile"/>), probed like "Add manually".</summary>
    Import,
}

/// <summary>Which inline editor is open below the device list.</summary>
public enum AddEditor
{
    None,

    /// <summary>User name + password for a device where no known credential worked.</summary>
    Login,

    /// <summary>First password for a factory-default device.</summary>
    Password,
}

/// <summary>
/// The one add devices page (no wizard). Devices appear live while the server finds them and logs in
/// to them with the technician's known credentials; authenticated devices (and factory-default ones
/// once their first password is entered) are selected and added with one click. "Login failed" rows
/// open an inline login editor that retries right away; factory-default rows open the password
/// editor. All device work happens on the server; passwords typed here go to the server only.
/// </summary>
public sealed partial class AddDevicesViewModel : ValidatingViewModel, IAsyncDisposable
{
    public const int MaxRangeSize = 65536;

    /// <summary>Import: addresses probed at the same time (a probe holds its slot until its logins finished).</summary>
    public const int MaxImportProbes = 16;

    private readonly IOadmApi _api;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _sessions = [];
    private readonly Dictionary<string, string> _manualInputs = [];

    // UI thread only: sessions whose scan runs, whose stop was asked for, and whose watch stream is open.
    private readonly HashSet<string> _scanning = [];
    private readonly HashSet<string> _stopRequested = [];
    private readonly HashSet<string> _watching = [];
    private int _starting;
    private bool _anyStopped;

    public AddDevicesViewModel(IOadmApi api, IUiDispatcher ui, ILogger<AddDevicesViewModel> logger, AddDevicesMode mode)
    {
        _api = api;
        _ui = ui;
        _logger = logger;
        Mode = mode;
        Rows.CollectionChanged += OnRowsChanged;

        // Field errors below their field: IP range, address, login and first password editors.
        Validation
            .Rule(nameof(RangeFrom), () => IsRangeMode ? RangeStartError(RangeFrom) : null)
            .Rule(nameof(RangeTo), () => IsRangeMode ? RangeEndError(RangeFrom, RangeTo) : null)
            .Rule(nameof(ManualAddress), () => IsManualMode && ManualAddress.Trim().Length == 0 ? "Enter an IP address or host name." : null)
            .Rule(nameof(EditorUserName), () => IsLoginEditorOpen && EditorUserName.Trim().Length == 0 ? "Enter a user name." : null)
            .Rule(nameof(EditorPassword), () => IsLoginEditorOpen && EditorPassword.Length == 0 ? "Enter the password." : null)
            .Rule(nameof(NewPassword), () => IsPasswordEditorOpen ? NewPasswordError() : null)
            .Rule(nameof(ConfirmPassword), () => IsPasswordEditorOpen ? PasswordRules.ConfirmError(NewPassword, ConfirmPassword) : null);
        Validation.Validate();
    }

    private static readonly string[] RangeFields = [nameof(RangeFrom), nameof(RangeTo)];
    private static readonly string[] LoginFields = [nameof(EditorUserName), nameof(EditorPassword)];
    private static readonly string[] PasswordFields = [nameof(NewPassword), nameof(ConfirmPassword)];
    private static readonly string[] EditorFields = [.. LoginFields, .. PasswordFields];

    /// <summary>Why Scan is disabled (tooltip), null when the range is valid.</summary>
    public string? RangeBlockedReason => Validation.FirstErrorOf(RangeFields);

    /// <summary>Why Find is disabled (tooltip).</summary>
    public string? AddressBlockedReason => Validation.FirstErrorOf([nameof(ManualAddress)]);

    /// <summary>Why Retry is disabled (tooltip).</summary>
    public string? RetryBlockedReason => Validation.FirstErrorOf(LoginFields);

    /// <summary>Why Apply of the password editor is disabled (tooltip).</summary>
    public string? ApplyPasswordBlockedReason => Validation.FirstErrorOf(PasswordFields);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(RangeBlockedReason));
        OnPropertyChanged(nameof(AddressBlockedReason));
        OnPropertyChanged(nameof(RetryBlockedReason));
        OnPropertyChanged(nameof(ApplyPasswordBlockedReason));
        StartRangeCommand.NotifyCanExecuteChanged();
        ProbeAddressCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        ApplyPasswordCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The first password must satisfy the policy of every device it is set on.</summary>
    private string? NewPasswordError()
    {
        IEnumerable<string?> policies = UseForAllFactoryDefault
            ? Rows.Where(r => r.IsFactoryDefault).Select(r => (string?)r.PassphrasePolicy).Distinct()
            : [EditorRow?.PassphrasePolicy];
        return policies.Select(p => PasswordRules.PasswordError(NewPassword, p)).FirstOrDefault(e => e is not null);
    }

    public AddDevicesMode Mode { get; }
    public bool IsScanMode => Mode == AddDevicesMode.Scan;
    public bool IsRangeMode => Mode == AddDevicesMode.IpRange;
    public bool IsManualMode => Mode == AddDevicesMode.Manual;
    public bool IsImportMode => Mode == AddDevicesMode.Import;

    public static string Title => "Add devices";

    public string HeaderTitle => Mode switch
    {
        AddDevicesMode.IpRange => "Scan IP range",
        AddDevicesMode.Manual => "Add manually",
        AddDevicesMode.Import => "Import devices",
        _ => "Devices on the network",
    };

    public string HeaderDescription => Mode switch
    {
        AddDevicesMode.IpRange => "Enter the first and last IPv4 address and press Enter. Every address is probed on HTTPS (443) and HTTP (80).",
        AddDevicesMode.Manual => "Enter an IP address or host name, optionally with port or scheme (https://camera.example.com:8443), and press Enter.",
        AddDevicesMode.Import => string.Create(CultureInfo.CurrentCulture,
            $"Addresses from {ImportFileName}, {MaxImportProbes} checked at a time. User names and passwords in the file are tried first for their device."),
        _ => "Found with zero-configuration (Bonjour). The scan ends after the time set on the Settings page, or with Stop.",
    } + " OADM logs in with your known credentials; authenticated devices can be added right away.";

    public ObservableCollection<DiscoveredRowViewModel> Rows { get; } = [];
    public RangeObservableCollection<DiscoveredRowViewModel> FilteredRows { get; } = [];

    /// <summary>Device ids added while the page was open.</summary>
    public List<string> AddedDeviceIds { get; } = [];

    /// <summary>Raised when the page should close; true = devices were added.</summary>
    public event EventHandler<bool>? CloseRequested;

    // ------------------------------------------------------------ inputs and state

    [ObservableProperty] public partial string RangeFrom { get; set; } = "";
    [ObservableProperty] public partial string RangeTo { get; set; } = "";
    [ObservableProperty] public partial string ManualAddress { get; set; } = "";
    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStop), nameof(ShowScanAgain))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand), nameof(ScanAgainCommand))]
    public partial bool IsScanning { get; private set; }

    /// <summary>A scan was started on this page (Scan again needs something to repeat).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowScanAgain))]
    [NotifyCanExecuteChangedFor(nameof(ScanAgainCommand))]
    public partial bool HasScanned { get; private set; }

    /// <summary>Stop button: zero-conf and IP range scans and an import while they run.</summary>
    public bool ShowStop => IsScanning && !IsManualMode;

    /// <summary>"Scan again" after a zero-conf or IP range scan finished or was stopped.</summary>
    public bool ShowScanAgain => !IsScanning && HasScanned && !IsManualMode && !IsImportMode;

    [ObservableProperty] public partial bool IsProgressIndeterminate { get; private set; }
    [ObservableProperty] public partial int ScanProgress { get; private set; }
    [ObservableProperty] public partial string ScanStatusText { get; private set; } = "";
    [ObservableProperty] public partial string SummaryText { get; private set; } = "";
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial int SelectedCount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string AddButtonText { get; private set; } = "Add";

    /// <summary>The row the user clicked; opens the matching inline editor.</summary>
    [ObservableProperty] public partial DiscoveredRowViewModel? FocusedRow { get; set; }

    // ------------------------------------------------------------ inline editors

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginEditorOpen), nameof(IsPasswordEditorOpen), nameof(IsEditorOpen))]
    public partial AddEditor Editor { get; private set; }

    public bool IsLoginEditorOpen => Editor == AddEditor.Login;
    public bool IsPasswordEditorOpen => Editor == AddEditor.Password;
    public bool IsEditorOpen => Editor != AddEditor.None;

    [ObservableProperty] public partial DiscoveredRowViewModel? EditorRow { get; private set; }
    [ObservableProperty] public partial string EditorTitle { get; private set; } = "";
    [ObservableProperty] public partial string EditorUserName { get; set; } = "root";
    [ObservableProperty] public partial string EditorPassword { get; set; } = "";

    /// <summary>Login editor: store a working credential in the server's encrypted credential list.</summary>
    [ObservableProperty] public partial bool SaveToCredentialList { get; set; } = true;
    [ObservableProperty] public partial string NewPassword { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";

    /// <summary>Password editor: use the password for every factory-default device in the list.</summary>
    [ObservableProperty] public partial bool UseForAllFactoryDefault { get; set; }
    [ObservableProperty] public partial string PolicyHint { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool IsRetrying { get; private set; }

    // ------------------------------------------------------------ lifecycle

    /// <summary>Called when the page opens. Scan mode starts discovery immediately, import mode checks the addresses.</summary>
    public async Task OpenAsync()
    {
        if (IsScanMode)
        {
            await StartZeroConfAsync().ConfigureAwait(true);
        }
        else if (IsImportMode && ImportCompletion is null)
        {
            ImportCompletion = RunImportAsync(); // runs in the background; the page stays responsive
        }
    }

    // ------------------------------------------------------------ import

    /// <summary>The imported file's name (header text).</summary>
    public string ImportFileName { get; private set; } = "";

    /// <summary>Completes when every address of the import was probed (or the import was stopped). Tests await it.</summary>
    public Task? ImportCompletion { get; private set; }

    // UI thread only: import rows waiting for their probe's device, sessions holding a probe slot,
    // sessions whose device is another line's (duplicate), rows not probed yet.
    private readonly Dictionary<string, DiscoveredRowViewModel> _importRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _importSlotHeld = new(StringComparer.Ordinal);
    private readonly HashSet<string> _duplicateSessions = new(StringComparer.Ordinal);
    private readonly List<DiscoveredRowViewModel> _importQueue = [];
    private SemaphoreSlim? _importSlots;
    private CancellationTokenSource? _importStop;
    private bool _importRunning;
    private int _importTotal;
    private int _importDone;

    /// <summary>
    /// Import mode, before the page opens: one row per line of the file, at once (one list reset), in file
    /// order. Lines with a problem show it in their row; the others wait for their probe.
    /// </summary>
    public void SetImport(DeviceImportFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!IsImportMode || ImportCompletion is not null)
        {
            throw new InvalidOperationException("The import is set once, on an import page that has not opened yet.");
        }

        ImportFileName = file.FileName;
        OnPropertyChanged(nameof(HeaderDescription));
        Rows.CollectionChanged -= OnRowsChanged;
        foreach (ImportLine line in file.Lines)
        {
            DiscoveredRowViewModel row = DiscoveredRowViewModel.ForImport(line);
            row.PropertyChanged += OnRowPropertyChanged;
            _rowsById.TryAdd(row.DiscoveredId, row);
            Rows.Add(row);
            Recount(row);
            if (line.Problem is null)
            {
                _importQueue.Add(row);
            }
        }

        Rows.CollectionChanged += OnRowsChanged;
        FilteredRows.ReplaceAll(Rows.Where(r => IsListed(r, SearchText)).ToList());
        _importTotal = _importQueue.Count;
        UpdateSummary();
    }

    /// <summary>
    /// Probes every address of the import, at most <see cref="MaxImportProbes"/> at a time: a probe holds
    /// its slot until its watch stream ended (device found and its logins finished, or nothing answered).
    /// Credentials of a line go with its probe; the server tries them first for that device only.
    /// </summary>
    private async Task RunImportAsync()
    {
        if (_importTotal == 0)
        {
            ScanStatusText = "No address of the file can be checked.";
            return;
        }

        _importRunning = true;
        HasScanned = true;
        _anyStopped = false;
        IsProgressIndeterminate = false;
        ScanProgress = 0;
        UpdateImportProgress();
        UpdateScanning();
        var slots = new SemaphoreSlim(MaxImportProbes);
        _importSlots = slots;
        _importStop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        CancellationToken stop = _importStop.Token;
        int next = 0;
        try
        {
            for (; next < _importQueue.Count; next++)
            {
                await slots.WaitAsync(stop).ConfigureAwait(true);
                DiscoveredRowViewModel row = _importQueue[next];
                ImportLine line = row.ImportLine!;
                _ui.Post(row.MarkImportChecking);
                string sessionId;
                try
                {
                    sessionId = await _api.ProbeAddressAsync(line.Address, line.UserName, line.Password, stop).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    slots.Release();
                    _ui.Post(() =>
                    {
                        row.SetImportProblem("Not found", Message(ex)); // e.g. a host name that does not resolve
                        ImportLineDone();
                    });
                    continue;
                }

                _ui.Post(() =>
                {
                    _sessions.Add(sessionId);
                    _scanning.Add(sessionId);
                    _importRows[sessionId] = row;
                    _importSlotHeld.Add(sessionId);
                    row.SessionId = sessionId;
                    StartWatch(sessionId);
                });
            }
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            // Stop: the lines not probed yet stay unchecked.
            int from = next;
            _ui.Post(() =>
            {
                for (int i = from; i < _importQueue.Count; i++)
                {
                    _importQueue[i].SetImportProblem("Not checked", "Stopped before this address was checked.", PillKind.Neutral);
                }
            });
        }
        catch (OperationCanceledException)
        {
            return; // page closed
        }

        _ui.Post(() =>
        {
            _importRunning = false;
            ScanFinished(null);
        });
    }

    /// <summary>A probe's watch stream ended: its slot is free for the next address.</summary>
    private void ReleaseImportSlot(string sessionId)
    {
        if (_importSlotHeld.Remove(sessionId))
        {
            try
            {
                _importSlots?.Release();
            }
            catch (ObjectDisposedException)
            {
                // page closed
            }
        }
    }

    private void ImportLineDone()
    {
        _importDone++;
        UpdateImportProgress();
    }

    private void UpdateImportProgress()
    {
        ScanProgress = _importTotal == 0 ? 100 : _importDone * 100 / _importTotal;
        ScanStatusText = string.Create(CultureInfo.CurrentCulture, $"Checking addresses: {_importDone:N0} of {_importTotal:N0}");
    }

    /// <summary>Zero-conf: runs until the server's time limit (Discovery.ZeroConfSeconds) or Stop.</summary>
    private async Task StartZeroConfAsync()
    {
        ErrorText = null;
        _anyStopped = false;
        ScanStatusText = "Searching the network...";
        ScanProgress = 0;
        IsProgressIndeterminate = true;
        await StartSessionAsync(() => _api.StartZeroConfAsync(_cts.Token), null).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (string session in _sessions)
        {
            try
            {
                await _api.StopDiscoveryAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStopFailed(_logger, ex.Message);
            }
        }

        _cts.Dispose();
        _importStop?.Dispose();
    }

    // ------------------------------------------------------------ commands

    /// <summary>Range mode: Enter or the Scan button.</summary>
    [RelayCommand(CanExecute = nameof(CanStartRange))]
    private async Task StartRangeAsync()
    {
        ErrorText = null;
        if (!Validation.IsValidFor(RangeFields))
        {
            Validation.ShowAll(RangeFields);
            return;
        }

        string from = RangeFrom.Trim();
        string to = RangeTo.Trim();
        _anyStopped = false;
        ScanStatusText = $"Scanning {from} - {to}";
        ScanProgress = 0;
        IsProgressIndeterminate = false;
        await StartSessionAsync(() => _api.StartRangeScanAsync(from, to, _cts.Token), null).ConfigureAwait(true);
    }

    private bool CanStartRange() => Validation.IsValidFor(RangeFields);

    /// <summary>Stop button: ends the running scans on the server; the devices found stay in the list.</summary>
    [RelayCommand(CanExecute = nameof(ShowStop))]
    private async Task StopScanAsync()
    {
        if (_importStop is { IsCancellationRequested: false } import)
        {
            _anyStopped = true;
            await import.CancelAsync().ConfigureAwait(true); // no further addresses; running probes end below
        }

        foreach (string session in _scanning.ToList())
        {
            _stopRequested.Add(session);
            try
            {
                await _api.StopScanAsync(session, _cts.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The stream still ends the scan; without it the page would keep "scanning".
                LogStopFailed(_logger, ex.Message);
                ScanFinished(session);
            }
        }
    }

    /// <summary>"Scan again": a new zero-conf scan, or the IP range again. Found devices stay in the list.</summary>
    [RelayCommand(CanExecute = nameof(ShowScanAgain))]
    private Task ScanAgainAsync() => IsRangeMode ? StartRangeAsync() : StartZeroConfAsync();

    /// <summary>Manual mode: Enter or the Find button. Every address adds to the list.</summary>
    [RelayCommand(CanExecute = nameof(CanProbeAddress))]
    private async Task ProbeAddressAsync()
    {
        ErrorText = null;
        string address = ManualAddress.Trim();
        if (address.Length == 0)
        {
            Validation.ShowAll(nameof(ManualAddress));
            return;
        }

        ScanStatusText = $"Looking for a device at {address}";
        IsProgressIndeterminate = true;
        if (await StartSessionAsync(() => _api.ProbeAddressAsync(address, _cts.Token), address).ConfigureAwait(true))
        {
            ManualAddress = "";
            Validation.Reset(nameof(ManualAddress)); // ready for the next address, no "Enter an address" yet
        }
    }

    private bool CanProbeAddress() => Validation.IsValidFor(nameof(ManualAddress));

    /// <summary>Checks every device that can be added (authenticated, or factory default with a password ready).</summary>
    [RelayCommand]
    private void SelectAllAuthenticated()
    {
        foreach (DiscoveredRowViewModel row in FilteredRows.Where(r => r.CanAdd))
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        List<DiscoveredRowViewModel> selected = Rows.Where(r => r.IsSelected && r.CanAdd).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        ErrorText = null;
        int added = 0;
        try
        {
            IsBusy = true;
            foreach (IGrouping<string, DiscoveredRowViewModel> session in selected.GroupBy(r => r.SessionId))
            {
                var request = new CommitRequest { SessionId = session.Key };
                foreach (DiscoveredRowViewModel row in session)
                {
                    request.DiscoveredIds.Add(row.DiscoveredId);
                    if (row.PendingPassword is { } password)
                    {
                        request.InitialPasswords[row.DiscoveredId] = password;
                    }
                }

                CommitReply reply = await _api.CommitAddAsync(request, _cts.Token).ConfigureAwait(true);
                foreach (CommitResult result in reply.Results.Where(r => r.DeviceId.Length > 0))
                {
                    AddedDeviceIds.Add(result.DeviceId);
                    _rowsById.GetValueOrDefault(result.DiscoveredId)?.MarkAdded();
                    added++;
                }

                if (reply.Results.Count == 0)
                {
                    // older server: no per-device results
                    AddedDeviceIds.AddRange(reply.DeviceIds);
                    foreach (DiscoveredRowViewModel row in session)
                    {
                        row.MarkAdded();
                    }

                    added += reply.DeviceIds.Count;
                }
            }

            LogAdded(_logger, added);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = "Adding the devices failed: " + Message(ex);
            return;
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }

        // User decision: the page always closes after adding.
        CloseRequested?.Invoke(this, AddedDeviceIds.Count > 0);
    }

    private bool CanAdd() => SelectedCount > 0 && !IsBusy;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, AddedDeviceIds.Count > 0);

    /// <summary>"Log in" / "Set password" / "Change" link, or a click on the row.</summary>
    [RelayCommand]
    private void OpenEditor(DiscoveredRowViewModel? row)
    {
        if (row is null || row.IsMuted)
        {
            return;
        }

        if (row.ShowLogIn)
        {
            EditorRow = row;
            EditorTitle = $"Log in to {row.Address} ({row.Serial})";
            EditorUserName = row.AuthUserName.Length > 0 ? row.AuthUserName : EditorUserName;
            EditorPassword = "";
            Editor = AddEditor.Login;
        }
        else if (row.IsFactoryDefault)
        {
            EditorRow = row;
            EditorTitle = $"Set the first password of {row.Address} ({row.Serial})";
            NewPassword = row.PendingPassword ?? "";
            ConfirmPassword = row.PendingPassword ?? "";
            PolicyHint = "User root. " + PasswordRules.Hint(row.PassphrasePolicy);
            Editor = AddEditor.Password;
        }

        Validation.Reset(EditorFields); // a freshly opened editor shows no errors
    }

    [RelayCommand]
    private void CancelEditor()
    {
        Editor = AddEditor.None;
        EditorRow = null;
        EditorPassword = "";
        NewPassword = "";
        ConfirmPassword = "";
        Validation.Reset(EditorFields);
    }

    /// <summary>Login editor: the server logs in with the entered credentials right away.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        DiscoveredRowViewModel? row = EditorRow;
        if (row is null || Editor != AddEditor.Login)
        {
            return;
        }

        if (!Validation.IsValidFor(LoginFields))
        {
            Validation.ShowAll(LoginFields);
            return;
        }

        try
        {
            IsRetrying = true;
            var request = new RetryAuthRequest
            {
                SessionId = row.SessionId,
                DiscoveredId = row.DiscoveredId,
                UserName = EditorUserName.Trim(),
                Password = EditorPassword,
                SaveToCredentialList = SaveToCredentialList,
            };
            request.RelatedSessionIds.AddRange(_sessions.Where(s => s != row.SessionId));
            DiscoveredDevice reply = await _api.RetryAuthAsync(request, _cts.Token).ConfigureAwait(true);
            row.Update(reply);
            if (row.AuthState == AuthState.Authenticated)
            {
                row.IsSelected = true;
                CancelEditor();

                // The server now tries the credential on every other device whose login failed; sessions whose
                // stream already ended are watched again to receive those results.
                foreach (string session in Rows.Where(r => r.ShowLogIn).Select(r => r.SessionId).Distinct().Where(s => !_watching.Contains(s)).ToList())
                {
                    StartWatch(session);
                }
            }
            else
            {
                // The device's answer belongs to the credentials: below the password, until it is edited.
                Validation.SetServerError(nameof(EditorPassword), reply.AuthDetail.Length > 0 ? reply.AuthDetail : "The login failed.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Validation.SetServerError(nameof(EditorPassword), Message(ex));
        }
        finally
        {
            IsRetrying = false;
            UpdateSummary();
        }
    }

    private bool CanRetry() => !IsRetrying && Validation.IsValidFor(LoginFields);

    /// <summary>A login error of the server is about the pair: editing the user name clears it as well.</summary>
    partial void OnEditorUserNameChanged(string value) => Validation.SetServerError(nameof(EditorPassword), null);

    /// <summary>Password editor: keeps the password for the add (and for all factory-default devices when asked).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyPassword))]
    private void ApplyPassword()
    {
        DiscoveredRowViewModel? row = EditorRow;
        if (row is null || Editor != AddEditor.Password)
        {
            return;
        }

        if (!Validation.IsValidFor(PasswordFields))
        {
            Validation.ShowAll(PasswordFields);
            return;
        }

        List<DiscoveredRowViewModel> targets = UseForAllFactoryDefault
            ? Rows.Where(r => r.IsFactoryDefault).ToList()
            : [row];

        foreach (DiscoveredRowViewModel target in targets)
        {
            target.SetPendingPassword(NewPassword);
            target.IsSelected = true;
        }

        CancelEditor();
        UpdateSummary();
    }

    private bool CanApplyPassword() => Validation.IsValidFor(PasswordFields);

    // ------------------------------------------------------------ discovery

    /// <returns>True when the session started.</returns>
    private async Task<bool> StartSessionAsync(Func<Task<string>> start, string? manualInput)
    {
        string sessionId;
        try
        {
            _starting++;
            UpdateScanning();
            if (manualInput is null)
            {
                HasScanned = true;
            }

            sessionId = await start().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (manualInput is null)
            {
                ErrorText = "Discovery could not be started: " + Message(ex);
            }
            else
            {
                Validation.SetServerError(nameof(ManualAddress), Message(ex)); // e.g. an unusable address
            }

            _starting--;
            ScanFinished(null);
            return false;
        }

        _starting--;
        _sessions.Add(sessionId);
        _scanning.Add(sessionId);
        if (manualInput is not null)
        {
            _manualInputs[sessionId] = manualInput;
        }

        StartWatch(sessionId);
        return true;
    }

    /// <summary>Opens the watch stream of a session (UI thread). Used again after a retry for sessions whose stream ended.</summary>
    private void StartWatch(string sessionId)
    {
        _watching.Add(sessionId);
        CancellationToken ct = _cts.Token;
        _ = Task.Run(() => WatchAsync(sessionId, ct), ct);
    }

    private async Task WatchAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await foreach (DiscoveredDevice found in _api.WatchDiscoveredAsync(sessionId, ct).ConfigureAwait(false))
            {
                _ui.Post(() => OnDiscovered(sessionId, found));
            }

            _ui.Post(() =>
            {
                _watching.Remove(sessionId);
                ReleaseImportSlot(sessionId);
            });
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                _watching.Remove(sessionId);
                ReleaseImportSlot(sessionId);
                if (_scanning.Contains(sessionId))
                {
                    ErrorText = "Discovery stopped: " + Message(ex);
                    ScanFinished(sessionId);
                }
                else
                {
                    LogWatchFailed(_logger, ex.Message); // a watch after a retry; the rows keep their last state
                }
            });
        }
    }

    private void UpdateScanning() => IsScanning = _starting > 0 || _scanning.Count > 0 || _importRunning;

    internal void OnDiscovered(string sessionId, DiscoveredDevice found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (!IsScanMode && !IsImportMode && _scanning.Contains(sessionId) && found.ProgressPercent > 0 && !found.ScanFinished)
        {
            ScanProgress = Math.Clamp(found.ProgressPercent, 0, 100);
        }

        if (!string.IsNullOrEmpty(found.DiscoveredId) && !_duplicateSessions.Contains(sessionId))
        {
            // Scale: O(1) lookups; a /16 range scan can report thousands of devices, each several times.
            DiscoveredRowViewModel? row = _rowsById.GetValueOrDefault(found.DiscoveredId)
                ?? (found.Serial.Length > 0 ? FindBySerial(found.Serial) : null);
            _importRows.Remove(sessionId, out DiscoveredRowViewModel? line);
            if (line is not null && row is not null && row != line)
            {
                // Import: another line (another address) already found this device.
                line.SetImportProblem("Not added", $"Same device as {row.Address} ({row.Serial}).");
                _duplicateSessions.Add(sessionId);
            }
            else if (line is not null && row is null)
            {
                // Import: the line's own row shows the device it found (file order stays).
                _rowsById.Remove(line.DiscoveredId);
                line.Adopt(found, sessionId);
                _rowsById.TryAdd(line.DiscoveredId, line);
                if (!line.IsAlreadyManaged && !line.Matches(SearchText))
                {
                    FilteredRows.Remove(line); // the search no longer matches (managed ones leave in OnRowPropertyChanged)
                }
            }
            else if (row is null)
            {
                row = new DiscoveredRowViewModel(found, sessionId);
                row.PropertyChanged += OnRowPropertyChanged;
                _rowsById.TryAdd(row.DiscoveredId, row);
                Rows.Add(row);
                if (IsListed(row, SearchText))
                {
                    FilteredRows.Add(row);
                }
            }
            else if (!row.IsAdded && !(found.AuthState == AuthState.Pending && row.AuthState != AuthState.Pending && row.SessionId != sessionId))
            {
                // (Scan again: "Checking" of the new search does not hide the result of the earlier one, the selection stays.)
                row.SessionId = sessionId;
                row.Update(found);
            }
        }

        if (found.ScanFinished)
        {
            if (_manualInputs.Remove(sessionId, out string? input) && !Rows.Any(r => r.SessionId == sessionId))
            {
                Validation.SetServerError(nameof(ManualAddress), $"No Axis device answered at {input}.");
            }

            if (_importRows.Remove(sessionId, out DiscoveredRowViewModel? notFound))
            {
                notFound.SetImportProblem("Not found", $"No Axis device answered at {notFound.Address}.");
            }

            if (IsImportMode && _scanning.Contains(sessionId))
            {
                ImportLineDone();
            }

            ScanFinished(sessionId);
        }

        UpdateSummary();
    }

    /// <summary>
    /// A session's scan ended (scan_finished, stream error, or start failed with <paramref name="sessionId"/> null).
    /// A replayed scan_finished of an earlier session is ignored. When the last scan ended the progress
    /// row says "Scan finished, N devices found" (or "Scan stopped, ..." after Stop).
    /// </summary>
    private void ScanFinished(string? sessionId)
    {
        if (sessionId is not null)
        {
            if (!_scanning.Remove(sessionId))
            {
                return;
            }

            _anyStopped |= _stopRequested.Remove(sessionId);
        }

        if (_starting == 0 && _scanning.Count == 0 && !_importRunning)
        {
            // Text first, then IsScanning: whoever reacts to IsScanning sees the final text.
            ScanProgress = 100;
            IsProgressIndeterminate = false;
            int listed = Rows.Count - _hidden - _noDevice;
            string found = listed == 1 ? "1 device found" : string.Create(CultureInfo.CurrentCulture, $"{listed} devices found");
            if (_hidden > 0)
            {
                found += string.Create(CultureInfo.CurrentCulture, $", {_hidden} already added");
            }
            string what = IsImportMode ? "Import" : "Scan";
            ScanStatusText = what + (_anyStopped ? " stopped, " : " finished, ") + found;
        }

        UpdateScanning();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoveredRowViewModel.Serial) && sender is DiscoveredRowViewModel { Serial.Length: > 0 } withSerial)
        {
            _rowsBySerial.TryAdd(withSerial.Serial, withSerial);
        }

        if (e.PropertyName == nameof(DiscoveredRowViewModel.IsAlreadyManaged) && sender is DiscoveredRowViewModel { IsAlreadyManaged: true } managed)
        {
            // A device found as "Checking" turned out to be managed already: it leaves the list.
            FilteredRows.Remove(managed);
            if (FocusedRow == managed)
            {
                FocusedRow = null;
            }
        }

        bool countsChanged = sender is DiscoveredRowViewModel row && Recount(row);
        if (countsChanged || e.PropertyName is nameof(DiscoveredRowViewModel.IsSelected) or nameof(DiscoveredRowViewModel.CanAdd) or nameof(DiscoveredRowViewModel.ChipText))
        {
            UpdateSummary();
        }
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (DiscoveredRowViewModel row in e.NewItems.OfType<DiscoveredRowViewModel>())
            {
                Recount(row);
                if (row.Serial.Length > 0)
                {
                    _rowsBySerial.TryAdd(row.Serial, row);
                }
            }
        }

        UpdateSummary();
    }

    /// <summary>The first row with the serial; rows are indexed when added and when they learn a serial.</summary>
    private DiscoveredRowViewModel? FindBySerial(string serial) =>
        _rowsBySerial.TryGetValue(serial, out DiscoveredRowViewModel? row) && row.Serial == serial ? row : null;

    /// <summary>Updates the summary counters for one row in O(1); true when they changed.</summary>
    private bool Recount(DiscoveredRowViewModel row)
    {
        var now = new RowCounts(row.CanAdd, row.ShowLogIn, row.ShowSetPassword, row.IsSelected && row.CanAdd, row.IsAlreadyManaged,
            row.IsImportPlaceholder, row.HasImportProblem);
        if (_counted.TryGetValue(row, out RowCounts old))
        {
            if (old == now)
            {
                return false;
            }

            Count(old, -1);
        }

        Count(now, 1);
        _counted[row] = now;
        return true;
    }

    private void Count(RowCounts counts, int delta)
    {
        _ready += counts.Ready ? delta : 0;
        _failed += counts.NeedsLogin ? delta : 0;
        _factory += counts.NeedsPassword ? delta : 0;
        _selected += counts.Selected ? delta : 0;
        _hidden += counts.Hidden ? delta : 0;
        _noDevice += counts.NoDevice ? delta : 0;
        _problems += counts.Problem ? delta : 0;
    }

    /// <summary>What a row adds to the summary ("ready to add", "need a login", "need a password", "selected").</summary>
    private readonly record struct RowCounts(bool Ready, bool NeedsLogin, bool NeedsPassword, bool Selected, bool Hidden, bool NoDevice, bool Problem);

    // Scale: per-row bookkeeping so a discovered device, a login result or a checkbox costs O(1), not a
    // scan of every row (4 counts over 5,000 rows per change made "select all" O(n^2)).
    private readonly Dictionary<string, DiscoveredRowViewModel> _rowsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiscoveredRowViewModel> _rowsBySerial = new(StringComparer.Ordinal);
    private readonly Dictionary<DiscoveredRowViewModel, RowCounts> _counted = [];
    private int _ready;
    private int _failed;
    private int _factory;
    private int _selected;
    private int _hidden;
    private int _noDevice;
    private int _problems;

    /// <summary>
    /// Devices OADM already manages are not listed at all (user decision); only the scan status
    /// line counts them ("Scan finished, 8 devices found, 3 already added"). They stay in <see cref="Rows"/> so a later
    /// answer of the same device is merged, not added.
    /// </summary>
    private static bool IsListed(DiscoveredRowViewModel row, string? search) => !row.IsAlreadyManaged && row.Matches(search);

    partial void OnSearchTextChanged(string value)
    {
        // One Reset instead of one event per row.
        FilteredRows.ReplaceAll(Rows.Where(r => IsListed(r, value)).ToList());
    }

    partial void OnFocusedRowChanged(DiscoveredRowViewModel? value)
    {
        if (value is not null && (value.ShowLogIn || value.IsFactoryDefault))
        {
            OpenEditor(value);
        }
    }

    private void UpdateSummary()
    {
        int ready = _ready;
        int failed = _failed;
        int factory = _factory;
        SelectedCount = _selected;
        var parts = new List<string> { string.Create(CultureInfo.CurrentCulture, $"{Rows.Count - _hidden - _noDevice} found"), string.Create(CultureInfo.CurrentCulture, $"{ready} ready to add") };
        if (failed > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{failed} need a login"));
        }

        if (factory > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{factory} need a password"));
        }

        if (_problems > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{_problems} not added"));
        }

        parts.Add(string.Create(CultureInfo.CurrentCulture, $"{SelectedCount} selected"));
        SummaryText = string.Join(" · ", parts);
        AddButtonText = SelectedCount switch
        {
            0 => "Add",
            1 => "Add 1 device",
            _ => string.Create(CultureInfo.CurrentCulture, $"Add {SelectedCount} devices"),
        };
    }

    private static string Message(Exception ex) => ex is RpcException rpc ? rpc.Status.Detail : ex.Message;

    /// <summary>Error of the start address field, null when valid.</summary>
    public static string? RangeStartError(string from) =>
        (from ?? "").Trim().Length == 0 ? "Enter the first address of the range."
        : !TryParseIPv4(from, out _) ? "Enter a valid IPv4 address, e.g. 192.168.0.1." : null;

    /// <summary>Error of the end address field (checked against a valid start), null when valid.</summary>
    public static string? RangeEndError(string from, string to)
    {
        if ((to ?? "").Trim().Length == 0)
        {
            return "Enter the last address of the range.";
        }

        if (!TryParseIPv4(to, out uint last))
        {
            return "Enter a valid IPv4 address, e.g. 192.168.0.254.";
        }

        if (!TryParseIPv4(from, out uint first))
        {
            return null; // the start field says what is wrong
        }

        if (last < first)
        {
            return "The last address must not be lower than the first.";
        }

        return last - first + 1 > MaxRangeSize
            ? string.Create(CultureInfo.CurrentCulture, $"The range may contain at most {MaxRangeSize} addresses.")
            : null;
    }

    public static bool TryParseRange(string from, string to, out string? error)
    {
        error = null;
        if (!TryParseIPv4(from, out uint first))
        {
            error = "The start address is not a valid IPv4 address.";
            return false;
        }

        if (!TryParseIPv4(to, out uint last))
        {
            error = "The end address is not a valid IPv4 address.";
            return false;
        }

        if (last < first)
        {
            error = "The end address must not be lower than the start address.";
            return false;
        }

        if (last - first + 1 > MaxRangeSize)
        {
            error = string.Create(CultureInfo.CurrentCulture, $"The range may contain at most {MaxRangeSize} addresses.");
            return false;
        }

        return true;
    }

    private static bool TryParseIPv4(string? text, out uint value)
    {
        value = 0;
        string trimmed = (text ?? "").Trim();
        if (trimmed.Count(c => c == '.') != 3 || !IPAddress.TryParse(trimmed, out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = address.GetAddressBytes();
        value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Add devices page added {Count} device(s)")]
    private static partial void LogAdded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopping discovery failed: {Reason}")]
    private static partial void LogStopFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Watching discovery results after a retry failed: {Reason}")]
    private static partial void LogWatchFailed(ILogger logger, string reason);
}
