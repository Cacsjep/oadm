using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Settings;

namespace Oadm.Core.Events;

/// <summary>An event source whose types belong to an owner (the compliance checks: "Compliance: name" of a group's check).</summary>
public interface IEventTypeOwners
{
    /// <summary>The owner of the type, null for a type that is not one of the source's.</summary>
    DeviceScope? OwnerOfEventType(string typeId);
}

/// <summary>
/// The owners of event rules (docs/spec/device-scope.md, Shared objects): which event types an owner may use and see in the
/// catalog, its devices (<see cref="OwnerScopes"/>), its sites, and the addresses its rules may send to. Users with all
/// devices see and use everything; a group sees the site events of its sites, the compliance checks of the group, and
/// event types that name devices (device triggers) only when one of them is the group's.
/// </summary>
public sealed class RuleOwners(OwnerScopes scopes, ActionDestinations destinations, IEnumerable<IEventTypeOwners>? typeOwners = null)
{
    private readonly IReadOnlyList<IEventTypeOwners> _typeOwners = typeOwners?.ToList() ?? [];

    /// <summary>The devices and groups of owners.</summary>
    public OwnerScopes Scopes => scopes;

    /// <summary>The administrator's list of addresses rules of groups may send to.</summary>
    public ActionDestinations Destinations => destinations;

    /// <summary>
    /// Whether <paramref name="owner"/> may use and see the event type. <paramref name="isOwnersDevice"/> tells whether a
    /// device id is one of the owner's devices (types with <c>DeviceIds</c>).
    /// </summary>
    public bool Sees(CatalogEventType type, DeviceScope owner, Func<Guid, bool> isOwnersDevice)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(isOwnersDevice);
        if (owner.AllDevices)
        {
            return true;
        }

        if (owner.IsNone)
        {
            return false;
        }

        if (SiteEventSource.SiteOf(type.Id) is { } site)
        {
            return scopes.SitesOf(owner)?.Contains(site) ?? true;
        }

        foreach (var source in _typeOwners)
        {
            if (source.OwnerOfEventType(type.Id) is { } typeOwner)
            {
                return typeOwner == owner;
            }
        }

        return type.Info.DeviceIds is not { } ids || ids.Any(isOwnersDevice);
    }
}
