using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Discovery;

public enum AddDevicesMode
{
    ZeroConf,
    IpRange,
}

public enum WizardStep
{
    IpRange,
    Select,
    HostName,
    Password,
    Credentials,
    Review,
}

/// <summary>
/// The ADM add devices wizard: (IP range) - select devices - host name - set password - credentials - review.
/// All device work is done by the server; this view model only drives the steps.
/// </summary>
public sealed partial class AddDevicesWizardViewModel : ObservableObject, IAsyncDisposable
{
    public const int MaxRangeSize = 65536;

    private readonly IOadmApi _api;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private string? _sessionId;
    private AddPlan _plan = new();
    private bool _revalidating;

    public AddDevicesWizardViewModel(IOadmApi api, IUiDispatcher ui, ILogger<AddDevicesWizardViewModel> logger, AddDevicesMode mode)
    {
        _api = api;
        _ui = ui;
        _logger = logger;
        Mode = mode;
        if (mode == AddDevicesMode.IpRange)
        {
            Steps.Add(new WizardStepItem(WizardStep.IpRange, "IP range"));
        }

        Steps.Add(new WizardStepItem(WizardStep.Select, "Select devices"));
        Steps.Add(new WizardStepItem(WizardStep.HostName, "Host name"));
        Steps.Add(new WizardStepItem(WizardStep.Password, "Set password"));
        Steps.Add(new WizardStepItem(WizardStep.Credentials, "Credentials"));
        Steps.Add(new WizardStepItem(WizardStep.Review, "Review"));
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].Number = i + 1;
        }

        CurrentStep = mode == AddDevicesMode.IpRange ? WizardStep.IpRange : WizardStep.Select;
        Discovered.CollectionChanged += (_, _) => Revalidate();
        UpdateStepItems();
    }

    public AddDevicesMode Mode { get; }
    public bool IsRangeMode => Mode == AddDevicesMode.IpRange;
    public string Title => IsRangeMode ? "Add devices from IP range" : "Add devices";

    public ObservableCollection<WizardStepItem> Steps { get; } = [];
    public ObservableCollection<DiscoveredRowViewModel> Discovered { get; } = [];
    public ObservableCollection<DiscoveredRowViewModel> FilteredDiscovered { get; } = [];
    public ObservableCollection<PlanRowViewModel> PasswordDevices { get; } = [];
    public ObservableCollection<CredentialRowViewModel> CredentialDevices { get; } = [];
    public ObservableCollection<ReviewRow> ReviewRows { get; } = [];

    /// <summary>Set after a successful Finish.</summary>
    public CommitReply? Result { get; private set; }

    /// <summary>Raised when the dialog should close; true = devices were added.</summary>
    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIpRangeStep), nameof(IsSelectStep), nameof(IsHostNameStep), nameof(IsPasswordStep),
        nameof(IsCredentialsStep), nameof(IsReviewStep), nameof(CanGoBack), nameof(IsLastStep), nameof(StepTitle), nameof(StepDescription))]
    public partial WizardStep CurrentStep { get; private set; }

    public bool IsIpRangeStep => CurrentStep == WizardStep.IpRange;
    public bool IsSelectStep => CurrentStep == WizardStep.Select;
    public bool IsHostNameStep => CurrentStep == WizardStep.HostName;
    public bool IsPasswordStep => CurrentStep == WizardStep.Password;
    public bool IsCredentialsStep => CurrentStep == WizardStep.Credentials;
    public bool IsReviewStep => CurrentStep == WizardStep.Review;
    public bool IsLastStep => CurrentStep == WizardStep.Review;
    public bool CanGoBack => CurrentStep != FirstStep && !IsBusy;

    public string StepTitle => CurrentStep switch
    {
        WizardStep.IpRange => "Enter IP range",
        WizardStep.Select => "Select devices",
        WizardStep.HostName => "Host name",
        WizardStep.Password => "Set password",
        WizardStep.Credentials => "Enter credentials",
        _ => "Review",
    };

    public string StepDescription => CurrentStep switch
    {
        WizardStep.IpRange => "Enter the first and last IPv4 address to scan. Every address is probed on HTTPS (443) and HTTP (80).",
        WizardStep.Select => IsRangeMode
            ? "Devices found in the IP range. Select the devices to add."
            : "Devices found on the network with zero-configuration (Bonjour). Select the devices to add.",
        WizardStep.HostName => "Choose how the devices are addressed.",
        WizardStep.Password => "These devices have no password yet (factory default). Set a password for the root account, or skip to add them without one.",
        WizardStep.Credentials => "These devices already have a password. Enter the credentials to use.",
        _ => "Check what will happen and click Finish to add the devices.",
    };

    private WizardStep FirstStep => IsRangeMode ? WizardStep.IpRange : WizardStep.Select;

    // ------------------------------------------------------------ state

    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    [ObservableProperty] public partial bool CanGoNext { get; private set; }

    // IP range
    [ObservableProperty] public partial string RangeFrom { get; set; } = "";
    [ObservableProperty] public partial string RangeTo { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial int ScanProgress { get; private set; }
    [ObservableProperty] public partial string ScanStatusText { get; private set; } = "";

    // Select
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string SelectionText { get; private set; } = "0 devices found, 0 selected";
    [ObservableProperty] public partial int SelectedCount { get; private set; }

    // Host name
    [ObservableProperty] public partial bool UseHostName { get; set; }

    // Password
    [ObservableProperty] public partial string NewPassword { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";
    [ObservableProperty] public partial string? PasswordError { get; private set; }
    [ObservableProperty] public partial bool SkipPassword { get; private set; }

    // Credentials
    [ObservableProperty] public partial string UserName { get; set; } = "root";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool UseForAll { get; set; } = true;

    public bool ShowPerDeviceCredentials => !UseForAll;

    public static string PasswordRules => "1 to 64 characters. Allowed: printable ASCII characters (letters, digits, space and !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~).";

    // ------------------------------------------------------------ lifecycle

    /// <summary>Called when the dialog opens. Zero-conf starts browsing immediately.</summary>
    public async Task OpenAsync()
    {
        if (IsRangeMode)
        {
            return;
        }

        await StartDiscoveryAsync(() => _api.StartZeroConfAsync(_cts.Token)).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_sessionId is not null)
        {
            try
            {
                await _api.StopDiscoveryAsync(_sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStopFailed(_logger, ex.Message);
            }
        }

        _cts.Dispose();
    }

    // ------------------------------------------------------------ commands

    [RelayCommand]
    private void SelectAll()
    {
        foreach (DiscoveredRowViewModel row in FilteredDiscovered.Where(r => r.IsSelectable))
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (DiscoveredRowViewModel row in Discovered)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanGoNext || IsBusy)
        {
            return;
        }

        ErrorText = null;
        switch (CurrentStep)
        {
            case WizardStep.IpRange:
                await StartRangeScanAsync().ConfigureAwait(true);
                break;
            case WizardStep.Select:
                await PrepareAsync().ConfigureAwait(true);
                break;
            case WizardStep.HostName:
                GoTo(NextApplicable(WizardStep.HostName));
                break;
            case WizardStep.Password:
                SkipPassword = false;
                GoTo(NextApplicable(WizardStep.Password));
                break;
            case WizardStep.Credentials:
                GoTo(WizardStep.Review);
                break;
            case WizardStep.Review:
                await FinishAsync().ConfigureAwait(true);
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        ErrorText = null;
        WizardStep step = CurrentStep;
        do
        {
            step = step - 1;
        }
        while (step > FirstStep && !IsApplicable(step));

        if (step == WizardStep.IpRange && IsRangeMode)
        {
            // a new scan starts from scratch
            _ = StopSessionAsync();
        }

        GoTo(step);
    }

    /// <summary>"Skip" on the Set password step: the factory-default devices are added without a password.</summary>
    [RelayCommand]
    private void Skip()
    {
        if (CurrentStep != WizardStep.Password)
        {
            return;
        }

        SkipPassword = true;
        NewPassword = "";
        ConfirmPassword = "";
        PasswordError = null;
        GoTo(NextApplicable(WizardStep.Password));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    // ------------------------------------------------------------ step logic

    private async Task StartRangeScanAsync()
    {
        if (!TryParseRange(RangeFrom, RangeTo, out string? error))
        {
            ErrorText = error;
            return;
        }

        Discovered.Clear();
        FilteredDiscovered.Clear();
        ScanProgress = 0;
        IsScanning = true;
        ScanStatusText = $"Scanning {RangeFrom.Trim()} - {RangeTo.Trim()}";
        GoTo(WizardStep.Select);
        await StartDiscoveryAsync(() => _api.StartRangeScanAsync(RangeFrom.Trim(), RangeTo.Trim(), _cts.Token)).ConfigureAwait(true);
    }

    private async Task StartDiscoveryAsync(Func<Task<string>> start)
    {
        try
        {
            IsBusy = true;
            _sessionId = await start().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = "Discovery could not be started: " + ex.Message;
            IsScanning = false;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        string session = _sessionId;
        CancellationToken ct = _cts.Token;
        _ = Task.Run(() => WatchAsync(session, ct), ct);
    }

    private async Task WatchAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await foreach (DiscoveredDevice found in _api.WatchDiscoveredAsync(sessionId, ct).ConfigureAwait(false))
            {
                if (_sessionId != sessionId)
                {
                    return;
                }

                _ui.Post(() => OnDiscovered(found));
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
                IsScanning = false;
                ErrorText = "Discovery stopped: " + ex.Message;
            });
        }
    }

    internal void OnDiscovered(DiscoveredDevice found)
    {
        if (IsRangeMode && found.ProgressPercent > 0)
        {
            ScanProgress = Math.Clamp(found.ProgressPercent, 0, 100);
        }

        if (found.ScanFinished)
        {
            IsScanning = false;
            ScanProgress = 100;
            ScanStatusText = $"Scan finished, {Discovered.Count} device(s) found";
            Revalidate();
            return;
        }

        if (string.IsNullOrEmpty(found.DiscoveredId))
        {
            return;
        }

        DiscoveredRowViewModel? row = Discovered.FirstOrDefault(r => r.DiscoveredId == found.DiscoveredId)
            ?? Discovered.FirstOrDefault(r => found.Serial.Length > 0 && r.Serial == found.Serial);
        if (row is null)
        {
            row = new DiscoveredRowViewModel(found);
            row.PropertyChanged += OnRowPropertyChanged;
            Discovered.Add(row);
            if (row.Matches(SearchText))
            {
                FilteredDiscovered.Add(row);
            }
        }
        else
        {
            row.Update(found);
        }

        Revalidate();
    }

    private async Task PrepareAsync()
    {
        List<string> ids = Discovered.Where(r => r.IsSelected && r.IsSelectable).Select(r => r.DiscoveredId).ToList();
        if (ids.Count == 0 || _sessionId is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _plan = await _api.PrepareAddAsync(_sessionId, ids, _cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = "The server could not prepare the devices: " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        PasswordDevices.Clear();
        foreach (AddPlanItem item in _plan.Items.Where(i => i.NeedsInitialPassword))
        {
            PasswordDevices.Add(new PlanRowViewModel(item));
        }

        // keep per-device entries the user already typed when going back and forth
        Dictionary<string, CredentialRowViewModel> previous = CredentialDevices.ToDictionary(c => c.Item.DiscoveredId);
        CredentialDevices.Clear();
        foreach (AddPlanItem item in _plan.Items.Where(i => i.NeedsCredentials))
        {
            CredentialDevices.Add(previous.GetValueOrDefault(item.DiscoveredId) ?? new CredentialRowViewModel(item));
        }

        GoTo(WizardStep.HostName);
    }

    private async Task FinishAsync()
    {
        if (_sessionId is null)
        {
            return;
        }

        CommitRequest request = BuildCommitRequest();
        try
        {
            IsBusy = true;
            Result = await _api.CommitAddAsync(request, _cts.Token).ConfigureAwait(true);
            LogCommitted(_logger, Result.DeviceIds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = "Adding the devices failed: " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        CloseRequested?.Invoke(this, true);
    }

    internal CommitRequest BuildCommitRequest()
    {
        var request = new CommitRequest
        {
            SessionId = _sessionId ?? "",
            UseHostName = UseHostName,
            InitialRootPassword = PasswordDevices.Count > 0 && !SkipPassword ? NewPassword : "",
        };
        request.DiscoveredIds.AddRange(_plan.Items.Select(i => i.DiscoveredId));
        if (CredentialDevices.Count > 0)
        {
            if (UseForAll)
            {
                request.Credentials.Add(new DeviceCredentials { DiscoveredId = "", UserName = UserName.Trim(), Password = Password });
            }
            else
            {
                foreach (CredentialRowViewModel row in CredentialDevices.Where(r => r.Password.Length > 0))
                {
                    request.Credentials.Add(new DeviceCredentials
                    {
                        DiscoveredId = row.Item.DiscoveredId,
                        UserName = row.UserName.Trim(),
                        Password = row.Password,
                    });
                }
            }
        }

        return request;
    }

    private void BuildReview()
    {
        ReviewRows.Clear();
        foreach (AddPlanItem item in _plan.Items)
        {
            string address = UseHostName && item.HostName.Length > 0 ? item.HostName : item.Address;
            string action;
            bool warning = false;
            if (item.NeedsInitialPassword)
            {
                warning = SkipPassword;
                action = SkipPassword ? "Add without password (factory default)" : "Set root password and add";
            }
            else if (item.NeedsCredentials)
            {
                CredentialRowViewModel? row = CredentialDevices.FirstOrDefault(c => c.Item.DiscoveredId == item.DiscoveredId);
                (string user, string pw) = UseForAll ? (UserName.Trim(), Password) : (row?.UserName.Trim() ?? "", row?.Password ?? "");
                warning = pw.Length == 0;
                action = warning ? "Add, credentials required later" : $"Add with credentials for {user}";
            }
            else
            {
                action = "Add";
            }

            ReviewRows.Add(new ReviewRow(address, item.Serial, item.Model, action, warning));
        }
    }

    private bool IsApplicable(WizardStep step) => step switch
    {
        WizardStep.IpRange => IsRangeMode,
        WizardStep.Password => PasswordDevices.Count > 0,
        WizardStep.Credentials => CredentialDevices.Count > 0,
        _ => true,
    };

    private WizardStep NextApplicable(WizardStep from)
    {
        WizardStep step = from + 1;
        while (step < WizardStep.Review && !IsApplicable(step))
        {
            step++;
        }

        return step;
    }

    private void GoTo(WizardStep step)
    {
        if (step == WizardStep.Review)
        {
            BuildReview();
        }

        CurrentStep = step;
        UpdateStepItems();
        Revalidate();
    }

    private void UpdateStepItems()
    {
        bool planKnown = CurrentStep > WizardStep.Select;
        foreach (WizardStepItem item in Steps)
        {
            item.IsCurrent = item.Step == CurrentStep;
            item.IsDone = item.Step < CurrentStep;
            item.IsSkipped = planKnown && !IsApplicable(item.Step);
        }
    }

    private async Task StopSessionAsync()
    {
        string? session = _sessionId;
        _sessionId = null;
        IsScanning = false;
        if (session is not null)
        {
            try
            {
                await _api.StopDiscoveryAsync(session, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogStopFailed(_logger, ex.Message);
            }
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoveredRowViewModel.IsSelected))
        {
            Revalidate();
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(SearchText):
                ApplyFilter();
                break;
            case nameof(UseForAll):
                OnPropertyChanged(nameof(ShowPerDeviceCredentials));
                Revalidate();
                break;
            case nameof(IsBusy):
                OnPropertyChanged(nameof(CanGoBack));
                Revalidate();
                break;
            case nameof(RangeFrom) or nameof(RangeTo) or nameof(NewPassword) or nameof(ConfirmPassword) or nameof(UserName):
                Revalidate();
                break;
        }
    }

    private void ApplyFilter()
    {
        FilteredDiscovered.Clear();
        foreach (DiscoveredRowViewModel row in Discovered.Where(r => r.Matches(SearchText)))
        {
            FilteredDiscovered.Add(row);
        }
    }

    private void Revalidate()
    {
        if (_revalidating)
        {
            return;
        }

        _revalidating = true;
        try
        {
            SelectedCount = Discovered.Count(r => r.IsSelected);
            SelectionText = string.Create(CultureInfo.CurrentCulture, $"{Discovered.Count} devices found, {SelectedCount} selected");
            PasswordError = CurrentStep == WizardStep.Password ? ValidatePassword(NewPassword, ConfirmPassword) : null;
            CanGoNext = !IsBusy && CurrentStep switch
            {
                WizardStep.IpRange => RangeFrom.Trim().Length > 0 && RangeTo.Trim().Length > 0,
                WizardStep.Select => SelectedCount > 0 && _sessionId is not null,
                WizardStep.Password => PasswordError is null,
                WizardStep.Credentials => !UseForAll || UserName.Trim().Length > 0,
                _ => true,
            };
        }
        finally
        {
            _revalidating = false;
        }
    }

    /// <summary>Returns null when valid. Empty fields are invalid but not reported until typed.</summary>
    public static string? ValidatePassword(string password, string confirm)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(confirm);
        if (password.Length == 0)
        {
            return "";
        }

        if (password.Length > 64)
        {
            return "The password can be at most 64 characters.";
        }

        if (password.Any(ch => ch < 0x20 || ch > 0x7E))
        {
            return "The password may only contain printable ASCII characters.";
        }

        if (confirm.Length == 0)
        {
            return "";
        }

        return password == confirm ? null : "The passwords do not match.";
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Add devices wizard committed {Count} device(s)")]
    private static partial void LogCommitted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopping discovery failed: {Reason}")]
    private static partial void LogStopFailed(ILogger logger, string reason);
}
