using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The objects of the core a device group can own: event rules and compliance checks (tables EventRules, ComplianceChecks,
/// columns OwnerAllDevices + OwnerGroupId). Deleting the group clears their owner and turns them off; the rule and
/// compliance engines reload them on <see cref="DeviceGroupStore.Deleted"/>.
/// </summary>
public sealed class CoreOwnedObjects : IDeviceGroupOwnedObjects
{
    public async Task<string?> DisableOwnedAsync(OadmDbContext db, Guid groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rules = await db.EventRules.Where(r => r.OwnerGroupId == groupId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerGroupId, (Guid?)null).SetProperty(r => r.OwnerAllDevices, false).SetProperty(r => r.Enabled, false), ct)
            .ConfigureAwait(false);
        var checks = await db.ComplianceChecks.Where(c => c.OwnerGroupId == groupId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerGroupId, (Guid?)null).SetProperty(c => c.OwnerAllDevices, false).SetProperty(c => c.Enabled, false), ct)
            .ConfigureAwait(false);
        var parts = new List<string>();
        if (rules > 0)
        {
            parts.Add(DeviceGroupStore.Count(rules, "event rule", "event rules"));
        }

        if (checks > 0)
        {
            parts.Add(DeviceGroupStore.Count(checks, "compliance check", "compliance checks"));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
