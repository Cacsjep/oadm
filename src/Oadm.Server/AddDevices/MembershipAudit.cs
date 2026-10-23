using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Server.AddDevices;

/// <summary>
/// The membership audit of adds and moves (docs/spec/device-scope.md, Decisions "Membership"): a new device "Entered device
/// group X", a device that moved to another route left the groups of its old site and entered those of the new one. One
/// entry per group (<see cref="DeviceGroupMembership"/>). The rows are read back through the system view (the audit covers
/// every group, whoever made the change). Never fails the add or the move it reports on.
/// </summary>
public static class MembershipAudit
{
    /// <summary>The groups the new devices are in (read back from the device table: what was stored counts).</summary>
    public static async Task AddedAsync(DeviceViewFactory? views, AuditLog audit, string userName, string clientAddress,
        IReadOnlyCollection<Guid> deviceIds, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (views is null || deviceIds.Count == 0)
        {
            return;
        }

        IReadOnlyList<Device> added;
        try
        {
            added = await views.System.GetAsync(deviceIds, ct).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException)
        {
            // One was removed meanwhile: the others still count.
            var wanted = deviceIds.ToHashSet();
            added = [.. (await views.System.ListAsync(ct).ConfigureAwait(false)).Where(d => wanted.Contains(d.Id))];
        }

        var changes = await DeviceGroupMembership.EnteredAsync(views, [.. added], ct).ConfigureAwait(false);
        await DeviceGroupMembership.WriteAsync(audit, userName, clientAddress, changes, Reason(reason), ct).ConfigureAwait(false);
    }

    /// <summary>A device moved (address and route): the groups it left and entered with its new route.</summary>
    public static async Task MovedAsync(DeviceViewFactory? views, AuditLog? audit, Device before, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (views is null || audit is null || await views.System.FindAsync(before.Id, ct).ConfigureAwait(false) is not { } after || after.SiteId == before.SiteId)
        {
            return; // the same route: the same groups
        }

        var changes = await DeviceGroupMembership.ChangesAsync(views, [(before, after)], ct).ConfigureAwait(false);
        await DeviceGroupMembership.WriteAsync(audit, "system", string.Empty, changes, "moved", ct).ConfigureAwait(false);
    }

    private static string Reason(string detail) => string.IsNullOrWhiteSpace(detail) ? "added" : "added, " + detail;
}
