using System.Globalization;

using Oadm.Core.Auth;
using Oadm.Sdk.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>Devices that entered or left one device group with one change.</summary>
/// <param name="GroupId">The group.</param>
/// <param name="GroupName">Its name.</param>
/// <param name="Entered">True: they entered it; false: they left it.</param>
/// <param name="Serials">The devices, by serial number, sorted.</param>
public sealed record DeviceGroupMembershipChange(Guid GroupId, string GroupName, bool Entered, IReadOnlyList<string> Serials);

/// <summary>
/// The audit of a device's membership (docs/spec/device-scope.md, Decisions "Membership"): every change of the groups a device
/// is in (tag, site, relocation, add, remove) is audited as "Entered device group" / "Left device group" with the group as the
/// target and the devices in the detail. One entry per group and change, never one per device. The groups are checked with
/// their compiled filters (a few hash lookups per device and group).
/// </summary>
public static class DeviceGroupMembership
{
    /// <summary>The audit action of devices that entered a group.</summary>
    public const string EnteredAction = "Entered device group";

    /// <summary>The audit action of devices that left a group.</summary>
    public const string LeftAction = "Left device group";

    /// <summary>Most serial numbers named in one audit entry; the rest are counted.</summary>
    public const int MaxSerialsInDetail = 20;

    /// <summary>The groups the new <paramref name="devices"/> are in (an add).</summary>
    public static Task<IReadOnlyList<DeviceGroupMembershipChange>> EnteredAsync(DeviceViewFactory views, IReadOnlyCollection<IDeviceInfo> devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return ChangesAsync(views, [.. devices.Select(d => ((IDeviceInfo?)null, (IDeviceInfo?)d))], ct);
    }

    /// <summary>
    /// The groups each device left and entered between <c>Before</c> and <c>After</c> (null Before: added; null After:
    /// removed).
    /// </summary>
    public static async Task<IReadOnlyList<DeviceGroupMembershipChange>> ChangesAsync(DeviceViewFactory views,
        IReadOnlyCollection<(IDeviceInfo? Before, IDeviceInfo? After)> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return [];
        }

        // Loads the groups and the catalog once.
        await views.ForScopeAsync(DeviceScope.None, null, ct).ConfigureAwait(false);
        var groups = views.Groups.Loaded?.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var result = new List<DeviceGroupMembershipChange>();
        foreach (var group in groups)
        {
            var filter = views.FilterOf(group.Id);
            var entered = new List<string>();
            var left = new List<string>();
            foreach (var (before, after) in changes)
            {
                var was = before is not null && filter.Contains(before);
                var isIn = after is not null && filter.Contains(after);
                if (isIn && !was)
                {
                    entered.Add(after!.Serial);
                }
                else if (was && !isIn)
                {
                    left.Add(before!.Serial);
                }
            }

            if (left.Count > 0)
            {
                result.Add(new DeviceGroupMembershipChange(group.Id, group.Name, false, [.. left.Order(StringComparer.Ordinal)]));
            }

            if (entered.Count > 0)
            {
                result.Add(new DeviceGroupMembershipChange(group.Id, group.Name, true, [.. entered.Order(StringComparer.Ordinal)]));
            }
        }

        return result;
    }

    /// <summary>"3 devices (added): ACCC8E000001, ACCC8E000002, ACCC8E000003" (at most <see cref="MaxSerialsInDetail"/> named).</summary>
    public static string Detail(DeviceGroupMembershipChange change, string reason)
    {
        ArgumentNullException.ThrowIfNull(change);
        var count = change.Serials.Count == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{change.Serials.Count:N0} devices");
        var named = string.Join(", ", change.Serials.Take(MaxSerialsInDetail));
        var more = change.Serials.Count > MaxSerialsInDetail
            ? string.Create(CultureInfo.InvariantCulture, $" and {change.Serials.Count - MaxSerialsInDetail:N0} more")
            : string.Empty;
        return string.IsNullOrWhiteSpace(reason) ? $"{count}: {named}{more}" : $"{count} ({reason}): {named}{more}";
    }

    /// <summary>Writes one audit entry per change (user and client of the change; "system" for background work).</summary>
    public static async Task WriteAsync(AuditLog audit, string userName, string clientAddress, IReadOnlyList<DeviceGroupMembershipChange> changes, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            await audit.WriteAsync(userName, clientAddress, change.Entered ? EnteredAction : LeftAction, change.GroupName, Detail(change, reason), ct).ConfigureAwait(false);
        }
    }
}
