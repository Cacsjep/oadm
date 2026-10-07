using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Network;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DhcpServer.Client;

/// <summary>What the page view does for the view model (windows); tests leave it null or fake it.</summary>
public interface IDhcpPageUi
{
    /// <summary>Shows the static lease dialog modally until it closes.</summary>
    Task ShowStaticLeaseDialogAsync(StaticLeaseDialogViewModel dialog);
}

/// <summary>
/// The DHCP server page: enable, interface, start and end address (errors under the fields), derived values line, Save;
/// status line in the page header; the lease list (virtualized, searchable, sortable by the grid) with static leases,
/// device names of managed devices and the row actions. <see cref="Activate"/> when the page is shown (reads the state
/// and watches the live events), <see cref="Deactivate"/> when hidden; without live events it re-reads every 2 s.
/// </summary>
public sealed partial class DhcpServerViewModel : ValidatingViewModel, IDisposable
{
    /// <summary>Wait before watching again after the event stream ended or failed.</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, LeaseRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, LeaseRowViewModel> _rowsByAddress = [];
    private Dictionary<string, DhcpNetworkInfo> _networks = new(StringComparer.Ordinal);
    private Dictionary<string, string> _devicesByMac = new(StringComparer.Ordinal);
    private Dictionary<string, string> _devicesByAddress = new(StringComparer.Ordinal);
    private CancellationTokenSource? _active;
    private long _leaseVersion = -1;
    private bool _formLoaded;
    private int _staticCount;

