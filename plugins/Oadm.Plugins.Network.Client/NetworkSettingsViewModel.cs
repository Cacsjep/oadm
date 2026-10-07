using System.Collections;
using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

public enum Ipv4Choice
{
    Keep = 0,
    Dhcp = 1,
    Static = 2,
}

public enum Ipv6Choice
{
    Keep = 0,
    Disabled = 1,
    Auto = 2,
    Dhcp = 3,
    Static = 4,
}

public enum SourceChoice
{
    Keep = 0,
    Dhcp = 1,
    Static = 2,
}

/// <summary>Combo box entry; the label is what the combo box shows.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Asks the user to confirm a risky change (the host's shared <c>MessageWindow</c>): title, message, confirm button text.</summary>
public delegate Task<bool> ConfirmChange(string title, string message, string confirmText);

/// <summary>
/// "Network settings..." dialog. Every section starts at "Keep unchanged", so only touched sections end up in the
/// payload. Validation is the same <see cref="PayloadValidator"/> the server runs before writing. Errors appear once:
/// at the field they belong to (<see cref="INotifyDataErrorInfo"/>) or in the row of the Devices table. Several
/// devices: the new IPv4 (and static IPv6) addresses are set per device in the table only, suggested from the first
/// device's current address. Apply asks for confirmation (<see cref="Confirm"/>) when the change can cut OADM off.
/// </summary>
public sealed partial class NetworkSettingsViewModel : ObservableObject, INotifyDataErrorInfo
{
    public const string ConfirmTitle = "The devices may become unreachable";

    private static readonly HashSet<string> Inputs =
    [
        nameof(SelectedIpv4), nameof(Ipv4Address), nameof(Ipv4Mask), nameof(Ipv4Gateway),
        nameof(SelectedIpv6), nameof(Ipv6Address), nameof(Ipv6Prefix), nameof(Ipv6Gateway),
        nameof(SelectedDns), nameof(DnsPrimary), nameof(DnsSecondary), nameof(DnsDomain), nameof(DnsSearch),
        nameof(SelectedHostName), nameof(HostNameText),
    ];

    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly FieldErrors _fieldErrors;
    private NetworkPayload? _payload;
    private bool _initialized;
    private bool _recomputing;
    private string? _lastSuggestion;

