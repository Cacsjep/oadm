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
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

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
public sealed partial class AddDevicesViewModel : ObservableObject, IAsyncDisposable
{
    public const int MaxRangeSize = 65536;

    private readonly IOadmApi _api;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _sessions = [];
    private readonly Dictionary<string, string> _manualInputs = [];
    private int _scansRunning;

    public AddDevicesViewModel(IOadmApi api, IUiDispatcher ui, ILogger<AddDevicesViewModel> logger, AddDevicesMode mode)
    {
        _api = api;
        _ui = ui;
        _logger = logger;
        Mode = mode;
        Rows.CollectionChanged += (_, _) => UpdateSummary();
    }

    public AddDevicesMode Mode { get; }
    public bool IsScanMode => Mode == AddDevicesMode.Scan;
    public bool IsRangeMode => Mode == AddDevicesMode.IpRange;
    public bool IsManualMode => Mode == AddDevicesMode.Manual;

    public static string Title => "Add devices";

    public string HeaderTitle => Mode switch
    {
        AddDevicesMode.IpRange => "Scan IP range",
        AddDevicesMode.Manual => "Add manually",
        _ => "Devices on the network",
    };

    public string HeaderDescription => Mode switch
    {
        AddDevicesMode.IpRange => "Enter the first and last IPv4 address and press Enter. Every address is probed on HTTPS (443) and HTTP (80).",
        AddDevicesMode.Manual => "Enter an IP address or host name, optionally with port or scheme (https://camera.example.com:8443), and press Enter.",
        _ => "Found with zero-configuration (Bonjour), live while this page is open.",
    } + " OADM logs in with your known credentials; authenticated devices can be added right away.";

    public ObservableCollection<DiscoveredRowViewModel> Rows { get; } = [];
    public ObservableCollection<DiscoveredRowViewModel> FilteredRows { get; } = [];

    /// <summary>Device ids added while the page was open.</summary>
    public List<string> AddedDeviceIds { get; } = [];

    /// <summary>Raised when the page should close; true = devices were added.</summary>
    public event EventHandler<bool>? CloseRequested;

    // ------------------------------------------------------------ inputs and state