    public DhcpServerViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.DevicesChanged += (_, _) => OnDevicesChanged();
        BuildDeviceMaps();
        Listen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InterfaceSelection.Selected))
            {
                InterfaceError = null;
                OnPropertyChanged(nameof(SelectedNetwork));
                OnPropertyChanged(nameof(ClientsGetText));
                Validation.Validate();
            }
        };
        Validation
            .Rule(nameof(RangeStart), () => RangeErrors().Start)
            .Rule(nameof(RangeEnd), () => RangeErrors().End);
        Validation.Validate();
    }

    /// <summary>Set by the view.</summary>
    public IDhcpPageUi? Ui { get; set; }

    /// <summary>"Listen on": interfaces with an IPv4 address.</summary>
    public InterfaceSelection Listen { get; } = new();

    /// <summary>The lease list (search applied; the grid sorts).</summary>
    public BulkObservableCollection<LeaseRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string RangeStart { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RangeEnd { get; set; } = string.Empty;

    /// <summary>Server answer about the interface (shown under the select).</summary>
    [ObservableProperty]
    public partial string? InterfaceError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading";

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOk), nameof(IsStatusWarning), nameof(IsStatusError), nameof(IsStatusAccent))]
    public partial string StatusKind { get; set; } = "accent";

    [ObservableProperty]
    public partial string LeaseSummary { get; set; } = "No leases";

    public bool IsStatusOk => StatusKind == ServiceStatus.Ok;

    public bool IsStatusWarning => StatusKind == ServiceStatus.Warning;

    public bool IsStatusError => StatusKind == ServiceStatus.Error;

    public bool IsStatusAccent => StatusKind == "accent";

    public DhcpNetworkInfo? SelectedNetwork => Listen.SelectedId is { } id && _networks.TryGetValue(id, out var n) ? n : null;

    /// <summary>"Clients get mask 255.255.255.0, router 10.0.0.138, DNS 10.0.0.138, lease 24 h".</summary>
    public string? ClientsGetText => SelectedNetwork?.ClientsGet;

    public bool HasRows => Rows.Count > 0;

    /// <summary>"No leases yet." or "No lease matches the search.".</summary>
    public string EmptyText => _rows.Count == 0 ? "No leases yet." : "No lease matches the search.";

    /// <summary>Why Save is disabled (tooltip).</summary>
    public string SaveTip => IsFormValid ? "Store the settings and apply them" : FormError ?? string.Empty;

    /// <summary>All leases by MAC (tests).</summary>
    public int LeaseCount => _rows.Count;

    public bool IsActive => _active is not null;

    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
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

    public void Dispose() => Deactivate();

    /// <summary>Reads the whole state (interfaces and leases); the form is filled the first time.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _ctx.InvokeAsync(DhcpServerMethods.GetState, null, ct).ConfigureAwait(true);
            if (json is null)
            {
                return;
            }

            var state = DhcpJson.Deserialize<DhcpState>(json);
            _networks = state.Networks.ToDictionary(n => n.InterfaceId, StringComparer.Ordinal);
            Listen.Apply(state.Interfaces, state.Config.InterfaceId, state.Config.InterfaceName);
            OnPropertyChanged(nameof(SelectedNetwork));
            OnPropertyChanged(nameof(ClientsGetText));
            ApplyStatus(state);
            if (state.Leases is { } leases)
            {
                ReplaceLeases(leases, state.LeaseVersion);
            }

            if (!_formLoaded)
            {
                ApplyForm(state.Config);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any server error is shown in the status line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetStatus(ServiceStatus.Error, "Cannot read the DHCP server state", Message(ex));
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
            case DhcpServerMethods.StateTopic:
                ApplyStatus(DhcpJson.Deserialize<DhcpState>(item.PayloadJson));
                break;
            case DhcpServerMethods.LeasesTopic:
                ApplyLeaseChanges(DhcpJson.Deserialize<LeasesEvent>(item.PayloadJson));
                break;
        }
    }

    /// <summary>Replaces all leases (full state).</summary>
    public void ReplaceLeases(IReadOnlyList<LeaseInfo> leases, long version)
    {
        ArgumentNullException.ThrowIfNull(leases);
        _rows.Clear();
        _rowsByAddress.Clear();
        foreach (var lease in leases)
        {
            var row = new LeaseRowViewModel(lease, DeviceFor(lease));
            _rows[lease.Mac] = row;
            Index(row);
        }

        _leaseVersion = version;
        ApplyFilter();
    }

    /// <summary>Re-computes the Expires column ("in 23 h"); the view calls it once a minute.</summary>
    public void RefreshExpires()
    {
        var now = DateTime.UtcNow;
        foreach (var row in _rows.Values)
        {
            row.RefreshExpires(now);
        }
    }

    /// <summary>The dialog for a new static lease, or for editing <paramref name="row"/>.</summary>
    public StaticLeaseDialogViewModel CreateStaticLeaseDialog(LeaseRowViewModel? row = null) => new(
        _ctx.InvokeAsync,
        SelectedNetwork,
        mac => _rows.TryGetValue(MacAddress.Format(mac), out var r) ? r.Lease : null,
        address => _rowsByAddress.TryGetValue(address, out var r) ? r.Lease : null,
        row?.Lease);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        IsSaving = true;
        if (IsEnabled)
        {
            SetStatus("accent", "Checking for other DHCP servers", null);
        }

        try
        {
            IReadOnlyList<string>? confirmed = null;
            while (true)
            {
                var request = new DhcpSaveRequest(IsEnabled, Listen.SelectedId, RangeStart.Trim(), RangeEnd.Trim(), confirmed);
                var json = await _ctx.InvokeAsync(DhcpServerMethods.Save, DhcpJson.Serialize(request), CancellationToken.None).ConfigureAwait(true);
                var reply = DhcpJson.Deserialize<DhcpSaveReply>(json);
                if (reply.OtherServers is { Count: > 0 } others && !reply.Saved)
                {
                    ApplyStatus(reply.State with { Status = Status.DhcpStatusTexts.OtherServer(others) });
                    var list = string.Join(", ", others);
                    var go = await _ctx.ConfirmAsync(
                        "Another DHCP server answers",
                        $"Another DHCP server ({list}) answers on this network. Running two DHCP servers causes address conflicts. Enable anyway?",
                        "Enable anyway").ConfigureAwait(true);
                    if (!go)
                    {
                        ApplyStatus(reply.State);
                        return;
                    }

                    confirmed = others;
                    continue;
                }

                if (!reply.Saved)
                {
                    foreach (var (field, message) in reply.FieldErrors ?? new Dictionary<string, string>())
                    {
                        if (field == "Interface")
                        {
                            InterfaceError = message;
                        }
                        else
                        {
                            Validation.SetServerError(field, message);
                        }
                    }

                    ApplyStatus(reply.State);
                    return;
                }

                ApplyForm(reply.State.Config);
                ApplyStatus(reply.State);
                return;
            }
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetStatus(ServiceStatus.Error, "Saving failed", Message(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving && IsFormValid;

    [RelayCommand]
    private async Task AddStaticAsync()
    {
        if (Ui is { } ui)
        {
            await ui.ShowStaticLeaseDialogAsync(CreateStaticLeaseDialog()).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task EditStaticAsync(LeaseRowViewModel? row)
    {
        if (row is { IsStatic: true } && Ui is { } ui)
        {
            await ui.ShowStaticLeaseDialogAsync(CreateStaticLeaseDialog(row)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteStaticAsync(LeaseRowViewModel? row)
    {
        if (row is not { IsStatic: true })
        {
            return;
        }

        if (await _ctx.ConfirmAsync("Delete static lease", $"Delete the static lease of {row.Mac} ({row.Address})? The device keeps its address until its lease ends.", "Delete").ConfigureAwait(true))
        {
            await CallAsync(DhcpServerMethods.DeleteStatic, row.Mac).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task MakeStaticAsync(LeaseRowViewModel? row)
    {
        if (row is { IsDynamic: true })
        {
            await CallAsync(DhcpServerMethods.MakeStatic, row.Mac).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ReleaseAsync(LeaseRowViewModel? row)
    {
        if (row is not { IsDynamic: true })
        {
            return;
        }

        if (await _ctx.ConfirmAsync("Release lease", $"Release {row.Address} of {row.Mac}? The address becomes free; the device asks for an address again at its next renewal.", "Release").ConfigureAwait(true))
        {
            await CallAsync(DhcpServerMethods.Release, row.Mac).ConfigureAwait(true);
        }
    }

    protected override void OnValidationChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveTip));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsEnabledChanged(bool value) => Validation.Validate();

    private (string? Start, string? End) RangeErrors()
    {
        var (start, end) = DhcpValidation.Range(RangeStart, RangeEnd, SelectedNetwork);
        if (!IsEnabled)
        {
            // Disabled: an empty range is fine, a typed one must still be valid.
            start = string.IsNullOrWhiteSpace(RangeStart) ? null : start;
            end = string.IsNullOrWhiteSpace(RangeEnd) ? null : end;
        }

        return (start, end);
    }

    private async Task CallAsync(string method, string mac)
    {
        try
        {
            await _ctx.InvokeAsync(method, DhcpJson.Serialize(new LeaseRequest(mac)), CancellationToken.None).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true); // the live event follows too; reading keeps a host without events right
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("DHCP server", Message(ex)).ConfigureAwait(true);
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
                    // Subscribe first, then read the state: nothing published in between is lost (lease versions deduplicate).
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

    private void ApplyForm(DhcpConfig config)
    {
        _formLoaded = true;
        IsEnabled = config.Enabled;
        RangeStart = config.RangeStart ?? string.Empty;
        RangeEnd = config.RangeEnd ?? string.Empty;
        InterfaceError = null;
        if (config.InterfaceId is not null)
        {
            Listen.Select(config.InterfaceId);
        }

        Validation.Reset();
    }

    private void ApplyStatus(DhcpState state) => SetStatus(state.Status.Kind, state.Status.Text, state.Status.Detail);

    private void SetStatus(string kind, string text, string? detail)
    {
        StatusKind = kind;
        StatusText = text;
        StatusDetail = detail;
    }

    private void ApplyLeaseChanges(LeasesEvent changes)
    {
        if (changes.Version <= _leaseVersion)
        {
            return; // already contained in the state read
        }

        var structural = false;
        foreach (var lease in changes.Changed)
        {
            if (_rows.TryGetValue(lease.Mac, out var row))
            {
                Unindex(row);
                row.Update(lease, DeviceFor(lease));
                Index(row);
            }
            else
            {
                row = new LeaseRowViewModel(lease, DeviceFor(lease));
                _rows[lease.Mac] = row;
                Index(row);
                structural = true;
            }
        }

        foreach (var mac in changes.Removed)
        {
            if (_rows.Remove(mac, out var row))
            {
                Unindex(row);
                structural = true;
            }
        }

        _leaseVersion = changes.Version;
        if (structural || !string.IsNullOrEmpty(SearchText))
        {
            ApplyFilter();
        }
        else
        {
            UpdateSummary();
        }
    }

    private void Index(LeaseRowViewModel row)
    {
        if (Ipv4.TryParse(row.Address, out var a))
        {
            _rowsByAddress[a] = row;
        }
    }

    private void Unindex(LeaseRowViewModel row)
    {
        if (Ipv4.TryParse(row.Address, out var a) && _rowsByAddress.TryGetValue(a, out var current) && ReferenceEquals(current, row))
        {
            _rowsByAddress.Remove(a);
        }
    }

    /// <summary>O(n) over the leases, one grid reset.</summary>
    private void ApplyFilter()
    {
        var search = SearchText.Trim().ToLowerInvariant();
        Rows.ReplaceAll(search.Length == 0
            ? _rows.Values.OrderBy(r => r.AddressSortKey)
            : _rows.Values.Where(r => r.SearchText.Contains(search, StringComparison.Ordinal)).OrderBy(r => r.AddressSortKey));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyText));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        _staticCount = _rows.Values.Count(r => r.IsStatic);
        var total = _rows.Count;
        LeaseSummary = total == 0 ? "No leases"
            : string.Create(CultureInfo.InvariantCulture, $"{total:N0} {(total == 1 ? "lease" : "leases")}, {_staticCount:N0} static")
              + (Rows.Count == total ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", {Rows.Count:N0} shown"));
    }

    private void OnDevicesChanged()
    {
        BuildDeviceMaps();
        foreach (var row in _rows.Values)
        {
            row.SetDevice(DeviceFor(row.Lease));
        }
    }

    /// <summary>Serial number (= MAC of AXIS devices) and address -> model; O(n) once per device change.</summary>
    private void BuildDeviceMaps()
    {
        var byMac = new Dictionary<string, string>(StringComparer.Ordinal);
        var byAddress = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (IDeviceInfo device in _ctx.Devices)
        {
            var name = device.Model ?? device.HostName ?? device.Serial;
            if (!string.IsNullOrEmpty(device.Serial))
            {
                byMac.TryAdd(device.Serial.ToUpperInvariant(), name);
            }

            byAddress.TryAdd(device.Address, name);
        }

        _devicesByMac = byMac;
        _devicesByAddress = byAddress;
    }

    private string? DeviceFor(LeaseInfo lease)
    {
        if (MacAddress.TryParse(lease.Mac, out var mac) && _devicesByMac.TryGetValue(MacAddress.Compact(mac), out var byMac))
        {
            return byMac;
        }

        return _devicesByAddress.TryGetValue(lease.Address, out var byAddress) ? byAddress : null;
    }

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without a Grpc reference.</summary>
    internal static string Message(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }
}
