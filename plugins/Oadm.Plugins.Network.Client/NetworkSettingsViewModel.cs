using System.Collections.ObjectModel;
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

/// <summary>One row of the per-device preview (address range and host name template).</summary>
public sealed record PreviewRow(string Device, string CurrentAddress, string NewAddress, string NewHostName);

/// <summary>
/// "Network settings..." dialog. Every section starts at "Keep unchanged", so only touched sections end up in the
/// payload. Validation is the same <see cref="PayloadValidator"/> the server runs before writing.
/// </summary>
public sealed partial class NetworkSettingsViewModel : ObservableObject
{
    private static readonly HashSet<string> Inputs =
    [
        nameof(SelectedIpv4), nameof(Ipv4Address), nameof(Ipv4Mask), nameof(Ipv4Gateway),
        nameof(SelectedIpv6), nameof(Ipv6Address), nameof(Ipv6Prefix), nameof(Ipv6Gateway),
        nameof(SelectedDns), nameof(DnsPrimary), nameof(DnsSecondary), nameof(DnsDomain), nameof(DnsSearch),
        nameof(SelectedHostName), nameof(HostNameText), nameof(WarningAcknowledged),
    ];

    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private NetworkPayload? _payload;
    private bool _initialized;

    public NetworkSettingsViewModel(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        Ipv4Choices = [new(Ipv4Choice.Keep, "Keep unchanged"), new(Ipv4Choice.Dhcp, "DHCP"), new(Ipv4Choice.Static, "Static")];
        var v6 = new List<Choice<Ipv6Choice>>
        {
            new(Ipv6Choice.Keep, "Keep unchanged"),
            new(Ipv6Choice.Disabled, "Disabled"),
            new(Ipv6Choice.Auto, "Automatic (router advertisement)"),
            new(Ipv6Choice.Dhcp, "DHCPv6"),
        };
        if (devices.Count == 1)
        {
            v6.Add(new(Ipv6Choice.Static, "Static"));
        }

        Ipv6Choices = v6;
        DnsChoices = [new(SourceChoice.Keep, "Keep unchanged"), new(SourceChoice.Dhcp, "From DHCP"), new(SourceChoice.Static, "Static")];
        HostNameChoices = [new(SourceChoice.Keep, "Keep unchanged"), new(SourceChoice.Dhcp, "From DHCP"), new(SourceChoice.Static, "Static")];
        SelectedIpv4 = Ipv4Choices[0];
        SelectedIpv6 = Ipv6Choices[0];
        SelectedDns = DnsChoices[0];
        SelectedHostName = HostNameChoices[0];
        _initialized = true;
        Recompute();
    }

    /// <summary>Raised when the dialog should close: true = apply (see <see cref="ResultJson"/>), false = cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<Choice<Ipv4Choice>> Ipv4Choices { get; }

    public IReadOnlyList<Choice<Ipv6Choice>> Ipv6Choices { get; }

    public IReadOnlyList<Choice<SourceChoice>> DnsChoices { get; }

    public IReadOnlyList<Choice<SourceChoice>> HostNameChoices { get; }

    public string Title => _devices.Count == 1 ? "Network settings" : $"Network settings for {_devices.Count} devices";

    public string DeviceSummary => _devices.Count == 1
        ? $"{Label(_devices[0])}. Only sections you change are written."
        : $"{_devices.Count} devices, starting with {Label(_devices[0])}. Only sections you change are written.";

    public bool IsMultiDevice => _devices.Count > 1;

    public string Ipv4AddressLabel => IsMultiDevice ? "Start address" : "IP address";

    public string HostNameLabel => IsMultiDevice ? "Host name template" : "Host name";

    public string HostNameHint => IsMultiDevice ? "Use {n} for the position in the list or {serial} for the serial number, e.g. cam-{n}." : string.Empty;

    [ObservableProperty]
    public partial Choice<Ipv4Choice> SelectedIpv4 { get; set; }

