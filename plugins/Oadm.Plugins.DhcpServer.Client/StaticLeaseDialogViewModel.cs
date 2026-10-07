using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.DhcpServer.Client;

/// <summary>
/// "+ Static lease" / edit dialog: MAC address, IP address (inside the subnet, may be outside the range, must not collide),
/// optional name. Errors directly under the fields; the server checks again and its answer goes under the field too.
/// </summary>
public sealed partial class StaticLeaseDialogViewModel : ValidatingViewModel
{
    private readonly Func<string, string?, CancellationToken, Task<string?>> _invoke;
    private readonly DhcpNetworkInfo? _network;
    private readonly Func<ulong, LeaseInfo?> _leaseOfMac;
    private readonly Func<uint, LeaseInfo?> _leaseOfAddress;
    private readonly string? _originalMac;

    /// <param name="invoke">Page backend call (method, payload).</param>
    /// <param name="network">Subnet of the selected interface (null: not known, format checks only).</param>
    /// <param name="leaseOfMac">The page's lease of a MAC (collision checks while typing).</param>
    /// <param name="leaseOfAddress">The page's lease of an address.</param>
    /// <param name="edit">The static lease being edited; null = add.</param>
    public StaticLeaseDialogViewModel(
        Func<string, string?, CancellationToken, Task<string?>> invoke,
        DhcpNetworkInfo? network,
        Func<ulong, LeaseInfo?> leaseOfMac,
        Func<uint, LeaseInfo?> leaseOfAddress,
        LeaseInfo? edit = null)
    {
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _network = network;
        _leaseOfMac = leaseOfMac ?? throw new ArgumentNullException(nameof(leaseOfMac));
        _leaseOfAddress = leaseOfAddress ?? throw new ArgumentNullException(nameof(leaseOfAddress));
        if (edit is not null)
        {
            _originalMac = edit.Mac;
            Mac = edit.Mac;
            Address = edit.Address;
            Name = edit.Name ?? string.Empty;
        }

        Validation.Rules([nameof(Mac), nameof(Address), nameof(Name)], Check);
        Validation.Validate();
    }

    public string Title => _originalMac is null ? "Static lease" : "Edit static lease";

    public string SubnetHint => _network is null
        ? "The device always gets this address."
        : $"Inside {_network.Subnet}; may be outside the range of the DHCP server.";

    [ObservableProperty]
    public partial string Mac { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>Saved: the dialog closes (true) or was cancelled (false).</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool Saved { get; private set; }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var request = new StaticLeaseRequest(Mac.Trim(), Address.Trim(), string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(), _originalMac);
            var json = await _invoke(DhcpServerMethods.SaveStatic, DhcpJson.Serialize(request), CancellationToken.None).ConfigureAwait(true);
            var reply = DhcpJson.Deserialize<StaticLeaseReply>(json);
            if (!reply.Saved)
            {
                foreach (var (field, message) in reply.FieldErrors ?? new Dictionary<string, string>())
                {
                    Validation.SetServerError(field, message);
                }

                return;
            }

            Saved = true;
            CloseRequested?.Invoke(this, true);
        }
#pragma warning disable CA1031 // Shown under the MAC field (the dialog stays open).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(Mac), DhcpServerViewModel.Message(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private bool CanSave() => !IsSaving && IsFormValid;

    protected override void OnValidationChanged() => SaveCommand.NotifyCanExecuteChanged();

    private Dictionary<string, string?> Check()
    {
        var errors = DhcpValidation.StaticLease(Mac, Address, Name, _network).ToDictionary(e => e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        ulong? original = _originalMac is not null && MacAddress.TryParse(_originalMac, out var o) ? o : null;
        if (!errors.ContainsKey(nameof(Mac)) && MacAddress.TryParse(Mac, out var mac) && mac != original
            && _leaseOfMac(mac) is { IsStatic: true } own)
        {
            errors[nameof(Mac)] = $"This device already has a static lease ({own.Address}).";
        }

        if (!errors.ContainsKey(nameof(Address)) && Ipv4.TryParse(Address, out var address) && _leaseOfAddress(address) is { } holder
            && MacAddress.TryParse(holder.Mac, out var holderMac) && holderMac != original
            && (!MacAddress.TryParse(Mac, out var current) || holderMac != current))
        {
            if (holder.IsStatic)
            {
                errors[nameof(Address)] = $"Already reserved for {holder.Mac}.";
            }
            else if (holder.State == LeaseInfo.Active)
            {
                errors[nameof(Address)] = $"In use by {holder.Mac}.";
            }
        }

        return errors;
    }
}
