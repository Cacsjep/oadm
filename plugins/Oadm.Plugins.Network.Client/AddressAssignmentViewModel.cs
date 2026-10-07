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
/// (<see cref="AddressConflicts"/>). <see cref="CheckAsync"/> asks the server (query "checkAddresses") which
/// addresses other managed devices have and which answer on TCP 80/443, then suggests again around them.
/// </summary>
public sealed partial class AddressAssignmentViewModel : ObservableObject
{
    private const int MaxRounds = 3;

    private readonly Dictionary<string, AddressStatus> _known = new(StringComparer.Ordinal);
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

    public bool CanCheck => _context is not null && !IsChecking && Rows.Any(r => r.IsEditable);

    /// <summary>New addresses in row order (empty strings while unassigned).</summary>
    public IReadOnlyList<string> Addresses => [.. Rows.Select(r => r.NewAddress.Trim())];

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
        if (_context is null || IsChecking || !Rows.Any(r => r.IsEditable))
        {
            return;
        }

        IsChecking = true;
        try
        {
            var probed = new HashSet<string>(StringComparer.Ordinal);
            for (var round = 0; round < MaxRounds; round++)
            {
                var candidates = Rows
                    .Where(r => r.IsEditable && Ipv4.TryParse(r.NewAddress, out _) && !string.Equals(r.NewAddress.Trim(), r.CurrentAddress, StringComparison.Ordinal))
                    .Select(r => r.NewAddress.Trim())
                    .Where(probed.Add)
                    .Take(AddressCheckRequest.MaxProbes)
                    .ToList();
                if (round > 0 && candidates.Count == 0)
                {
                    break;
                }

                CheckStatus = candidates.Count == 1 ? "Checking 1 address..." : $"Checking {candidates.Count} addresses...";
                var json = await _context.QueryAsync(QueryDeviceId, AddressCheck.QueryMethod, new AddressCheckRequest(candidates).ToJson(), ct).ConfigureAwait(true);
                Merge(AddressCheckResponse.Parse(json));
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

            var inUse = _known.Values.Count(s => s.InUse && probed.Contains(s.Address));
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

    /// <summary>Merges server knowledge (tests and <see cref="CheckAsync"/>).</summary>
    public void Merge(AddressCheckResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        foreach (var managed in response.Managed)
        {
            _known[managed.Address] = managed with { InUse = _known.GetValueOrDefault(managed.Address)?.InUse ?? false };
        }

        foreach (var probe in response.Probed)
        {
            _known[probe.Address] = _known.TryGetValue(probe.Address, out var existing)
                ? existing with { InUse = probe.InUse }
                : probe;
        }

        Update(() => { });
    }

    private void Suggest()
    {
        var unavailable = _known.Values
            .Where(s => s.DeviceId is not null || s.InUse)
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

            var conflicts = AddressConflicts.Find([.. Rows.Select(r => r.ToAssignmentRow())], _prefix, _gateway, _known);
            for (var i = 0; i < Rows.Count; i++)
            {
                Rows[i].Conflict = Rows[i].IsEditable ? conflicts[i] : null;
            }
        }
        finally
        {
            _updating = false;
        }

        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(Addresses));
        CheckCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updating && e.PropertyName == nameof(AddressRowViewModel.NewAddress))
        {
            Update(() => { });
        }
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
