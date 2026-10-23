using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode as another user (<see cref="CurrentUserName"/>; the client's <c>--fake-user tech2</c> or <c>OADM_FAKE_USER</c>):
/// the user's role and devices like the server's. A user with a device group ("tech2": Building B, "guard": Lobby) gets only
/// the group's devices in the device list and on the Sites page, Your role names the group, tags a group uses are refused
/// when tagging, and a group without a site cannot add devices. The administrator "admin" (the default) has everything.
/// </summary>
public sealed partial class FakeOadmApi
{
    private string _currentUserName = FakeUserName;

    /// <summary>The user fake mode runs as (one of the sample users; default "admin").</summary>
    public string CurrentUserName
    {
        get
        {
            lock (_gate)
            {
                return _currentUserName;
            }
        }
        set
        {
            lock (_gate)
            {
                SeedAuthLocked();
                UserInfo user = _users.Find(u => string.Equals(u.UserName, value, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Fake mode has no user {value}. Users: {string.Join(", ", _users.Select(u => u.UserName))}.", nameof(value));
                _currentUserName = user.UserName;
            }
        }
    }

    /// <summary>The user fake mode runs as, with the permissions of the role (the client signs in with it).</summary>
    public UserInfo CurrentUser()
    {
        lock (_gate)
        {
            SeedAuthLocked();
            return CurrentUserInfoLocked();
        }
    }

    private bool IsAdminUserLocked => string.Equals(_currentUserName, FakeUserName, StringComparison.Ordinal);

    private UserInfo CurrentUserInfoLocked()
    {
        UserInfo me = _users.First(u => u.UserName == _currentUserName).Clone();
        RoleInfo? role = _roles.Find(r => r.Id == me.RoleId);
        me.AllPermissions = role?.Kind == RoleKind.Administrator;
        if (!me.AllPermissions && role is not null)
        {
            me.Permissions.AddRange(role.Permissions);
        }

        me.StartPage = role?.StartPage ?? "";
        return me;
    }

    /// <summary>Your role of the current user: the role's permissions and devices.</summary>
    private MyRole MyRoleLocked()
    {
        UserInfo me = CurrentUserInfoLocked();
        var reply = new MyRole { RoleName = me.RoleName, AllPermissions = me.AllPermissions, Devices = MyDevicesLocked(_users.First(u => u.UserName == _currentUserName)) };
        var keys = me.Permissions.ToHashSet(StringComparer.Ordinal);
        reply.Permissions.AddRange(PermissionsLocked().Where(p => me.AllPermissions || keys.Contains(p.Key)).Select(p => p.Clone()));
        return reply;
    }

    /// <summary>
    /// The rules of the current user's devices: null = all devices (the administrator "admin" needs no seeding: tests that add
    /// devices later still get the sample groups), empty rules = no devices (the group was deleted).
    /// </summary>
    private DeviceGroupRules? ViewRulesLocked()
    {
        if (IsAdminUserLocked)
        {
            return null;
        }

        SeedAuthLocked();
        UserInfo user = _users.First(u => u.UserName == _currentUserName);
        return user.AllDevices || user.Role == UserRole.Admin ? null : _groups.Find(g => g.Id == user.DeviceGroupId)?.Rules ?? new DeviceGroupRules();
    }

    /// <summary>The device is one of the current user's.</summary>
    private bool InViewLocked(Device device) => ViewRulesLocked() is not { } rules || InGroup(rules, device);

    /// <summary>Like the server: a group without "This server" and without a site cannot add devices.</summary>
    private void ThrowIfCannotAddLocked()
    {
        if (ViewRulesLocked() is { } rules && !rules.ThisServer && rules.SiteIds.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, Oadm.Sdk.Devices.DeviceGroupMessages.CannotAdd));
        }
    }

    /// <summary>Like the server's tag guard: a user with a device group never adds or removes a tag a group uses.</summary>
    private void ThrowIfGroupTagLocked(IEnumerable<string> names)
    {
        if (ViewRulesLocked() is null)
        {
            return;
        }

        foreach (string name in names)
        {
            if (FindTagLocked(name) is { } tag && _groups.Exists(g => g.Rules.TagIds.Contains(tag.Id)))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    $"A device group uses the tag {tag.Name}. Only users with all devices can add or remove it. Nothing was changed."));
            }
        }
    }

    /// <summary>The sample groups, seeded once the sample devices exist (tests that add their devices later still get them).</summary>
    private List<DeviceGroupInfo> SeededGroupsLocked()
    {
        if (_devices.Count > 0)
        {
            SeedGroupsLocked();
        }

        return _groups;
    }

    /// <summary>The device groups whose rules use a tag (users with all devices only, like the server).</summary>
    private IEnumerable<string> GroupsUsingTagLocked(DeviceTag tag) =>
        ViewRulesLocked() is not null ? [] : SeededGroupsLocked().Where(g => g.Rules.TagIds.Contains(tag.Id)).Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>The device groups whose rules use a site (users with all devices only, like the server).</summary>
    private IEnumerable<string> GroupsUsingSiteLocked(string siteId) =>
        ViewRulesLocked() is not null ? [] : SeededGroupsLocked().Where(g => g.Rules.SiteIds.Contains(siteId)).Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase);
}
