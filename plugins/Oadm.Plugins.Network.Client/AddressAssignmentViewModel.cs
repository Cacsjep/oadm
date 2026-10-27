using System.Collections.ObjectModel;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>
/// The address assignment table shared by "Assign IP address..." and "Network settings...": the selected devices in
/// grid order with current and new address. New addresses are suggested from an <see cref="IpRangeExpression"/>
/// (<see cref="AddressAssigner"/>), every row can be edited, conflicts are flagged per row
/// (<see cref="AddressConflicts"/>). With static IPv6 every row also gets its own IPv6 address
/// (<see cref="ShowIpv6"/>). <see cref="CheckAsync"/> asks the server (query "checkAddresses") which addresses other
/// managed devices have and which answer ping or TCP 80/443, then suggests again around them. Devices at different sites
/// are on different networks (docs/spec/sites.md, Discovery and moves at sites): the check runs once per route with a device of that
/// route, and each row is judged by what its own route knows.
/// </summary>
public sealed partial class AddressAssignmentViewModel : ObservableObject
{
    private const int MaxRounds = 3;

    /// <summary>What the server knows per route (the site id; <see cref="Guid.Empty"/> = the server's own network), by address.</summary>
    private readonly Dictionary<Guid, Dictionary<string, AddressStatus>> _known = [];
    private ITaskDialogContext? _context;
    private IpRangeExpression? _range;
    private int? _prefix;
    private string? _gateway;
    private bool _updating;