    [ObservableProperty] public partial string RangeFrom { get; set; } = "";
    [ObservableProperty] public partial string RangeTo { get; set; } = "";
    [ObservableProperty] public partial string ManualAddress { get; set; } = "";
    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty] public partial bool IsScanning { get; private set; }
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

    /// <summary>Stay open after adding (add more devices from the same list).</summary>
    [ObservableProperty] public partial bool KeepOpen { get; set; }

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
    [ObservableProperty] public partial string? EditorError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool IsRetrying { get; private set; }

    // ------------------------------------------------------------ lifecycle

    /// <summary>Called when the page opens. Scan mode starts discovery immediately.</summary>
    public async Task OpenAsync()
    {
        if (IsScanMode)
        {
            ScanStatusText = "Searching the network...";
            IsProgressIndeterminate = true;
            await StartSessionAsync(() => _api.StartZeroConfAsync(_cts.Token), null).ConfigureAwait(true);
        }
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
    }

    // ------------------------------------------------------------ commands

    /// <summary>Range mode: Enter or the Scan button.</summary>
    [RelayCommand]
    private async Task StartRangeAsync()
    {
        ErrorText = null;
        if (!TryParseRange(RangeFrom, RangeTo, out string? error))
        {
            ErrorText = error;
            return;
        }

        string from = RangeFrom.Trim();
        string to = RangeTo.Trim();
        ScanStatusText = $"Scanning {from} - {to}";
        ScanProgress = 0;
        IsProgressIndeterminate = false;
        await StartSessionAsync(() => _api.StartRangeScanAsync(from, to, _cts.Token), null).ConfigureAwait(true);
    }

    /// <summary>Manual mode: Enter or the Find button. Every address adds to the list.</summary>
    [RelayCommand]
    private async Task ProbeAddressAsync()
    {
        ErrorText = null;
        string address = ManualAddress.Trim();
        if (address.Length == 0)
        {
            ErrorText = "Enter an IP address or host name.";
            return;
        }

        ScanStatusText = $"Looking for a device at {address}";
        IsProgressIndeterminate = true;
        if (await StartSessionAsync(() => _api.ProbeAddressAsync(address, _cts.Token), address).ConfigureAwait(true))
        {
            ManualAddress = "";
        }
    }

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
                    session.FirstOrDefault(r => r.DiscoveredId == result.DiscoveredId)?.MarkAdded();
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

        if (!KeepOpen)
        {
            CloseRequested?.Invoke(this, AddedDeviceIds.Count > 0);
        }
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

        EditorError = null;
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
    }

    [RelayCommand]
    private void CancelEditor()
    {
        Editor = AddEditor.None;
        EditorRow = null;
        EditorPassword = "";
        NewPassword = "";
        ConfirmPassword = "";
        EditorError = null;
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

        if (EditorUserName.Trim().Length == 0 || EditorPassword.Length == 0)
        {
            EditorError = "Enter a user name and a password.";
            return;
        }

        EditorError = null;
        try
        {
            IsRetrying = true;
            DiscoveredDevice reply = await _api.RetryAuthAsync(new RetryAuthRequest
            {
                SessionId = row.SessionId,
                DiscoveredId = row.DiscoveredId,
                UserName = EditorUserName.Trim(),
                Password = EditorPassword,
                SaveToCredentialList = SaveToCredentialList,
            }, _cts.Token).ConfigureAwait(true);
            row.Update(reply);
            if (row.AuthState == AuthState.Authenticated)
            {
                row.IsSelected = true;
                CancelEditor();
            }
            else
            {
                EditorError = reply.AuthDetail.Length > 0 ? reply.AuthDetail : "The login failed.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EditorError = Message(ex);
        }
        finally
        {
            IsRetrying = false;
            UpdateSummary();
        }
    }

    private bool CanRetry() => !IsRetrying;

    /// <summary>Password editor: keeps the password for the add (and for all factory-default devices when asked).</summary>
    [RelayCommand]
    private void ApplyPassword()
    {
        DiscoveredRowViewModel? row = EditorRow;
        if (row is null || Editor != AddEditor.Password)
        {
            return;
        }

        List<DiscoveredRowViewModel> targets = UseForAllFactoryDefault
            ? Rows.Where(r => r.IsFactoryDefault).ToList()
            : [row];
        foreach (string? policy in targets.Select(r => r.PassphrasePolicy).Distinct())
        {
            string? error = PasswordRules.Validate(NewPassword, ConfirmPassword, policy);
            if (error is not null)
            {
                EditorError = error.Length == 0 ? "Enter the password twice." : error;
                return;
            }
        }

        foreach (DiscoveredRowViewModel target in targets)
        {
            target.SetPendingPassword(NewPassword);
            target.IsSelected = true;
        }

        CancelEditor();
        UpdateSummary();
    }

    // ------------------------------------------------------------ discovery

    /// <returns>True when the session started.</returns>
    private async Task<bool> StartSessionAsync(Func<Task<string>> start, string? manualInput)
    {
        string sessionId;
        try
        {
            IsScanning = true;
            Interlocked.Increment(ref _scansRunning);
            sessionId = await start().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = (manualInput is null ? "Discovery could not be started: " : "") + Message(ex);
            ScanFinished();
            return false;
        }

        _sessions.Add(sessionId);
        if (manualInput is not null)
        {
            _manualInputs[sessionId] = manualInput;
        }

        CancellationToken ct = _cts.Token;
        _ = Task.Run(() => WatchAsync(sessionId, ct), ct);
        return true;
    }

    private async Task WatchAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await foreach (DiscoveredDevice found in _api.WatchDiscoveredAsync(sessionId, ct).ConfigureAwait(false))
            {
                _ui.Post(() => OnDiscovered(sessionId, found));
            }
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                ErrorText = "Discovery stopped: " + Message(ex);
                ScanFinished();
            });
        }
    }

    internal void OnDiscovered(string sessionId, DiscoveredDevice found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (!IsScanMode && found.ProgressPercent > 0 && !found.ScanFinished)
        {
            ScanProgress = Math.Clamp(found.ProgressPercent, 0, 100);
        }

        if (!string.IsNullOrEmpty(found.DiscoveredId))
        {
            DiscoveredRowViewModel? row = Rows.FirstOrDefault(r => r.DiscoveredId == found.DiscoveredId)
                ?? Rows.FirstOrDefault(r => found.Serial.Length > 0 && r.Serial == found.Serial);
            if (row is null)
            {
                row = new DiscoveredRowViewModel(found, sessionId);
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
                if (row.Matches(SearchText))
                {
                    FilteredRows.Add(row);
                }
            }
            else if (!row.IsAdded)
            {
                row.SessionId = sessionId;
                row.Update(found);
            }
        }

        if (found.ScanFinished)
        {
            if (_manualInputs.Remove(sessionId, out string? input) && !Rows.Any(r => r.SessionId == sessionId))
            {
                ErrorText = $"No Axis device answered at {input}.";
            }

            ScanFinished();
        }

        UpdateSummary();
    }

    private void ScanFinished()
    {
        if (Interlocked.Decrement(ref _scansRunning) > 0)
        {
            return;
        }

        _scansRunning = 0;
        IsScanning = false;
        ScanProgress = 100;
        IsProgressIndeterminate = false;
        ScanStatusText = string.Create(CultureInfo.CurrentCulture, $"Done, {Rows.Count} device(s) found");
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscoveredRowViewModel.IsSelected) or nameof(DiscoveredRowViewModel.CanAdd) or nameof(DiscoveredRowViewModel.ChipText))
        {
            UpdateSummary();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredRows.Clear();
        foreach (DiscoveredRowViewModel row in Rows.Where(r => r.Matches(value)))
        {
            FilteredRows.Add(row);
        }
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
        int ready = Rows.Count(r => r.CanAdd);
        int failed = Rows.Count(r => r.ShowLogIn);
        int factory = Rows.Count(r => r.ShowSetPassword);
        SelectedCount = Rows.Count(r => r.IsSelected && r.CanAdd);
        var parts = new List<string> { string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} found"), string.Create(CultureInfo.CurrentCulture, $"{ready} ready to add") };
        if (failed > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{failed} need a login"));
        }

        if (factory > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{factory} need a password"));
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
}
