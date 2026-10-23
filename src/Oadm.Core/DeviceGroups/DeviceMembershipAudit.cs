using System.Globalization;

using Oadm.Core.Auth;
using Oadm.Core.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>A device before and after a write (null before: added; null after: removed).</summary>
public sealed record DeviceMembershipChange(DeviceScopeRow? Before, DeviceScopeRow? After);

/// <summary>The compiled rules of every device group at one moment (<see cref="DeviceMembershipAudit.CaptureAsync"/>).</summary>
public sealed class DeviceGroupMembership
{
    internal DeviceGroupMembership(IReadOnlyList<(Guid Id, string Name, DeviceGroupFilter Filter)> groups) => Groups = groups;

    internal IReadOnlyList<(Guid Id, string Name, DeviceGroupFilter Filter)> Groups { get; }
}

/// <summary>
/// The audit of membership changes (docs/spec/device-scope.md, Decisions: Membership): every change of a device's membership
/// (a tag, a site, a relocation, an add, a remove) is written as "Devices left device group" / "Devices entered device
/// group" with the group's name as target. The writer captures the groups' rules before the write
/// (<see cref="CaptureAsync"/>, the old tag and site names resolve as they were) and hands the changed devices after it
/// (<see cref="WriteAsync"/>): one entry per group and direction, at most <see cref="SerialsListed"/> serial numbers named.
/// Shared by every package that moves devices (tags here; sites, relocation, add and remove).
/// </summary>
public sealed class DeviceMembershipAudit(DeviceGroupStore groups, DeviceScopeCatalog catalog, AuditLog audit)
{
    /// <summary>Most serial numbers one entry names ("and 12 more" after them).</summary>
    public const int SerialsListed = 10;

    /// <summary>The rules of every group now (cheap: at most 200 groups, compiled from memory).</summary>
    public async Task<DeviceGroupMembership> CaptureAsync(CancellationToken ct)
    {
        var loaded = await groups.LoadAsync(ct).ConfigureAwait(false);
        await catalog.EnsureLoadedAsync(ct).ConfigureAwait(false);
        return Compile(loaded.Values);
    }

    /// <summary>
    /// Writes the entries for <paramref name="changes"/>: a device in a group before (rules of <paramref name="before"/>) and
    /// not after (the rules now) left it, and the other way round. <paramref name="reason"/> says what moved them ("tag PTZ
    /// removed"). Nothing is written when no group exists or nothing moved. Returns how many entries were written.
    /// </summary>
    public async Task<int> WriteAsync(DeviceGroupMembership before, IReadOnlyCollection<DeviceMembershipChange> changes, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return 0;
        }

        var after = await CaptureAsync(ct).ConfigureAwait(false);
        if (before.Groups.Count == 0 && after.Groups.Count == 0)
        {
            return 0;
        }

        var old = before.Groups.ToDictionary(g => g.Id);
        var now = after.Groups.ToDictionary(g => g.Id);
        var written = 0;
        foreach (var id in old.Keys.Union(now.Keys))
        {
            var oldFilter = old.TryGetValue(id, out var o) ? o.Filter : DeviceGroupFilter.Nothing;
            var newFilter = now.TryGetValue(id, out var n) ? n.Filter : DeviceGroupFilter.Nothing;
            var name = now.TryGetValue(id, out var named) ? named.Name : o.Name;
            var left = new List<string>();
            var entered = new List<string>();
            foreach (var change in changes)
            {
                var was = change.Before is { } b && oldFilter.Contains(b);
                var isIn = change.After is { } a && newFilter.Contains(a);
                if (was && !isIn)
                {
                    left.Add((change.Before ?? change.After)!.Serial);
                }
                else if (!was && isIn)
                {
                    entered.Add((change.After ?? change.Before)!.Serial);
                }
            }

            if (left.Count > 0)
            {
                await audit.WriteAsync(AuditActions.DevicesLeftGroup, name, Detail(left, reason), ct).ConfigureAwait(false);
                written++;
            }

            if (entered.Count > 0)
            {
                await audit.WriteAsync(AuditActions.DevicesEnteredGroup, name, Detail(entered, reason), ct).ConfigureAwait(false);
                written++;
            }
        }

        return written;
    }

    /// <summary>"3 devices (ACCC8E000001, ACCC8E000002, ACCC8E000003): tag PTZ removed".</summary>
    internal static string Detail(IReadOnlyList<string> serials, string reason)
    {
        var count = serials.Count == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{serials.Count:N0} devices");
        var named = string.Join(", ", serials.Order(StringComparer.Ordinal).Take(SerialsListed));
        var more = serials.Count > SerialsListed ? string.Create(CultureInfo.InvariantCulture, $" and {serials.Count - SerialsListed:N0} more") : string.Empty;
        return string.IsNullOrWhiteSpace(reason) ? $"{count} ({named}{more})" : $"{count} ({named}{more}): {reason}";
    }

    private DeviceGroupMembership Compile(IEnumerable<DeviceGroup> all)
    {
        var key = catalog.Key;
        var names = catalog.TagNames;
        return new DeviceGroupMembership([.. all.Select(g => (g.Id, g.Name, DeviceGroupFilter.Compile(g, names, catalog.IsLiveSite, key)))]);
    }
}