    [ObservableProperty]
    public partial string Ipv4Address { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Ipv4Mask { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Ipv4Gateway { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Choice<Ipv6Choice> SelectedIpv6 { get; set; }

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

    [ObservableProperty]
    public partial string CurrentIpv4Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentIpv6Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentDnsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentHostNameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrefillStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ReachabilityWarning { get; set; }

    [ObservableProperty]
    public partial bool WarningAcknowledged { get; set; }

    [ObservableProperty]
    public partial bool CanApply { get; set; }

    public ObservableCollection<string> Errors { get; } = [];

    public ObservableCollection<PreviewRow> Preview { get; } = [];

    public bool IsIpv4Static => SelectedIpv4.Value == Ipv4Choice.Static;

    public bool IsIpv6Static => SelectedIpv6.Value == Ipv6Choice.Static;

    public bool IsDnsStatic => SelectedDns.Value == SourceChoice.Static;

    public bool IsHostNameStatic => SelectedHostName.Value == SourceChoice.Static;

    public bool ShowPreview => Preview.Count > 0;

    public bool HasWarning => !string.IsNullOrEmpty(ReachabilityWarning);

    public bool HasErrors => Errors.Count > 0;

    /// <summary>Payload JSON after Apply; null before.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Current payload (null while the input cannot be turned into one).</summary>
    public NetworkPayload? Payload => _payload;

    /// <summary>Reads the first device's current settings through the plugin query and prefills the fields. Never throws.</summary>
    public async Task LoadCurrentAsync(ITaskDialogContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        PrefillStatus = $"Reading current settings from {Label(_devices[0])}...";
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

    /// <summary>Shows and prefills from the current settings of the first selected device.</summary>
    public void ApplyCurrent(CurrentNetworkSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        PrefillStatus = IsMultiDevice
            ? $"Current values are from {Label(_devices[0])} ({current.Source})."
            : $"Current values read via {current.Source}.";

        var v4 = current.Ipv4;
        CurrentIpv4Text = v4.Supported
            ? $"Current: {ModeText(v4.Mode)}, {AddressText(v4.Address, v4.PrefixLength)}, gateway {v4.Gateway ?? "none"}"
            : "Current: IPv4 is not supported on this interface";
        var mask = v4.StaticPrefixLength ?? v4.PrefixLength;
        Ipv4Mask = mask is { } m ? Ipv4.MaskText(m) : Ipv4Mask;
        Ipv4Gateway = v4.Gateway ?? v4.StaticGateway ?? Ipv4Gateway;
        Ipv4Address = v4.Address ?? v4.StaticAddress ?? Ipv4Address;

        var v6 = current.Ipv6;
        CurrentIpv6Text = !v6.Supported
            ? "Current: IPv6 is not supported on this interface"
            : v6.Enabled
                ? $"Current: enabled, {ModeText(v6.Mode)}{(v6.Addresses.Count > 0 ? ", " + string.Join(", ", v6.Addresses) : string.Empty)}"
                : "Current: disabled";
        if (v6.StaticAddresses.Count > 0)
        {
            var staticV6 = v6.StaticAddresses[0];
            var parts = staticV6.Split('/');
            Ipv6Address = parts[0];
            Ipv6Prefix = parts.Length > 1 ? parts[1] : Ipv6Prefix;
        }

        Ipv6Gateway = v6.Gateway ?? Ipv6Gateway;

        var dns = current.Dns;
        CurrentDnsText = $"Current: {(dns.UseDhcp ? "from DHCP" : "static")}, servers {(dns.Servers.Count > 0 ? string.Join(", ", dns.Servers) : "none")}" +
            (string.IsNullOrEmpty(dns.DomainName) ? string.Empty : $", domain {dns.DomainName}");
        var staticServers = dns.StaticServers.Count > 0 ? dns.StaticServers : dns.Servers;
        DnsPrimary = staticServers.ElementAtOrDefault(0) ?? DnsPrimary;
        DnsSecondary = staticServers.ElementAtOrDefault(1) ?? DnsSecondary;
        DnsDomain = dns.StaticDomainName ?? dns.DomainName ?? DnsDomain;
        var search = dns.StaticSearchDomains.Count > 0 ? dns.StaticSearchDomains : dns.SearchDomains;
        DnsSearch = search.Count > 0 ? string.Join(", ", search) : DnsSearch;

        var host = current.HostName;
        CurrentHostNameText = $"Current: {host.HostName ?? "none"}{(host.UseDhcp ? " (DHCP preferred)" : string.Empty)}";
        if (!IsMultiDevice)
        {
            HostNameText = host.StaticHostName ?? host.HostName ?? HostNameText;
        }
    }

    [RelayCommand]
    private void Apply()
    {
        Recompute();
        if (!CanApply || _payload is null)
        {
            return;
        }

        ResultJson = _payload.ToJson();
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
            }
            else if (name == nameof(SelectedIpv6))
            {
                OnPropertyChanged(nameof(IsIpv6Static));
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

    /// <summary>Rebuilds payload, preview, warning and errors from the inputs.</summary>
    private void Recompute()
    {
        var errors = new List<string>();
        var ipv4Addresses = AssignIpv4(errors);
        var hostNames = AssignHostNames();

        var devices = new Dictionary<Guid, DeviceAssignment>();
        for (var i = 0; i < _devices.Count; i++)
        {
            devices[_devices[i].Id] = new DeviceAssignment(ipv4Addresses?.ElementAtOrDefault(i), hostNames?.ElementAtOrDefault(i));
        }

        int? prefix = Ipv4.TryParsePrefix(Ipv4Mask, out var p) ? p : null;
        if (IsIpv4Static && prefix is null)
        {
            errors.Add("IPv4: enter a subnet mask (255.255.255.0) or prefix length (24).");
        }

        int? v6Prefix = int.TryParse(Ipv6Prefix?.Trim().TrimStart('/'), NumberStyles.None, CultureInfo.InvariantCulture, out var pp) ? pp : null;
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
                Ipv6Choice.Static => new Ipv6Change(Ipv6Mode.Static, Ipv6Address.Trim(), v6Prefix, NullIfEmpty(Ipv6Gateway)),
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

        if (errors.Count == 0 || payload.HasChanges)
        {
            foreach (var error in PayloadValidator.Validate(payload).Where(e => !errors.Contains(e)))
            {
                // The UI reports a missing mask itself; the validator's range message would repeat it.
                if (!(prefix is null && error.StartsWith("IPv4: enter a subnet mask", StringComparison.Ordinal)))
                {
                    errors.Add(error);
                }
            }
        }

        if (!payload.HasChanges)
        {
            errors.Clear();
        }

        Errors.Clear();
        foreach (var error in errors)
        {
            Errors.Add(error);
        }

        UpdatePreview(ipv4Addresses, hostNames);
        ReachabilityWarning = ComputeWarning(payload, ipv4Addresses);
        _payload = errors.Count == 0 && payload.HasChanges ? payload : null;
        CanApply = _payload is not null && (!HasWarning || WarningAcknowledged);
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(Payload));
    }

    private List<string>? AssignIpv4(List<string> errors)
    {
        if (!IsIpv4Static)
        {
            return null;
        }

        if (!IsMultiDevice)
        {
            return [Ipv4Address.Trim()];
        }

        if (string.IsNullOrWhiteSpace(Ipv4Address))
        {
            errors.Add("IPv4: enter the start address of the range.");
            return null;
        }

        var prefix = Ipv4.TryParsePrefix(Ipv4Mask, out var p) ? p : 32;
        if (!Ipv4.TryAssignRange(Ipv4Address.Trim(), prefix, _devices.Count, out var addresses, out var error))
        {
            errors.Add("IPv4: " + error);
            return null;
        }

        return addresses.ToList();
    }

    private List<string>? AssignHostNames()
    {
        if (!IsHostNameStatic)
        {
            return null;
        }

        return _devices.Select((d, i) => PayloadValidator.ExpandHostName(HostNameText, i + 1, d.Serial)).ToList();
    }

    private void UpdatePreview(List<string>? addresses, List<string>? hostNames)
    {
        Preview.Clear();
        if (!IsMultiDevice || (addresses is null && hostNames is null))
        {
            return;
        }

        for (var i = 0; i < _devices.Count; i++)
        {
            var device = _devices[i];
            Preview.Add(new PreviewRow(
                Label(device),
                device.Address,
                addresses?.ElementAtOrDefault(i) ?? "unchanged",
                hostNames?.ElementAtOrDefault(i) ?? "unchanged"));
        }
    }

    private string? ComputeWarning(NetworkPayload payload, List<string>? addresses)
    {
        var parts = new List<string>();
        if (payload.Ipv4 is { Mode: Ipv4Mode.Static } && addresses is not null)
        {
            var moving = _devices.Where((d, i) => !string.Equals(d.Address, addresses.ElementAtOrDefault(i), StringComparison.OrdinalIgnoreCase)).Count();
            if (moving > 0)
            {
                parts.Add(moving == 1 && !IsMultiDevice
                    ? "The device gets a new IPv4 address. OADM reaches it at its current address and loses contact after the change, until you add it again with the new address."
                    : $"{moving} of {_devices.Count} devices get a new IPv4 address. OADM reaches them at their current address and loses contact after the change, until you add them again with the new address.");
            }

            parts.Add("A wrong subnet mask or gateway makes devices unreachable from the server. Make sure the server can reach the new addresses.");
        }
        else if (payload.Ipv4 is { Mode: Ipv4Mode.Dhcp })
        {
            parts.Add("With DHCP the devices get their address from the DHCP server. It can differ from the current address, and OADM then loses contact with them. Make sure a DHCP server is available on their network.");
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

    private static string ModeText(string? mode) => mode?.ToLowerInvariant() switch
    {
        "dhcp" => "DHCP",
        "static" => "static",
        "auto" => "automatic",
        null => "mode unknown",
        _ => mode,
    };

    private static string AddressText(string? address, int? prefix) =>
        address is null ? "no address" : prefix is { } p ? $"{address} / {Ipv4.MaskText(p)}" : address;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