    public AddressAssignmentViewModel(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        foreach (var device in devices)
        {
            var row = new AddressRowViewModel(device);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
    }

    /// <summary>Raised when an address, a conflict or the error changed (hosts re-validate).</summary>
    public event EventHandler? Changed;

    public ObservableCollection<AddressRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool ShowHostName { get; set; }

    /// <summary>Static IPv6: the "New IPv6 address" column is shown and every row needs an IPv6 address.</summary>
    [ObservableProperty]
    public partial bool ShowIpv6 { get; set; }

    /// <summary>"Not enough addresses" when the range is too small, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Result of the last server check ("Checked 4 addresses: 1 in use was skipped.").</summary>
    [ObservableProperty]
    public partial string CheckStatus { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsChecking { get; set; }

    public bool HasConflicts => Rows.Any(r => r.HasConflict);

    /// <summary>"4 ready, 1 conflict" next to Check addresses (O(n) per change).</summary>
    public string Summary
    {
        get
        {
            int ready = 0, conflicts = 0;
            foreach (var row in Rows)
            {
                if (row.HasConflict)
                {
                    conflicts++;
                }
                else if (row.IsReady)
                {
                    ready++;
                }
            }

            return conflicts == 0
                ? $"{ready} ready"
                : $"{ready} ready, {Plural(conflicts, "conflict", "conflicts")}";
        }
    }

    public bool CanCheck => _context is not null && !IsChecking && Rows.Any(r => r.IsEditable || r.IsIpv6Editable);

    /// <summary>New addresses in row order (empty strings while unassigned).</summary>
    public IReadOnlyList<string> Addresses => [.. Rows.Select(r => r.NewAddress.Trim())];

    /// <summary>New IPv6 addresses in row order (static IPv6).</summary>
    public IReadOnlyList<string> Ipv6Addresses => [.. Rows.Select(r => r.NewIpv6Address.Trim())];

    /// <summary>Server access for <see cref="CheckAsync"/>; <paramref name="queryDeviceId"/> is the device the query runs for.</summary>
    public void Attach(ITaskDialogContext context, Guid queryDeviceId)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        QueryDeviceId = queryDeviceId;
        CheckCommand.NotifyCanExecuteChanged();
    }

    public Guid QueryDeviceId { get; private set; }

    /// <summary>IPv4 stays unchanged: rows show "Unchanged" and are read-only.</summary>
    public void Clear()
    {
        _range = null;
        Update(() =>
        {
            foreach (var row in Rows)
            {
                row.IsEditable = false;
                row.Suggest("Unchanged");
                row.Conflict = null;
            }

            Error = null;
        });
    }

    /// <summary>
    /// Suggests addresses from <paramref name="range"/>. With <paramref name="resetEdits"/> (a new range) user edits are
    /// discarded, otherwise edited rows keep their address and the others go around them.
    /// </summary>
    public void Assign(IpRangeExpression range, int? prefixLength, string? gateway, bool resetEdits)
    {
        ArgumentNullException.ThrowIfNull(range);
        _range = range;
        _prefix = prefixLength;
        _gateway = gateway;
        Update(() =>
        {
            foreach (var row in Rows)
            {
                row.IsEditable = true;
                if (resetEdits)
                {
                    row.IsEdited = false;
                }
            }

            Suggest();
        });
    }

    /// <summary>
    /// No suggestion is possible (no start address known): the rows become editable with what they have, the user
    /// types every address.
    /// </summary>
    public void EditManually(int? prefixLength, string? gateway)
    {
        _range = null;
        _prefix = prefixLength;
        _gateway = gateway;
        Update(() =>
        {
            foreach (var row in Rows)
            {
                if (!row.IsEditable)
                {
                    row.Suggest(string.Empty);
                }

                row.IsEditable = true;
            }

            Error = null;
        });
    }

    /// <summary>Static IPv6 on (every row gets an editable IPv6 address) or off.</summary>
    public void SetIpv6(bool enabled)
    {
        if (ShowIpv6 == enabled && Rows.All(r => r.IsIpv6Editable == enabled))
        {
            return;
        }

        ShowIpv6 = enabled;
        Update(() =>
        {
            foreach (var row in Rows)
            {
                row.IsIpv6Editable = enabled;
            }
        });
    }

    /// <summary>Subnet mask or default router changed: only the conflicts are re-evaluated.</summary>
    public void UpdateNetwork(int? prefixLength, string? gateway)
    {
        _prefix = prefixLength;
        _gateway = gateway;
        Update(() => { });
    }

    /// <summary>Host names per row (Network settings), null = unchanged.</summary>
    public void SetHostNames(IReadOnlyList<string>? names)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].NewHostName = names?.ElementAtOrDefault(i) ?? "Unchanged";
        }
    }

    /// <summary>
    /// Asks the server which addresses are taken (managed devices, TCP 80/443 answers) and suggests again around
    /// them, up to three rounds. Never throws; a failure ends up in <see cref="CheckStatus"/>.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        if (_context is null || IsChecking || !Rows.Any(r => r.IsEditable || r.IsIpv6Editable))
        {
            return;
        }

        IsChecking = true;
        try
        {
            var probed = new HashSet<(Guid Route, string Address)>();
            for (var round = 0; round < MaxRounds; round++)
            {
                // One query per route, run for a device of that route: its probes go through that route.
                var asked = 0;
                foreach (var group in Rows.GroupBy(RouteOf))
                {
                    var route = group.Key;
                    var candidates = group
                        .Where(r => r.IsEditable && Ipv4.TryParse(r.NewAddress, out _) && !string.Equals(r.NewAddress.Trim(), r.CurrentAddress, StringComparison.Ordinal))
                        .Select(r => r.NewAddress.Trim())
                        .Concat(group
                            .Where(r => r.IsIpv6Editable && PayloadValidator.TryParseIpv6(r.NewIpv6Address, out _) && !AddressCheck.IsOwnAddress(r.NewIpv6Address, [r.CurrentAddress]))
                            .Select(r => r.NewIpv6Address.Trim()))
                        .Where(a => probed.Add((route, a)))
                        .Take(AddressCheckRequest.MaxProbes)
                        .ToList();
                    if (round > 0 && candidates.Count == 0)
                    {
                        continue;
                    }

                    asked += candidates.Count;
                    CheckStatus = asked == 1 ? "Checking 1 address..." : $"Checking {asked} addresses...";
                    var queryDevice = group.Any(r => r.Device.Id == QueryDeviceId) ? QueryDeviceId : group.First().Device.Id;
                    var json = await _context.QueryAsync(queryDevice, AddressCheck.QueryMethod, new AddressCheckRequest(candidates).ToJson(), ct).ConfigureAwait(true);
                    Merge(AddressCheckResponse.Parse(json), route);
                }

                if (round > 0 && asked == 0)
                {
                    break;
                }

                var before = Addresses;
                Update(() =>
                {
                    if (_range is not null)
                    {
                        Suggest();
                    }
                });
                if (before.SequenceEqual(Addresses))
                {
                    break;
                }
            }

            var inUse = _known.Sum(route => route.Value.Values.Count(s => s.InUse && probed.Contains((route.Key, s.Address))));
            CheckStatus = inUse == 0
                ? $"Checked {Plural(probed.Count, "address", "addresses")}: none is in use."
                : $"Checked {Plural(probed.Count, "address", "addresses")}: {Plural(inUse, "address is", "addresses are")} in use{(_range is null ? "." : " and skipped where possible.")}";
        }
        catch (OperationCanceledException)
        {
            CheckStatus = string.Empty;
        }
