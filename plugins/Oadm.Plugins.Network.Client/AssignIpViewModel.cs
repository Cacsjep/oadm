using System.Collections;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>
/// "Assign IP address..." dialog, a clone of ADM's "Assign IP address to selected devices":
/// page 1 "Obtain IP addresses automatically (DHCP)" or "Assign the following IP address range" with IP range,
/// subnet mask, default router (plus optional DNS servers); page 2 lists the devices in grid order with current and
/// new IP address, every new address editable, conflicts flagged. DHCP finishes on page 1.
/// The payload is a <see cref="NetworkPayload"/> with the IPv4 section (and DNS servers when entered).
/// Errors appear once: below their field (<see cref="INotifyDataErrorInfo"/>) or in the row of the table. Finish asks
/// for confirmation (<see cref="Confirm"/>, the host's shared message window) with the reachability warning.
/// </summary>
public sealed partial class AssignIpViewModel : ObservableObject, IDisposable, INotifyDataErrorInfo
{
    private static readonly HashSet<string> Inputs =
    [
        nameof(UseDhcp), nameof(IpRange), nameof(SubnetMask), nameof(DefaultRouter), nameof(DnsPrimary), nameof(DnsSecondary),
    ];

    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly FieldErrors _fieldErrors;
    private readonly bool _initialized;
    private ITaskDialogContext? _context;
    private NetworkPayload? _payload;
    private CancellationTokenSource? _check;
    private bool _recomputing;

    public AssignIpViewModel(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            throw new ArgumentException("At least one device is required.", nameof(devices));
        }

