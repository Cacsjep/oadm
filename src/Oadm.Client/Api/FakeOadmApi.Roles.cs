using System.Runtime.CompilerServices;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Roles in fake mode: the three built-in roles, a custom "Contractor" role (Web View only) and a permission list like a
/// server with the bundled plugins. Saves and deletes follow the server's rules (Administrator fixed, names unique, roles
/// in use stay).
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string AdministratorRoleId = "00000000-0000-0000-0000-000000000001";
    public const string OperatorRoleId = "00000000-0000-0000-0000-000000000002";
    public const string ViewerRoleId = "00000000-0000-0000-0000-000000000003";
    public const string ContractorRoleId = "7f6d1c3a-5b2e-4c8f-9a01-3e4d5f6a7b8c";

    private readonly List<RoleInfo> _roles = [];
    private List<PermissionInfo>? _permissions;

    public IAsyncEnumerable<UserInfo> WatchSessionAsync(CancellationToken ct) => WatchSessionCoreAsync(ct);

    public Task<RoleMatrix> GetRoleMatrixAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            return Task.FromResult(MatrixLocked());
        }
    }

    public Task<RoleMatrix> SaveRolesAsync(IReadOnlyList<RoleChange> roles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roles);
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            var known = PermissionsLocked().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            foreach (RoleChange change in roles)
            {
                string name = (change.Name ?? "").Trim();
                if (name.Length is 0 or > 64)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, name.Length == 0 ? "Enter a role name." : "The role name must be 1-64 characters without control characters."));
                }

                RoleInfo? role = string.IsNullOrEmpty(change.Id) ? null : _roles.FirstOrDefault(r => r.Id == change.Id)
                    ?? throw new RpcException(new Status(StatusCode.NotFound, "The role does not exist (any more)."));
                if (role?.Kind == RoleKind.Administrator)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "The Administrator role always has every permission and cannot change."));
                }

                if (_roles.Any(r => r != role && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"A role named {name} exists already."));
                }

                if (role is null)
                {
                    role = new RoleInfo { Id = Guid.NewGuid().ToString(), Kind = RoleKind.Custom };
                    _roles.Add(role);
                    AuditLocked("Created role", name, "");
                }
                else
                {
                    AuditLocked("Changed role", name, "");
                }

                role.Name = name;
                role.Description = (change.Description ?? "").Trim();
                role.Permissions.Clear();
                role.Permissions.AddRange(change.Permissions.Where(known.Contains).Distinct().Order(StringComparer.Ordinal));
                foreach (UserInfo user in _users.Where(u => u.RoleId == role.Id))
                {
                    user.RoleName = role.Name;
                }
            }

            return Task.FromResult(MatrixLocked());
        }
    }

    public Task<RoleMatrix> DeleteRoleAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            RoleInfo role = _roles.FirstOrDefault(r => r.Id == id)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "The role does not exist (any more)."));
            if (role.Kind != RoleKind.Custom)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{role.Name} is built in and cannot be deleted."));
            }

            int users = _users.Count(u => u.RoleId == id);
            if (users > 0)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, users == 1
                    ? $"1 user has the role {role.Name}. Give that user another role first."
                    : $"{users} users have the role {role.Name}. Give them another role first."));
            }

            _roles.Remove(role);
            AuditLocked("Deleted role", role.Name, "");
            return Task.FromResult(MatrixLocked());
        }
    }

    public Task<MyRole> GetMyRoleAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            SeedAuthLocked();
            var reply = new MyRole { RoleName = "Administrator", AllPermissions = true };
            reply.Permissions.AddRange(PermissionsLocked().Select(p => p.Clone()));
            return Task.FromResult(reply);
        }
    }

    private async IAsyncEnumerable<UserInfo> WatchSessionCoreAsync([EnumeratorCancellation] CancellationToken ct)
    {
        yield return await GetCurrentUserAsync(ct).ConfigureAwait(false);
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    private RoleMatrix MatrixLocked()
    {
        var matrix = new RoleMatrix();
        matrix.Permissions.AddRange(PermissionsLocked().Select(p => p.Clone()));
        foreach (RoleInfo role in _roles)
        {
            RoleInfo copy = role.Clone();
            copy.UserCount = _users.Count(u => u.RoleId == role.Id);
            matrix.Roles.Add(copy);
        }

        return matrix;
    }

    private void SeedRolesLocked()
    {
        IEnumerable<string> Keys(Func<PermissionInfo, bool> pick) => PermissionsLocked().Where(pick).Select(p => p.Key);
        _roles.Add(new RoleInfo { Id = AdministratorRoleId, Name = "Administrator", Description = "Everything, including what plugins add later.", Kind = RoleKind.Administrator });
        _roles.Add(new RoleInfo { Id = OperatorRoleId, Name = "Operator", Description = "Works with devices and tasks.", Kind = RoleKind.Operator, Permissions = { Keys(p => p.OperatorDefault) } });
        _roles.Add(new RoleInfo { Id = ViewerRoleId, Name = "Viewer", Description = "Sees devices, live view and the monitoring pages.", Kind = RoleKind.Viewer, Permissions = { Keys(p => p.ViewerDefault) } });
        _roles.Add(new RoleInfo { Id = ContractorRoleId, Name = "Contractor", Description = "Web View on site.", Kind = RoleKind.Custom, Permissions = { "devices.liveView", "oadm.web-view:show", "oadm.web-view:open" } });
    }

    /// <summary>The matrix rows of a server with the bundled plugins (same keys, names and defaults).</summary>
    private List<PermissionInfo> PermissionsLocked() => _permissions ??=
    [
        P("devices.addRemove", "Add and remove devices", "Find devices, add them and remove them from OADM.", "Devices", op: true),
        P("devices.refresh", "Refresh devices", "Read the current state of the devices again.", "Devices", op: true),
        P("devices.login", "Log in to devices", "Log in with another user name and password, and set the first password of new devices.", "Devices", op: true, full: true),
        P("devices.webInterface", "Open the web interface", "Open a device's web interface in the browser.", "Devices", op: true),
        P("devices.liveView", "Live view", "Click a camera in the device list to see its video.", "Devices", op: true, viewer: true),
        P("devices.export", "Export the device list", "Save the device list as a CSV file.", "Devices", op: true),
        P("tags.assign", "Tag devices", "Give devices tags and create new tags.", "Devices", op: true),
        P("tags.manage", "Rename and delete tags", "Rename, recolor and delete tags on every device.", "Devices"),
        P("tasks.cancel", "Cancel tasks", "Cancel running and waiting tasks of every user.", "Tasks", op: true),
        P("tasks.delete", "Delete tasks", "Delete single tasks from the task list.", "Tasks", op: true),
        P("tasks.deleteAll", "Delete all tasks", "Delete the whole task list.", "Tasks"),
        P("task:oadm.acap:install", "Install and upgrade applications", "Install applications on devices or upgrade them.", "Tasks", op: true, icon: "app"),
        P("task:oadm.acap:downgrade", "Downgrade applications", "Install an older version of an application.", "Tasks", icon: "app"),
        P("task:oadm.acap:startStop", "Start and stop applications", "Start and stop applications that are installed.", "Tasks", op: true, icon: "app"),
        P("task:oadm.acap:remove", "Remove applications", "Delete applications from devices; their settings are lost.", "Tasks", icon: "app"),
        P("task:oadm.datetime", "Date and time", "Run the task Date and time on devices.", "Tasks", op: true, icon: "clock"),
        P("task:oadm.device-config.import:import", "Import configuration", "Import settings from a configuration file, without the connection settings.", "Tasks", op: true),
        P("task:oadm.device-config.import:connection", "Import connection settings", "Also import HTTPS, ports, login method and IEEE 802.1X; devices can become unreachable.", "Tasks"),
        P("task:oadm.device-config.configure:configure", "Configure devices", "Set parameters, action rules and stream profiles on many devices.", "Tasks", op: true, icon: "sliders"),
        P("task:oadm.device-config.configure:dangerous", "Configure dangerous parameters", "Also set addresses, host names, HTTPS, ports, the login method and IEEE 802.1X; devices can become unreachable.", "Tasks", icon: "sliders"),
        P("task:oadm.device-config.create", "Create configuration file", "Run the task Create configuration file on devices.", "Tasks", op: true, icon: "save"),
        P("task:oadm.network", "Network settings", "Run the task Network settings on devices.", "Tasks", op: true, icon: "network"),
        P("task:oadm.restart", "Restart", "Run the task Restart on devices.", "Tasks", op: true, icon: "refresh"),
        P("task:oadm.firmware:upgrade", "Upgrade firmware", "Install new firmware on devices; their settings stay.", "Tasks", op: true, icon: "firmware"),
        P("task:oadm.firmware:factoryDefault", "Upgrade with factory default", "Reset the devices to factory default together with the upgrade.", "Tasks", icon: "firmware"),
        P("task:oadm.firmware:downgrade", "Downgrade firmware", "Install older firmware; this always resets the devices to factory default.", "Tasks", icon: "firmware"),
        P("task:oadm.users:add", "Add users on devices", "Create accounts on the devices.", "Tasks", op: true, full: true, icon: "users"),
        P("task:oadm.users:update", "Change users on devices", "Change the password and role of accounts on the devices.", "Tasks", op: true, full: true, icon: "users"),
        P("task:oadm.users:remove", "Remove users on devices", "Delete accounts from the devices.", "Tasks", full: true, icon: "users"),
        P("events.view", "See event rules", "See the event rules and the event log.", "Event rules", op: true),
        P("events.manage", "Change event rules", "Create, change, turn on or off and delete event rules. Rules act on devices on their own.", "Event rules", full: true),
        P("events.clearLog", "Delete the event log", "Delete every entry of the event log.", "Event rules"),
        P("oadm.ntp-server:show", "Show the page", "See the NTP server page and what it shows.", "NTP server", op: true, icon: "metronome"),
        P("oadm.ntp-server:manage", "Change the NTP server", "Turn the NTP server on or off and choose its network and time source.", "NTP server", icon: "metronome"),
        P("oadm.dhcp-server:show", "Show the page", "See the DHCP server page and what it shows.", "DHCP server", op: true, icon: "network"),
        P("oadm.dhcp-server:manage", "Change the DHCP server", "Turn the DHCP server on or off, change its addresses and static leases, and release leases.", "DHCP server", icon: "network"),
        P("oadm.vapix-commander:show", "Show the page", "See the VAPIX Commander page and what it shows.", "VAPIX Commander", op: true, icon: "command"),
        P("oadm.vapix-commander:send", "Send to one device", "Send a command or a request of your own to one device and see its answer.", "VAPIX Commander", op: true, full: true, icon: "command"),
        P("oadm.vapix-commander:rollout", "Run on many devices", "Run the commands of the rollout set on the chosen devices.", "VAPIX Commander", op: true, full: true, icon: "command"),
        P("oadm.vapix-commander:saved", "Change saved commands", "Save, change, delete and import the saved commands everyone shares.", "VAPIX Commander", op: true, icon: "command"),
        P("oadm.pki:show", "Show the page", "See the PKI page and what it shows.", "PKI", op: true, icon: "shield"),
        P("oadm.pki:manage", "Manage the certificate authority", "Create, import, back up and export the certificate authority and change its settings.", "PKI", icon: "shield"),
        P("oadm.pki:trustServer", "Trust it on the server", "Install the certificate authority in the trusted root store of the server computer.", "PKI", icon: "shield"),
        P("oadm.metadata-monitor:show", "Show the page", "See the Metadata Monitor page and what it shows.", "Metadata Monitor", op: true, viewer: true, icon: "activity"),
        P("oadm.snapshot-report:show", "Show the page", "See the Snapshot report page and what it shows.", "Snapshot report", op: true, viewer: true, icon: "snapshot"),
        P("oadm.snapshot-report:report", "Create snapshots and reports", "Take a current picture of every camera and export the PDF report.", "Snapshot report", op: true, viewer: true, icon: "snapshot"),
        P("oadm.system-report:download", "Download system reports", "Download the system reports of the selected devices as one ZIP file for Axis support.", "System report", op: true, icon: "report"),
        P("oadm.web-view:show", "Show the page", "See the Web View page and what it shows.", "Web View", icon: "globe"),
        P("oadm.web-view:open", "Open devices", "Open the web interface of devices in tabs, logged in with the stored login.", "Web View", full: true, icon: "globe"),
    ];

    private static PermissionInfo P(string key, string name, string description, string group, bool op = false, bool viewer = false, bool full = false, string icon = "") => new()
    {
        Key = key,
        Name = name,
        Description = description,
        Group = group,
        IconKey = icon,
        OperatorDefault = op,
        ViewerDefault = viewer,
        FullDeviceControl = full,
        Available = true,
    };
}