#pragma warning disable CA1031 // The check is advisory; the dialog stays usable and the server validates again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CheckStatus = "The addresses could not be checked: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task Check() => CheckAsync(CancellationToken.None);

    /// <summary>Merges server knowledge into the route of every row (tests with one route).</summary>
    public void Merge(AddressCheckResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        foreach (var route in Rows.Select(RouteOf).Distinct().ToList())
        {
            MergeInto(KnownOf(route), response);
        }

        Update(() => { });
    }

    /// <summary>Merges the answer of the query for <paramref name="route"/> (<see cref="CheckAsync"/> and tests).</summary>
    public void Merge(AddressCheckResponse response, Guid? route)
    {
        ArgumentNullException.ThrowIfNull(response);
        MergeInto(KnownOf(route ?? Guid.Empty), response);
        Update(() => { });
    }

    private static void MergeInto(Dictionary<string, AddressStatus> known, AddressCheckResponse response)
    {
        foreach (var managed in response.Managed)
        {
            known[managed.Address] = managed with { InUse = known.GetValueOrDefault(managed.Address)?.InUse ?? false };
        }

        foreach (var probe in response.Probed)
        {
            known[probe.Address] = known.TryGetValue(probe.Address, out var existing)
                ? existing with { InUse = probe.InUse, OutsideSite = probe.OutsideSite, UsedByDevice = probe.UsedByDevice }
                : probe;
        }
    }

    /// <summary>The route of a row: its site, <see cref="Guid.Empty"/> on the server's own network.</summary>
    private static Guid RouteOf(AddressRowViewModel row) => row.Device.SiteId ?? Guid.Empty;

    private Dictionary<string, AddressStatus> KnownOf(Guid route)
    {
        if (!_known.TryGetValue(route, out var known))
        {
            _known[route] = known = new Dictionary<string, AddressStatus>(StringComparer.Ordinal);
        }

        return known;
    }

    private void Suggest()
    {
        // One range for the whole table: an address taken on any route is skipped (never suggests a taken one).
        var unavailable = _known.Values.SelectMany(k => k.Values)
            .Where(s => s.DeviceId is not null || s.InUse || s.OutsideSite)
            .Select(s => s.Address)
            .Concat(Rows.Where(r => r.IsEdited).Select(r => r.NewAddress.Trim()))
            .ToHashSet(StringComparer.Ordinal);
        var open = Rows.Where(r => !r.IsEdited).ToList();
        var result = AddressAssigner.Assign(_range!, _prefix, _gateway, [.. open.Select(r => new AssignmentDevice(r.Device.Id, r.CurrentAddress))], unavailable);
        for (var i = 0; i < open.Count; i++)
        {
            open[i].Suggest(result.Addresses[i]);
        }

        Error = result.Error;
    }

    private void Update(Action change)
    {
        _updating = true;
        try
        {
            change();
            if (Error is not null && Rows.All(r => r.NewAddress.Trim().Length > 0))
            {
                Error = null; // the user filled the rows the range had no address for
            }

            // The table's own rules over every row; what the server knows only for the rows of the same route.
            var rows = Rows.Select(r => r.ToAssignmentRow()).ToList();
            var ipv6Rows = Rows.Select(r => r.ToIpv6Row()).ToList();
            var conflicts = AddressConflicts.Find(rows, _prefix, _gateway).ToArray();
            var ipv6 = AddressConflicts.FindIpv6(ipv6Rows).ToArray();
            foreach (var group in Enumerable.Range(0, Rows.Count).GroupBy(i => RouteOf(Rows[i])))
            {
                if (!_known.TryGetValue(group.Key, out var known))
                {
                    continue;
                }

                var indices = group.ToList();
                var routeConflicts = AddressConflicts.Find([.. indices.Select(i => rows[i])], _prefix, _gateway, known);
                var routeIpv6 = AddressConflicts.FindIpv6([.. indices.Select(i => ipv6Rows[i])], known);
                for (var j = 0; j < indices.Count; j++)
                {
                    conflicts[indices[j]] ??= routeConflicts[j];
                    ipv6[indices[j]] ??= routeIpv6[j];
                }
            }

            for (var i = 0; i < Rows.Count; i++)
            {
                Rows[i].Conflict = Rows[i].IsEditable ? conflicts[i] : null;
                Rows[i].Ipv6Conflict = Rows[i].IsIpv6Editable ? ipv6[i] : null;
            }
        }
        finally
        {
            _updating = false;
        }

        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Addresses));
        OnPropertyChanged(nameof(Ipv6Addresses));
        CheckCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updating && e.PropertyName is nameof(AddressRowViewModel.NewAddress) or nameof(AddressRowViewModel.NewIpv6Address))
        {
            Update(() => { });
        }
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