        _devices = devices;
        _fieldErrors = new FieldErrors(name => ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name)));
        Assignment = new AddressAssignmentViewModel(devices);
        Assignment.Changed += (_, _) => Recompute();
        Assignment.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddressAssignmentViewModel.IsChecking))
            {
                Recompute();
            }
        };
        _initialized = true;
        Recompute();
    }

    /// <summary>Raised when the dialog should close: true = finish (see <see cref="ResultJson"/>), false = cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <summary>Confirmation of Finish with the reachability warning (shared message window). Null: no confirmation.</summary>
    public ConfirmChange? Confirm { get; set; }

    /// <summary>Title of the confirmation on Finish.</summary>
    public string ConfirmTitle => UseDhcp ? "The devices get their addresses from DHCP" : "The devices get new IP addresses";
    public AddressAssignmentViewModel Assignment { get; }

    public bool IsMultiDevice => _devices.Count > 1;

    public string Title => IsMultiDevice ? $"Assign IP address to {_devices.Count} devices" : "Assign IP address";

    public string DhcpOptionText => IsMultiDevice ? "Obtain IP addresses automatically (DHCP)" : "Obtain IP address automatically (DHCP)";

    public string RangeOptionText => IsMultiDevice ? "Assign the following IP address range" : "Assign the following IP address";

    public string RangeLabel => IsMultiDevice ? "IP range" : "IP address";

    public string RangePlaceholder => IsMultiDevice ? "192.168.0.10-20" : "192.168.0.90";

    public string RangeHint => IsMultiDevice
        ? "Use wildcards (192.168.0.* or 10.*.1.*), first and last address (192.168.0.10-192.168.0.20, shortened 192.168.0.10-20), " +
          "a range in any part (10.10-30.1.*) and commas for several ranges (192.168.0.*,192.168.1.10-20). " +
          "A single address is the first of consecutive addresses."
        : "The address may also be a range; the first free address is used.";

    /// <summary>True: DHCP. False: "Assign the following IP address range".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseRange), nameof(PrimaryText), nameof(PageDescription))]
    public partial bool UseDhcp { get; set; }

    public bool UseRange
    {
        get => !UseDhcp;
        set => UseDhcp = !value;
    }

    [ObservableProperty]
    public partial string IpRange { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SubnetMask { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DefaultRouter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsPrimary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsSecondary { get; set; } = string.Empty;

    /// <summary>1 = settings, 2 = review of the new addresses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage), nameof(IsReviewPage), nameof(PrimaryText), nameof(PageDescription))]
    public partial int Page { get; set; } = 1;

    public bool IsSettingsPage => Page == 1;

    public bool IsReviewPage => Page == 2;

    /// <summary>Next on the settings page of a static assignment, Finish otherwise.</summary>
    public string PrimaryText => IsSettingsPage && !UseDhcp ? "Next" : "Finish";

    public string PageDescription => IsReviewPage
        ? "Step 2 of 2. Review the current IP addresses and the new IP addresses. Click a new IP address to edit it."
        : UseDhcp
            ? (IsMultiDevice ? $"{_devices.Count} devices selected. The devices get their addresses from a DHCP server." : "1 device selected. The device gets its address from a DHCP server.")
            : (IsMultiDevice ? $"Step 1 of 2. {_devices.Count} devices selected, addresses are suggested in the order of the device list." : "Step 1 of 2.");

    /// <summary>Only set when the current settings could not be read (the values themselves are prefilled).</summary>
    [ObservableProperty]
    public partial string PrefillStatus { get; set; } = string.Empty;

    /// <summary>The reachability warning, shown in the confirmation on Finish.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? Warning { get; set; }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial bool CanContinue { get; set; }

    /// <summary>Why Next / Finish is disabled (its tooltip), null when it is enabled.</summary>
    [ObservableProperty]
    public partial string? BlockedReason { get; set; }

    /// <summary>A field has an error (shown below that field).</summary>
    public bool HasErrors => _fieldErrors.HasErrors;

    public IEnumerable GetErrors(string? propertyName) => _fieldErrors.GetErrors(propertyName);

    /// <summary>The error of one field (tests).</summary>
    public string? ErrorOf(string propertyName) => _fieldErrors[propertyName];

    /// <summary>Payload JSON after Finish; null before.</summary>
    public string? ResultJson { get; private set; }

    public NetworkPayload? Payload => _payload;

    /// <summary>Stops a running address check.</summary>
    public void Dispose()
    {
        _check?.Cancel();
        _check?.Dispose();
        _check = null;
    }

    /// <summary>Connects the server: the address check of the table and the prefill of mask, router and current values.</summary>
    public async Task LoadCurrentAsync(ITaskDialogContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _context = ctx;
        Assignment.Attach(ctx, _devices[0].Id);
        try
        {
            var json = await ctx.QueryAsync(_devices[0].Id, NetworkSettingsTaskPlugin.QueryGetNetworkInfo, null, ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(json))
            {
                ApplyCurrent(CurrentNetworkSettings.FromJson(json));
            }
        }
        catch (OperationCanceledException)
        {
            // dialog closed
        }
#pragma warning disable CA1031 // A failed prefill never blocks the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PrefillStatus = "The current settings could not be read: " + ex.Message;
        }
    }

    /// <summary>Prefills subnet mask and default router from the first device when empty.</summary>
    public void ApplyCurrent(CurrentNetworkSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var v4 = current.Ipv4;
        var mask = v4.StaticPrefixLength ?? v4.PrefixLength;
        if (string.IsNullOrWhiteSpace(SubnetMask) && mask is { } prefix)
        {
            SubnetMask = Ipv4.MaskText(prefix);
        }

        if (string.IsNullOrWhiteSpace(DefaultRouter) && (v4.Gateway ?? v4.StaticGateway) is { } router)
        {
            DefaultRouter = router;
        }
    }

    /// <summary>Next (page 1 of a range) or Finish: confirms the change with the reachability warning first.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task PrimaryAsync()
    {
        if (IsSettingsPage && !UseDhcp)
        {
            GoToReview();
            return;
        }

        Recompute();
        if (!CanContinue || _payload is null)
        {
            return;
        }

        var payload = _payload;
        if (HasWarning && Confirm is { } confirm && !await confirm(ConfirmTitle, Warning!, "Finish").ConfigureAwait(true))
        {
            return;
        }

        ResultJson = payload.ToJson();
        CloseRequested?.Invoke(this, true);
    }

    /// <summary>Page 2: suggest the addresses, then let the server check them in the background.</summary>
    public void GoToReview()
    {
        Page = 2;
        Recompute(newRange: true);
        if (_context is not null)
        {
            _check?.Cancel();
            _check?.Dispose();
            _check = new CancellationTokenSource();
            _ = Assignment.CheckAsync(_check.Token);
        }
    }

    [RelayCommand]
    private void Back()
    {
        _check?.Cancel();
        Page = 1;
        Recompute();
    }

    [RelayCommand]
    private void Cancel()
    {
        _check?.Cancel();
        ResultJson = null;
        CloseRequested?.Invoke(this, false);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (_initialized && e.PropertyName is { } name && Inputs.Contains(name))
        {
            Recompute(newRange: name == nameof(IpRange));
        }
    }

    private void Recompute(bool newRange = false)
    {
        if (_recomputing)
        {
            return;
        }

        _recomputing = true;
        try
        {
            RecomputeCore(newRange);
        }
        finally
        {
            _recomputing = false;
        }
    }

    private void RecomputeCore(bool newRange)
    {
        var fields = new Dictionary<string, string?>();
        string? tableError = null;
        int? prefix = Ipv4.TryParsePrefix(SubnetMask, out var p) ? p : null;
        var router = DefaultRouter.Trim();
        var servers = new[] { DnsPrimary, DnsSecondary }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        List<string>? addresses = null;

        if (!UseDhcp)
        {
            // Page 1: the fields shared by all devices, with the same rules the server applies, each below its field.
            if (!IpRangeExpression.TryParse(IpRange, out var range, out var rangeError))
            {
                fields[nameof(IpRange)] = Strip(rangeError!);
            }

            foreach (var error in PayloadValidator.ValidateIpv4Network(prefix, router))
            {
                fields.TryAdd(error.Contains("subnet mask", StringComparison.Ordinal) ? nameof(SubnetMask) : nameof(DefaultRouter), Strip(error));
            }

            foreach (var server in new[] { (nameof(DnsPrimary), DnsPrimary), (nameof(DnsSecondary), DnsSecondary) })
            {
                if (!string.IsNullOrWhiteSpace(server.Item2) && PayloadValidator.ValidateDnsServers([server.Item2.Trim()]) is [var dnsError, ..])
                {
                    fields[server.Item1] = Strip(dnsError);
                }
            }

            if (!fields.ContainsKey(nameof(DnsPrimary)) && !fields.ContainsKey(nameof(DnsSecondary)) && PayloadValidator.ValidateDnsServers(servers) is [var both, ..])
            {
                fields[nameof(DnsSecondary)] = Strip(both); // e.g. the same server twice
            }

            if (range is not null && IsReviewPage)
            {
                // Suggestions are made when page 2 opens (and by the server check); edits only re-evaluate conflicts.
                if (newRange)
                {
                    Assignment.Assign(range, prefix, router, resetEdits: true);
                }

                tableError = Assignment.Error;
                addresses = [.. Assignment.Addresses];
            }
        }

        _fieldErrors.SetAll(fields);

        var devices = new Dictionary<Guid, DeviceAssignment>();
        for (var i = 0; i < _devices.Count; i++)
        {
            devices[_devices[i].Id] = new DeviceAssignment(addresses?.ElementAtOrDefault(i));
        }

        var payload = new NetworkPayload
        {
            Ipv4 = UseDhcp ? new Ipv4Change(Ipv4Mode.Dhcp) : new Ipv4Change(Ipv4Mode.Static, prefix, router),
            Dns = !UseDhcp && servers.Count > 0 ? new DnsChange(false, servers, KeepDomains: true) : null,
            Devices = devices,
        };

        // Page 2 (and DHCP): the whole payload, exactly as the server validates it before the first write.
        var remaining = !_fieldErrors.HasErrors && tableError is null && (UseDhcp || IsReviewPage)
            ? PayloadValidator.Validate(payload).Select(Strip).ToList()
            : [];
        var rowProblems = IsReviewPage && !UseDhcp && Assignment.HasConflicts;
        var blocked = _fieldErrors.First
            ?? tableError
            ?? (rowProblems ? "Resolve the problems shown in the table, or edit the new IP addresses." : null)
            ?? remaining.FirstOrDefault();

        Warning = UseDhcp
            ? NetworkWarnings.Dhcp
            : string.Join(" ", NetworkWarnings.Static(_devices, addresses ?? []));
        _payload = blocked is null && (UseDhcp || IsReviewPage) ? payload : null;
        if (blocked is null && !IsSettingsPage && Assignment.IsChecking)
        {
            blocked = "Checking the addresses...";
        }

        BlockedReason = blocked;
        CanContinue = IsSettingsPage && !UseDhcp
            ? blocked is null
            : _payload is not null && !Assignment.IsChecking;
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(Payload));
        OnPropertyChanged(nameof(ConfirmTitle));
    }

    /// <summary>The dialog speaks ADM's words: "IPv4: ... gateway" becomes "... default router".</summary>
    private static string Strip(string error)
    {
        var text = FieldErrors.Clean(error)
            .Replace("default gateway", "default router", StringComparison.Ordinal)
            .Replace("gateway", "default router", StringComparison.Ordinal)
            .Replace("Gateway", "Default router", StringComparison.Ordinal);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
