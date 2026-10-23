using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The objects of the core a device group can own: compliance checks (table ComplianceChecks, columns OwnerAllDevices +
/// OwnerGroupId; event rules have no owner, they are for users with all devices). Deleting the group clears their owner
/// and turns them off; the compliance engine reloads them on <see cref="DeviceGroupStore.Deleted"/>.
/// </summary>
public sealed class CoreOwnedObjects : IDeviceGroupOwnedObjects
{
    public async Task<string?> DisableOwnedAsync(OadmDbContext db, Guid groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var checks = await db.ComplianceChecks.Where(c => c.OwnerGroupId == groupId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerGroupId, (Guid?)null).SetProperty(c => c.OwnerAllDevices, false).SetProperty(c => c.Enabled, false), ct)
            .ConfigureAwait(false);
        return checks > 0 ? DeviceGroupStore.Count(checks, "compliance check", "compliance checks") : null;
    }
}
