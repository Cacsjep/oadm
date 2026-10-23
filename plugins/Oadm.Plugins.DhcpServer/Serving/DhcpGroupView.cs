using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DhcpServer.Serving;

/// <summary>
/// What a user limited to a device group sees of the server-wide DHCP server (docs/spec/device-scope.md, server-wide
/// services): the leases of the group's devices by MAC address only (an Axis serial number is its MAC address). Never by
/// address: a lease of a device outside the group (another managed device, or one OADM does not manage) at the address a
/// group device had before would show to the group. Settings and status are the same for everyone.
/// </summary>
internal sealed class DhcpGroupView
{
    private readonly HashSet<string> _serials;

    private DhcpGroupView(HashSet<string> serials)
    {
        _serials = serials;
    }

    /// <summary>The view of <paramref name="devices"/> (one pass).</summary>
    public static DhcpGroupView From(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var serials = new HashSet<string>(devices.Count, StringComparer.Ordinal);
        foreach (var device in devices)
        {
            serials.Add(Normalize(device.Serial));
        }

        return new DhcpGroupView(serials);
    }

    /// <summary>True when the lease belongs to a device of the group.</summary>
    public bool Covers(LeaseInfo lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return _serials.Contains(Normalize(lease.Mac));
    }

    /// <summary>The leases of the group's devices.</summary>
    public IReadOnlyList<LeaseInfo> Filter(IReadOnlyList<LeaseInfo> leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        return [.. leases.Where(Covers)];
    }

    /// <summary>The changes of the group's leases; null when none is the group's. Removed leases by the devices' MAC address.</summary>
    public LeasesEvent? Filter(LeasesEvent changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var changed = Filter(changes.Changed);
        IReadOnlyList<string> removed = [.. changes.Removed.Where(mac => _serials.Contains(Normalize(mac)))];
        return changed.Count == 0 && removed.Count == 0 ? null : changes with { Changed = changed, Removed = removed };
    }

    /// <summary>The state with the group's leases only.</summary>
    public DhcpState Filter(DhcpState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Leases is { } leases ? state with { Leases = Filter(leases) } : state;
    }

    /// <summary>"ac:cc:8e:12:34:56", "ACCC8E-123456" and the serial "ACCC8E123456" alike.</summary>
    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(Uri.IsHexDigit)).ToUpperInvariant();
}
