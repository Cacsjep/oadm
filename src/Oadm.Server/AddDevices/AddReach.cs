using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Sites;

namespace Oadm.Server.AddDevices;

/// <summary>
/// Where a user may discover and add devices (docs/spec/device-scope.md, Decisions "New devices" and "Credential list"):
/// a user with all devices everywhere, with the credential list; a user with a device group only at the sites of the group
/// (the server's own network only with "This server"), never with the credential list, and only devices that land inside
/// the group. A group of tags or single devices only cannot add devices (<see cref="CanAdd"/> false). Built from the
/// caller of a call (<see cref="ForAsync"/>) or from the user who kept a waiting device, as that user is now
/// (<see cref="ForUserAsync"/>).
/// </summary>
public sealed class AddReach
{
    private AddReach(DeviceView view, Guid? userId, string userName)
    {
        View = view;
        UserId = userId;
        UserName = userName;
        if (view.HasAllDevices)
        {
            Routes = DiscoveryRoutes.All;
            Waiting = WaitingScope.Everything;
        }
        else
        {
            var filter = view.Filter;
            var sites = filter.Sites.ToHashSet();
            Routes = new DiscoveryRoutes(filter.ThisServer, sites);
            Waiting = new WaitingScope(false, filter.ThisServer, sites, userId);
        }
    }

    /// <summary>The user's devices.</summary>
    public DeviceView View { get; }

    /// <summary>The user; null for the server itself.</summary>
    public Guid? UserId { get; }

    public string UserName { get; }

    /// <summary>All devices: everywhere, with the credential list.</summary>
    public bool AllDevices => View.HasAllDevices;

    /// <summary>Where discovery may look.</summary>
    public DiscoveryRoutes Routes { get; }

    /// <summary>The entries of the waiting list the user sees and changes.</summary>
    public WaitingScope Waiting { get; }

    /// <summary>False for a group of tags or single devices only (and for a user without devices).</summary>
    public bool CanAdd => Routes.Any;

    /// <summary>The credential list is tried only for users with all devices (a fake camera at a site could collect it).</summary>
    public bool UsesCredentialList => AllDevices;

    /// <summary>What a new device stores about who added it.</summary>
    public DeviceScope AddedBy => AllDevices ? DeviceScope.All : View.GroupId is { } group ? DeviceScope.Group(group) : DeviceScope.None;

    /// <summary>True when discovery and adds may use <paramref name="route"/>.</summary>
    public bool Allows(DeviceRoute route) => Routes.Allows(route);

    /// <summary>
    /// True when the device found at <paramref name="route"/> may be added by this user: on an allowed route, and (for a group)
    /// inside the group once added (an excepted serial is not).
    /// </summary>
    public bool Takes(string serial, DeviceRoute route, IReadOnlyList<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        if (AllDevices)
        {
            return true;
        }

        return CanAdd && Allows(route)
            && View.Filter.Contains(DeviceSerial.Normalize(serial), route.SiteId, tags ?? [], false, View.GroupId);
    }

    /// <summary>The reach of the caller of a call.</summary>
    public static async Task<AddReach> ForAsync(DeviceViewFactory views, AuthenticatedCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(caller);
        var view = await views.ForAsync(caller, ct).ConfigureAwait(false);
        return new AddReach(view, caller.UserId, caller.UserName);
    }

    /// <summary>
    /// The reach of a user as stored now (a deleted or disabled user: none). <paramref name="userId"/> null is an entry of the
    /// waiting list kept before device groups existed (every user had all devices then; every newer entry names its user).
    /// </summary>
    public static async Task<AddReach> ForUserAsync(DeviceViewFactory views, Guid? userId, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(views);
        var view = userId is { } id
            ? await views.ForUserAsync(id, ct).ConfigureAwait(false)
            : await views.ForScopeAsync(DeviceScope.All, userName, ct).ConfigureAwait(false);
        return new AddReach(view, userId, userName);
    }

    /// <summary>The server itself (the automatic add of core plugins): everywhere, with the credential list.</summary>
    public static AddReach System(DeviceViewFactory views)
    {
        ArgumentNullException.ThrowIfNull(views);
        return new AddReach(views.System, null, "system");
    }
}

/// <summary>Texts of adding devices for users with a device group.</summary>
public static class AddDeviceTexts
{
    /// <summary>A group of tags or single devices only.</summary>
    public const string CannotAdd = "Your device group has no site, so you cannot add devices.";

    /// <summary>A waiting device whose user can no longer add it there (another group, no devices, removed).</summary>
    public const string KeeperCannotAdd = "Not added: the user who kept it cannot add devices here.";

    /// <summary>A found device that would not be in the user's device group.</summary>
    public const string OutsideGroup = "Not added: the device would not be in your device group.";
}