    public NetworkSettingsViewModel(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        _fieldErrors = new FieldErrors(name => ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name)));
        Ipv4Choices = [new(Ipv4Choice.Keep, "Keep unchanged"), new(Ipv4Choice.Dhcp, "DHCP"), new(Ipv4Choice.Static, "Static")];
        Ipv6Choices =
        [
            new(Ipv6Choice.Keep, "Keep unchanged"),
            new(Ipv6Choice.Disabled, "Disabled"),
            new(Ipv6Choice.Auto, "Automatic (router advertisement)"),
            new(Ipv6Choice.Dhcp, "DHCPv6"),
            new(Ipv6Choice.Static, "Static"),
        ];
        DnsChoices = [new(SourceChoice.Keep, "Keep unchanged"), new(SourceChoice.Dhcp, "From DHCP"), new(SourceChoice.Static, "Static")];
        HostNameChoices = [new(SourceChoice.Keep, "Keep unchanged"), new(SourceChoice.Dhcp, "From DHCP"), new(SourceChoice.Static, "Static")];
        SelectedIpv4 = Ipv4Choices[0];
        SelectedIpv6 = Ipv6Choices[0];
        SelectedDns = DnsChoices[0];
        SelectedHostName = HostNameChoices[0];
        Assignment = new AddressAssignmentViewModel(devices);
        Assignment.Changed += (_, _) => Recompute();
        _initialized = true;
        Recompute();
    }

    /// <summary>The per-device table (new IPv4 / IPv6 addresses, host names), shared with "Assign IP address...".</summary>
    public AddressAssignmentViewModel Assignment { get; }

    /// <summary>Raised when the dialog should close: true = apply (see <see cref="ResultJson"/>), false = cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <summary>Confirmation before a risky Apply; the dialog shows the shared message window. Null: no confirmation.</summary>
    public ConfirmChange? Confirm { get; set; }

    public IReadOnlyList<Choice<Ipv4Choice>> Ipv4Choices { get; }

    public IReadOnlyList<Choice<Ipv6Choice>> Ipv6Choices { get; }

    public IReadOnlyList<Choice<SourceChoice>> DnsChoices { get; }

    public IReadOnlyList<Choice<SourceChoice>> HostNameChoices { get; }

    public string Title => _devices.Count == 1 ? "Network settings" : $"Network settings for {_devices.Count} devices";

    public string DeviceSummary => _devices.Count == 1
        ? $"{Label(_devices[0])}. Only sections you change are written."
        : $"{_devices.Count} devices, starting with {Label(_devices[0])}. Only sections you change are written.";

    /// <summary>Description of the Devices card: which devices (plus a failed prefill).</summary>
    public string DevicesDescription => string.IsNullOrEmpty(PrefillStatus) ? DeviceSummary : DeviceSummary + " " + PrefillStatus;

    public bool IsMultiDevice => _devices.Count > 1;

    public bool IsSingleDevice => !IsMultiDevice;

    public string HostNameLabel => IsMultiDevice ? "Host name template" : "Host name";

    public string HostNameHint => IsMultiDevice ? "Use {n} for the position in the list or {serial} for the serial number, e.g. cam-{n}." : string.Empty;

    [ObservableProperty]
    public partial Choice<Ipv4Choice> SelectedIpv4 { get; set; }

    /// <summary>
    /// One device: the new IPv4 address (field). Several devices: no field; the first device's current address is
    /// where the suggestions in the table start.
    /// </summary>
    [ObservableProperty]
    public partial string Ipv4Address { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Ipv4Mask { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Ipv4Gateway { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Choice<Ipv6Choice> SelectedIpv6 { get; set; }

    /// <summary>One device: the static IPv6 address (field). Several devices: per row in the table.</summary>
    [ObservableProperty]
    public partial string Ipv6Address { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Ipv6Prefix { get; set; } = "64";

    [ObservableProperty]
    public partial string Ipv6Gateway { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Choice<SourceChoice> SelectedDns { get; set; }

    [ObservableProperty]
    public partial string DnsPrimary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsSecondary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsDomain { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsSearch { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Choice<SourceChoice> SelectedHostName { get; set; }

    [ObservableProperty]
    public partial string HostNameText { get; set; } = string.Empty;

    /// <summary>Only set when the current settings could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevicesDescription))]
    public partial string PrefillStatus { get; set; } = string.Empty;

    /// <summary>What the change can do to OADM's connection; shown in the confirmation on Apply. Null: nothing risky.</summary>
    [ObservableProperty]
    public partial string? ReachabilityWarning { get; set; }

    [ObservableProperty]
    public partial bool CanApply { get; set; }

    /// <summary>Why Apply is disabled (its tooltip), null when it is enabled.</summary>
    [ObservableProperty]
    public partial string? ApplyBlockedReason { get; set; }

    public bool IsIpv4Static => SelectedIpv4.Value == Ipv4Choice.Static;

    public bool IsIpv6Static => SelectedIpv6.Value == Ipv6Choice.Static;

    public bool IsDnsStatic => SelectedDns.Value == SourceChoice.Static;

    public bool IsHostNameStatic => SelectedHostName.Value == SourceChoice.Static;

    /// <summary>The IPv4 address field: one device only.</summary>
    public bool ShowIpv4Address => IsIpv4Static && IsSingleDevice;

    /// <summary>The IPv6 address field: one device only.</summary>
    public bool ShowIpv6Address => IsIpv6Static && IsSingleDevice;

    public bool ShowPreview => IsMultiDevice && (IsIpv4Static || IsIpv6Static || IsHostNameStatic);

    public bool HasWarning => !string.IsNullOrEmpty(ReachabilityWarning);

    /// <summary>A field has an error (shown below that field).</summary>
    public bool HasErrors => _fieldErrors.HasErrors;

    /// <summary>Payload JSON after Apply; null before.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Current payload (null while the input cannot be turned into one).</summary>
    public NetworkPayload? Payload => _payload;

    public IEnumerable GetErrors(string? propertyName) => _fieldErrors.GetErrors(propertyName);

    /// <summary>The error of one field (tests).</summary>
    public string? ErrorOf(string propertyName) => _fieldErrors[propertyName];

    /// <summary>Reads the first device's current settings through the plugin query and prefills the fields. Never throws.</summary>
    public async Task LoadCurrentAsync(ITaskDialogContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Assignment.Attach(ctx, _devices[0].Id);
        try
        {
            var json = await ctx.QueryAsync(_devices[0].Id, NetworkSettingsTaskPlugin.QueryGetNetworkInfo, null, ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(json))
            {
                PrefillStatus = "The current settings are not available.";
                return;
            }

            ApplyCurrent(CurrentNetworkSettings.FromJson(json));
        }
        catch (OperationCanceledException)
        {
            PrefillStatus = string.Empty;
        }
#pragma warning disable CA1031 // A failed prefill must never break the dialog; the user can still enter everything.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PrefillStatus = "The current settings could not be read: " + ex.Message;
        }
    }

    /// <summary>Prefills the fields from the current settings of the first selected device.</summary>
    public void ApplyCurrent(CurrentNetworkSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        PrefillStatus = string.Empty;

        var v4 = current.Ipv4;
        var mask = v4.StaticPrefixLength ?? v4.PrefixLength;
        Ipv4Mask = mask is { } m ? Ipv4.MaskText(m) : Ipv4Mask;
        Ipv4Gateway = v4.Gateway ?? v4.StaticGateway ?? Ipv4Gateway;
        Ipv4Address = v4.Address ?? v4.StaticAddress ?? Ipv4Address;

        var v6 = current.Ipv6;
        if (v6.StaticAddresses.Count > 0)
        {
            var parts = v6.StaticAddresses[0].Split('/');
            Ipv6Address = parts[0];
            Ipv6Prefix = parts.Length > 1 ? parts[1] : Ipv6Prefix;
            if (IsMultiDevice && Assignment.Rows[0].NewIpv6Address.Length == 0)
            {
                Assignment.Rows[0].NewIpv6Address = parts[0];
            }
        }

        Ipv6Gateway = v6.Gateway ?? Ipv6Gateway;

        var dns = current.Dns;
        var staticServers = dns.StaticServers.Count > 0 ? dns.StaticServers : dns.Servers;
        DnsPrimary = staticServers.ElementAtOrDefault(0) ?? DnsPrimary;
        DnsSecondary = staticServers.ElementAtOrDefault(1) ?? DnsSecondary;
        DnsDomain = dns.StaticDomainName ?? dns.DomainName ?? DnsDomain;
        var search = dns.StaticSearchDomains.Count > 0 ? dns.StaticSearchDomains : dns.SearchDomains;
        DnsSearch = search.Count > 0 ? string.Join(", ", search) : DnsSearch;

        if (!IsMultiDevice)
        {
            HostNameText = current.HostName.StaticHostName ?? current.HostName.HostName ?? HostNameText;
        }
    }

    /// <summary>Apply: confirms a risky change first (shared confirmation window), then closes with the payload.</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        Recompute();
        if (!CanApply || _payload is null)
        {
            return;
        }

        var payload = _payload;
        if (HasWarning && Confirm is { } confirm && !await confirm(ConfirmTitle, ReachabilityWarning!, "Apply").ConfigureAwait(true))
        {
            return;
        }

        ResultJson = payload.ToJson();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        ResultJson = null;
        CloseRequested?.Invoke(this, false);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (!_initialized)
        {
            return;
        }

        if (e.PropertyName is { } name && Inputs.Contains(name))
        {
            if (name == nameof(SelectedIpv4))
            {
                OnPropertyChanged(nameof(IsIpv4Static));
                OnPropertyChanged(nameof(ShowIpv4Address));
            }
            else if (name == nameof(SelectedIpv6))
            {
                OnPropertyChanged(nameof(IsIpv6Static));
                OnPropertyChanged(nameof(ShowIpv6Address));
            }
            else if (name == nameof(SelectedDns))
            {
                OnPropertyChanged(nameof(IsDnsStatic));
            }
            else if (name == nameof(SelectedHostName))
            {
                OnPropertyChanged(nameof(IsHostNameStatic));
            }

            Recompute();
        }
        else if (e.PropertyName == nameof(ReachabilityWarning))
        {
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    /// <summary>Rebuilds payload, table, warning and errors from the inputs.</summary>
    private void Recompute()
    {
        if (_recomputing)
        {
            return;
        }

        _recomputing = true;
        try
        {
            RecomputeCore();
        }
        finally
        {
            _recomputing = false;
        }
    }

    private void RecomputeCore()
    {
        var fields = new Dictionary<string, string?>();
        int? prefix = Ipv4.TryParsePrefix(Ipv4Mask, out var p) ? p : null;
        int? v6Prefix = int.TryParse(Ipv6Prefix?.Trim().TrimStart('/'), NumberStyles.None, CultureInfo.InvariantCulture, out var pp) ? pp : null;
        var ipv4Addresses = AssignIpv4(prefix);
        Assignment.SetIpv6(IsMultiDevice && IsIpv6Static);
        var hostNames = AssignHostNames();

        var devices = new Dictionary<Guid, DeviceAssignment>();
        for (var i = 0; i < _devices.Count; i++)
        {
            devices[_devices[i].Id] = new DeviceAssignment(
                ipv4Addresses?.ElementAtOrDefault(i),
                hostNames?.ElementAtOrDefault(i),
                IsIpv6Static ? (IsMultiDevice ? Assignment.Rows[i].NewIpv6Address.Trim() : Ipv6Address.Trim()) : null);
        }

        var payload = new NetworkPayload
        {
            Ipv4 = SelectedIpv4.Value switch
            {
                Ipv4Choice.Dhcp => new Ipv4Change(Ipv4Mode.Dhcp),
                Ipv4Choice.Static => new Ipv4Change(Ipv4Mode.Static, prefix, Ipv4Gateway.Trim()),
                _ => null,
            },
            Ipv6 = SelectedIpv6.Value switch
            {
                Ipv6Choice.Disabled => new Ipv6Change(Ipv6Mode.Disabled),
                Ipv6Choice.Auto => new Ipv6Change(Ipv6Mode.Auto),
                Ipv6Choice.Dhcp => new Ipv6Change(Ipv6Mode.Dhcp),
                Ipv6Choice.Static => new Ipv6Change(Ipv6Mode.Static, null, v6Prefix, NullIfEmpty(Ipv6Gateway)),
                _ => null,
            },
            Dns = SelectedDns.Value switch
            {
                SourceChoice.Dhcp => new DnsChange(true),
                SourceChoice.Static => new DnsChange(
                    false,
                    new[] { DnsPrimary, DnsSecondary }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList(),
                    NullIfEmpty(DnsDomain),
                    DnsSearch.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()),
                _ => null,
            },
            HostName = SelectedHostName.Value switch
            {
                SourceChoice.Dhcp => new HostNameChange(true),
                SourceChoice.Static => new HostNameChange(false),
                _ => null,
            },
            Devices = devices,
        };

        if (IsIpv4Static)
        {
            AddIpv4FieldErrors(fields, prefix);
        }

        if (IsIpv6Static)
        {
            AddIpv6FieldErrors(fields, v6Prefix);
        }

        if (payload.Dns is { UseDhcp: false } dns)
        {
            foreach (var error in PayloadValidator.ValidateDnsChange(dns))
            {
                var field = error.Contains("search", StringComparison.OrdinalIgnoreCase) ? nameof(DnsSearch)
                    : error.Contains("domain name", StringComparison.Ordinal) ? nameof(DnsDomain)
                    : nameof(DnsPrimary);
                fields.TryAdd(field, FieldErrors.Clean(error));
            }
        }

        if (payload.HostName is { UseDhcp: false })
        {
            var hostOnly = new NetworkPayload { HostName = payload.HostName, Devices = devices };
            if (PayloadValidator.Validate(hostOnly) is [var error, ..])
            {
                fields[nameof(HostNameText)] = FieldErrors.Clean(error);
            }
        }

        _fieldErrors.SetAll(fields);

        // The whole payload, exactly as the server validates it before the first write (rows included).
        var remaining = payload.HasChanges ? PayloadValidator.Validate(payload) : [];
        var rowProblems = ShowPreview && (Assignment.HasConflicts || Assignment.HasError);
        ApplyBlockedReason = !payload.HasChanges
            ? "Choose at least one setting to change."
            : _fieldErrors.First
                ?? (Assignment.Error is { } tableError && IsMultiDevice && IsIpv4Static ? tableError : null)
                ?? (rowProblems ? "Resolve the problems shown in the Devices table." : null)
                ?? remaining.Select(FieldErrors.Clean).FirstOrDefault();

        Assignment.ShowHostName = IsHostNameStatic;
        Assignment.SetHostNames(hostNames);
        ReachabilityWarning = ComputeWarning(payload, ipv4Addresses);
        _payload = ApplyBlockedReason is null ? payload : null;
        CanApply = _payload is not null;
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(Payload));
    }

    private void AddIpv4FieldErrors(Dictionary<string, string?> fields, int? prefix)
    {
        var network = PayloadValidator.ValidateIpv4Network(prefix, Ipv4Gateway.Trim());
        if (prefix is null)
        {
            fields[nameof(Ipv4Mask)] = "Enter a subnet mask (255.255.255.0) or prefix length (24).";
        }
        else if (network.FirstOrDefault(e => e.Contains("subnet mask", StringComparison.Ordinal)) is { } maskError)
        {
            fields[nameof(Ipv4Mask)] = FieldErrors.Clean(maskError);
        }

        if (prefix is not null && network.FirstOrDefault(e => !e.Contains("subnet mask", StringComparison.Ordinal)) is { } gatewayError)
        {
            fields[nameof(Ipv4Gateway)] = FieldErrors.Clean(gatewayError);
        }

        if (IsSingleDevice)
        {
            var row = new AssignmentRow(new AssignmentDevice(_devices[0].Id, _devices[0].Address), Ipv4Address);
            var problem = AddressConflicts.Find([row], prefix, fields.ContainsKey(nameof(Ipv4Gateway)) ? null : Ipv4Gateway.Trim())[0];
            if (problem is not null)
            {
                fields[nameof(Ipv4Address)] = problem == "No address" ? "Enter an IP address." : problem + ".";
            }
        }
    }

    private void AddIpv6FieldErrors(Dictionary<string, string?> fields, int? v6Prefix)
    {
        foreach (var error in PayloadValidator.ValidateIpv6Network(v6Prefix, Ipv6Gateway))
        {
            fields.TryAdd(error.Contains("prefix", StringComparison.Ordinal) ? nameof(Ipv6Prefix) : nameof(Ipv6Gateway), FieldErrors.Clean(error));
        }

        if (IsSingleDevice)
        {
            var row = new AssignmentRow(new AssignmentDevice(_devices[0].Id, _devices[0].Address), Ipv6Address);
            var problem = AddressConflicts.FindIpv6([row])[0];
            if (problem is not null)
            {
                fields[nameof(Ipv6Address)] = problem == "No IPv6 address" ? "Enter an IPv6 address." : problem + ".";
            }
        }
    }

    /// <summary>
    /// The new IPv4 addresses per device. One device: the field. Several: the table, suggested once from the first
    /// device's current address (same assignment as "Assign IP address..."); mask or gateway changes only re-check.
    /// </summary>
    private List<string>? AssignIpv4(int? prefix)
    {
        if (!IsIpv4Static || !IsMultiDevice)
        {
            Assignment.Clear();
            _lastSuggestion = null;
            return IsIpv4Static ? [Ipv4Address.Trim()] : null;
        }

        var start = Ipv4Address.Trim();
        var gateway = Ipv4Gateway.Trim();
        var key = $"{start}|{prefix}|{gateway}";
        if (!string.Equals(_lastSuggestion, key, StringComparison.Ordinal))
        {
            // A new start discards edits; a new mask or gateway suggests again around the user's edits.
            var first = _lastSuggestion is null || !_lastSuggestion.StartsWith(start + "|", StringComparison.Ordinal);
            _lastSuggestion = key;
            if (IpRangeExpression.TryParse(start, out var range, out _))
            {
                Assignment.Assign(range!, prefix, gateway, resetEdits: first);
            }
            else if (first)
            {
                Assignment.EditManually(prefix, gateway);
            }
            else
            {
                Assignment.UpdateNetwork(prefix, gateway);
            }
        }

        return [.. Assignment.Addresses];
    }

    private List<string>? AssignHostNames()
    {
        if (!IsHostNameStatic)
        {
            return null;
        }

        return _devices.Select((d, i) => PayloadValidator.ExpandHostName(HostNameText, i + 1, d.Serial)).ToList();
    }

    private string? ComputeWarning(NetworkPayload payload, List<string>? addresses)
    {
        var parts = new List<string>();
        if (payload.Ipv4 is { Mode: Ipv4Mode.Static } && addresses is not null)
        {
            parts.AddRange(NetworkWarnings.Static(_devices, addresses));
        }
        else if (payload.Ipv4 is { Mode: Ipv4Mode.Dhcp })
        {
            parts.Add(NetworkWarnings.Dhcp);
        }

        var ipv6Connected = _devices.Count(d => d.Address.Contains(':', StringComparison.Ordinal));
        if (payload.Ipv6 is not null && ipv6Connected > 0)
        {
            parts.Add($"{ipv6Connected} device(s) are reached over IPv6. Changing IPv6 can make them unreachable from the server.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string Label(IDeviceInfo device) =>
        string.IsNullOrEmpty(device.Model) ? $"{device.Address} ({device.Serial})" : $"{device.Model} at {device.Address}";

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
