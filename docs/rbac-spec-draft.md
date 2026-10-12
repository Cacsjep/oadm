# Roles and permissions (RBAC): spec

Status: built (2026-10-12). CLAUDE.md ("Security and access", Roles and permissions) is the source of truth; this file
keeps the reasoning and the decisions. Source: `fix.md`.

Differences to the draft found while building:

- A plugin may declare "show" to change the defaults of its page. Web View does: its page is off for Operator too (the
  page is of no use without "open").
- The Viewer also has Snapshot report "report" (the page shows nothing without taking snapshots; it only reads).
- The automatic "manage" of a core plugin without declarations exists only when the plugin answers `RequiredRole`
  itself (read-only pages such as Image Health get no empty "Change settings" row).
- Listing the credential list (`SettingsService.ListCredentials`) became Administrator only; it was open to every login.
- The "What the code does today" section describes the code before roles.

## Why

OADM has two fixed roles. Administrators do everything, operators do a fixed set. That was enough while OADM was a
technician's tool. With plugins it becomes a tool for several levels of people: a guard who only watches Image Health,
a contractor who only opens Web View, a technician who may restart and upgrade but not change users on the devices.

Problems today:

1. An administrator cannot see what an operator may do. The rules exist only in code and in CLAUDE.md.
2. An administrator cannot limit a user to a few things, for example only the Web View page.
3. Plugins can only say "this method is Admin" (`ICorePlugin.RequiredRole`). They cannot declare their own named
   permissions ("Send VAPIX commands", "Change DHCP leases").
4. A plugin page has no "may see this page" rule. Every user sees every page that is turned on.

## What the code does today

| Check | Where | Notes |
|---|---|---|
| gRPC method table: Open / Operator / Admin | `Oadm.Server.Auth.AccessPolicy`, `AuthInterceptor` | Unlisted methods are Operator. `UserService`, `AuditService` Admin as a whole. |
| Plugin page methods | `PluginGrpcService.Invoke` + `ICorePlugin.RequiredRole(method)` | DIM Operator; a throwing plugin counts as Admin. |
| Save to credential list after "Log in" | `DeviceGrpcService.LogIn` | Inline `IsAdmin` check. |
| Client visibility | `UserSession.IsAdmin` | Hides Users, Credentials, Plugins, the Audit tab; read-only Settings, Event rules, tag admin actions. |

Findings that matter for the spec:

- **`TaskService.Run` and `TaskService.Query` have no check.** Every logged-in user can run every task plugin:
  Users (accounts on the devices), Upgrade firmware, Network settings, Assign IP address, Import configuration, the PKI
  tasks. CLAUDE.md lists "tasks (run ...)" for operators, so this is intended today, but it is the biggest lever for a
  limited role.
- **`PluginService.Watch` has no check.** Any user gets every plugin's live events (DHCP leases, NTP requests, metadata
  messages) while the plugin is on.
- **Web View is full control of the device.** The relay logs in with the stored login (usually root). Whoever may open
  Web View may change anything on the device, including its users. The same is true for "Open web interface" when the
  browser saves the login, but there the user types the password.
- `Cancel` / `Delete` of tasks work on any task, not only the user's own.
- The role travels in the session (`AuthTokenStore`); `UserService.Update` ends the sessions of a changed user, so a
  role change applies at the next login.

## Goals

- An administrator creates roles and picks their permissions from a readable, grouped list.
- Every user has one role. The role decides what the server allows and what the client shows.
- Plugins declare named permissions; every plugin page gets a "show" permission automatically.
- Today's behaviour stays the default: existing users keep exactly what they can do now after the upgrade.
- The server enforces every permission. The client only hides what the user cannot use.

Not in this draft (later, but the model must allow it): limiting a role to some devices (by tag), several roles per
user, permissions per event rule.

## Model

### Roles

- Table `Roles`: Id, Name (1..64, unique case-insensitive), Description (optional, 200), BuiltIn (bool), Permissions
  (JSON array of permission keys), CreatedUtc, ChangedUtc. `Users.Role` becomes `Users.RoleId`.
- Built-in roles:
  - **Administrator**: everything, including permissions that appear later. Not editable, not deletable.
  - **Operator**: today's operator set (table below). Editable, with "Reset to default".
  - **Viewer**: `devices.view`, `devices.liveView` and the `show` of the monitoring pages (Image Health Dashboard,
    Metadata Monitor, I/O Monitor, Snapshot report). Runs no tasks; no Web View, event rules or server pages (NTP,
    DHCP, PKI). Editable, with "Reset to default".
- Custom roles: created empty or as a copy of another role; at most 50.
- The last enabled user with the Administrator role cannot lose it (as today).
- A role in use cannot be deleted; the dialog names how many users have it.

### Permissions

A permission is a key, a name, a group and one sentence for the role editor. Keys:

- Host: `devices.addRemove`, `tasks.cancel` (dotted, fixed by OADM).
- Plugins: `<plugin id>:<key>`, for example `oadm.dhcp-server:manage`. The plugin id is the namespace, so
  third-party plugins never collide.
- Automatic per plugin page: `<plugin id>:show` (see the page, read its state, watch its events).
- Automatic per task plugin: `task:<task plugin id>` (run the task and its dialog queries).

**Administrator only, never grantable.** These stay with the Administrator role, so no custom role can lift itself to
administrator:

- users and roles;
- the credential list (add, remove, reveal);
- turning plugins on or off;
- server settings, the audit log.

The audit log and read-only settings could become grantable later.

New permissions (a plugin installed or updated later): Administrator has them at once; Operator gets the ones the
plugin marks as Operator default; Viewer gets only the `show` of a new monitoring page; custom roles get nothing until
an administrator ticks them. A limited role never gains a right by itself.

Stored roles keep keys they do not know (a plugin that was removed or renamed a permission): ignored while unknown,
active again when the plugin comes back.

### Permission list we can deliver now

Grouped like the client. Column "Operator" = the default of the built-in Operator role: today's operator rights,
without Web View and the risky task choices (decisions 9 and 11).

**Devices**

| Key | Name | Covers | Operator |
|---|---|---|---|
| `devices.view` | See devices | Device list, status, tags, the tasks pane. Every role has it (without it the client is empty). | Yes |
| `devices.addRemove` | Add and remove devices | Discovery, add page, import, Remove. | Yes |
| `devices.refresh` | Refresh devices | Refresh. | Yes |
| `devices.login` | Log in to devices | Log in, Set password, device credentials. | Yes |
| `devices.webInterface` | Open the web interface | The address link and "Open web interface". | Yes |
| `devices.liveView` | Live view | The video panel. | Yes |
| `devices.export` | Export the device list | CSV export (client side only: the data is already on the client). | Yes |
| `tags.assign` | Tag devices | Tags dialog, create tags. | Yes |
| `tags.manage` | Rename and delete tags | Rename, recolor, delete tag definitions. | No |

**Tasks**

| Key | Name | Covers | Operator |
|---|---|---|---|
| `tasks.cancel` | Cancel tasks | Cancel any task. | Yes |
| `tasks.delete` | Delete tasks | Delete single tasks. | Yes |
| `tasks.deleteAll` | Delete all tasks | Delete all. | No |
| `task:oadm.restart` | Restart | | Yes |
| `task:oadm.firmware:upgrade` | Upgrade firmware | Upgrade, keep settings. | Yes |
| `task:oadm.firmware:factoryDefault` | Upgrade with factory default | Soft or hard factory default with the upgrade. | No |
| `task:oadm.firmware:downgrade` | Downgrade firmware | "Allow downgrade" (always with a factory default). | No |
| `task:oadm.datetime` | Date and time | | Yes |
| `task:oadm.users:add` | Add users on devices | | Yes |
| `task:oadm.users:update` | Change users on devices | Password and role of existing accounts. | Yes |
| `task:oadm.users:remove` | Remove users on devices | | No |
| `task:oadm.network` | Network settings | | Yes |
| `task:oadm.network.assign-ip` | Assign IP address | | Yes |
| `task:oadm.acap:install` | Install and upgrade applications | | Yes |
| `task:oadm.acap:downgrade` | Downgrade applications | "Allow downgrade". | No |
| `task:oadm.acap:startStop` | Start and stop applications | | Yes |
| `task:oadm.acap:remove` | Remove applications | | No |
| `task:<device config import id>` | Import configuration | | Yes |
| `task:<device config import id>:connection` | Import connection settings | The "Connection settings" area (HTTPS, ports, login method, 802.1X). | No |
| `task:<pki task ids>` | Enable / Disable HTTPS, IEEE 802.1X, certificates | One per PKI task. | Yes |
| `task:oadm.ntp-server.use` | Use OADM as NTP server | | Yes |

Export configuration is a dialog that reads only; it needs `task:<device config export id>`.
The task ids in angle brackets are filled from the registry when this is built. The sub-permissions above are the
first proposal per plugin; each plugin README fixes its own list.

**Event rules**

| Key | Name | Covers | Operator |
|---|---|---|---|
| `events.view` | See event rules | Rules and the event log. | Yes |
| `events.manage` | Change event rules | New, edit, duplicate, delete, on / off, Send test. | No |
| `events.clearLog` | Delete the event log | Delete all. | No |

**Plugins** (each page also gets `<id>:show`, Operator Yes)

| Plugin | Declared keys | Covers | Operator |
|---|---|---|---|
| NTP server | `manage` | Save. | No |
| DHCP server | `manage` | Save, static leases, release. | No |
| VAPIX Commander | `send` | Send to one device. | Yes |
| | `rollout` | Run on many devices. | Yes |
| | `saved` | Save, delete, import saved commands. | Yes |
| Device triggers | `manage` | New, edit, delete, on / off, duplicate. | No |
| PKI | `manage` | Generate, import, back up, export, settings. | No |
| | `trustServer` | Install in the server's trusted root store. | No |
| Hardening scan | `scan` | Start and cancel a scan. | Yes |
| Snapshot report | `report` | Create snapshots and the PDF. | Yes |
| Image Health Dashboard | (show only) | | Yes |
| Metadata Monitor | (show only) | | Yes |
| I/O Monitor | (show only) | | Yes |
| System report | `download` | The toolbar button (no page: no `show`). | Yes |
| Web View | `open` | Open devices in tabs. Full control of the device, see "Concerns". | No |

Event rule actions run as the server ("system"), not as a user. Whoever has `events.manage` decides what they do.

## Plugin SDK

New default interface members, so every existing plugin keeps loading:

```csharp
public sealed record PermissionDefinition(string Key, string Name, string Description, bool OperatorDefault);

public interface ICorePlugin
{
    // Declared permissions of the plugin, without ":show" (automatic for a plugin with a page).
    IReadOnlyList<PermissionDefinition> Permissions => [];

    // The permission a page method needs: a declared key, "show", or null = fall back to RequiredRole.
    string? RequiredPermission(string method) => null;
}
```

- A plugin that only implements `RequiredRole` (old or third-party) is mapped. Operator methods need `show`. Admin
  methods need an automatic `manage` that is not in the Operator default.
- `IsAudited` stays as it is.
- Task plugins: every task plugin gets `task:<id>`. A task plugin with choices that differ in risk declares
  sub-permissions and says which ones a run needs, from its payload:

  ```csharp
  public interface ITaskPlugin
  {
      // Finer rights inside this task ("add", "remove"), as task:<id>:<key>. Empty: task:<id> alone.
      IReadOnlyList<PermissionDefinition> Permissions => [];

      // The keys one run needs, from its payload (validated first). Null or empty: task:<id> alone.
      IReadOnlyList<string>? RequiredPermissions(string? payloadJson) => null;
  }
  ```

  A plugin with sub-permissions has no plain `task:<id>` key: the menu entry shows when the role has at least one of
  its keys. `Run` checks every key the payload needs before any task is created; a missing one refuses the whole run
  ("Your role does not allow: Remove users on devices."). The dialog reads the keys
  (`ITaskDialogContext.Has(key)`) and hides or disables the choices the role lacks, for example Remove in the Users
  dialog or "Allow downgrade" in the firmware dialog.
- `Query` (the dialog's reads) needs any key of the task.
- The client part reads the user's permissions through `ICorePluginClientContext.Has(key)`, so a page can hide a Save
  button. The server still checks every call.

## Enforcement

Server:

- **Gate:** `AccessPolicy` maps each gRPC method to a permission key instead of Open / Operator / Admin. The
  interceptor checks the caller's set; PERMISSION_DENIED says what is missing ("Your role does not allow: Change event
  rules.").
- **Inside calls:** `PluginService.Invoke` checks `RequiredPermission`. `Watch` checks `show`. `TaskService.Run` and
  `Query` check `task:<id>`, which closes today's gap.
- **Effective set:** the caller's permissions are a `HashSet` per role, cached per role version; an O(1) check per
  call.
- **Live changes:** a changed role or user role applies to the next call at once, no new login needed. A new
  `AuthService.WatchSession` stream tells the client to reload its permissions. The open plugin pages of a role that
  lost `show` close.
- **Audit:** "Created role", "Changed role" with "+Web View: open -Restart", "Deleted role", "Changed user role".

Client:

- `Login` / `Me` return the role name and the permission keys.
- The rail shows a plugin page only with `<id>:show`. Context menu entries and toolbar buttons only with their
  `task:<id>` or key; entries the user cannot run are hidden, not greyed (greyed means "this device cannot").
- Host actions follow their keys instead of `IsAdmin`.

## User interface

- **Roles card** on the **Users page** (Administrator only, like the page): the users card on top, the **Roles** card
  below it with a splitter between them (like the Devices page and its tasks), the split persisted per client. The
  Roles card holds a **permission matrix** (reference screenshot shared 2026-10-12): permissions as rows, roles as
  columns. Page subtitle: "Who can log in to this server and what each role may do."
  - Toolbar: **New role** (primary; empty, or "Copy of" a chosen role), search over the permission names, **Save** and
    **Discard** once something changed (changes are collected and saved together: one click in a matrix should not
    change rights at once).
  - Rows grouped like the rest of the client, with the theme's collapsible groups (`DataGrid.groups`, as on the
    Plugins page): Devices, Tasks, Event rules, then one group per plugin in rail order with the plugin's icon. A row:
    the permission name (its sentence as the tooltip), right after it an amber warning icon with a tooltip on
    permissions that give full device control (Web View, Users tasks, Log in to devices), then one check box per role.
    New role, Copy and Rename open a dialog (Name, Description, Start from).
  - Group header: per role a three-state check box (all / some / none of the group), a click sets the whole group.
  - Columns: Administrator first (every box checked and disabled, "Always everything"), then Operator, Viewer, then the
    custom roles in creation order. Each header: the role name, "3 users" below it, a menu with Rename, Copy, Reset to
    default (built-in roles) and Delete (custom roles without users; red, confirmed).
  - Scale: about 60 permissions with today's plugins, up to 50 roles: a virtualized `DataGrid`, the columns scroll
    sideways with the name column frozen.
  - Saving shows one confirmation that lists what changes per role ("Operator: -Remove users on devices, +Web View
    open") and is audited the same way.
- **Users card:** **+ User** above the list; the row's buttons Edit user (the role), Change password, Disable / Enable,
  Delete. Add user, Edit user and Change password are dialogs; the role select lists every role.
- **"What can this role do"** (problem 1): the matrix shows every role side by side for administrators. Every user
  clicks their entry at the bottom of the rail (initial avatar and name) to open a small read-only window: the role
  name and what it allows, grouped like the matrix (`ui:DialogTitleBar`, one list, Close).

## Upgrade

- Migration: `Roles` with Administrator, Operator and Viewer, `Users.RoleId` from the old enum. Operator gets its
  default set. **Operators lose some rights on upgrade** (decisions 9 and 11): Web View, Remove users on devices,
  firmware downgrade and factory default, application downgrade and removal, importing connection settings. The release notes
  say so and point administrators to the roles on the Users page; the client shows a one-time notice to administrators after the
  upgrade ("Operators no longer have: ..." with a link to the Users page).
- An older client against a newer server: the server still answers `UserInfo.role` (Admin for Administrator, Operator
  for every other role), so the client shows the same pages as before, and the server refuses what the role lacks.

## Concerns

1. **Web View and stored logins give full device control.** A role with Web View or the Users task can do anything
   on the device, whatever OADM's permissions say. The role editor must say so. Later: a per-device "Web View login"
   with a viewer account.
2. **The client is not a security boundary.** Hiding is for clarity. Every new check goes on the server, and every
   permission gets a test per role like `AuthTests` today.
3. **Plugins run inside the server.** A permission limits which calls a user may make; it does not limit what a
   plugin's code can do. Third-party plugins stay trusted code.
4. **Device scope** ("this role only sees Building A") touches every device list, stream, task and plugin. It is out
   of this draft. Keys and the role table leave room for a `Scope` column later.
5. **Count of permissions.** With many plugins the list grows. Grouping, "all" per group and copying roles keep it
   usable; per-method permissions are not exposed, plugins declare only what an administrator understands.
6. **Event rules run as the server**, so `events.manage` is close to Administrator in power. It stays not
   granted to Operator by default.

7. **Open streams outlive a check.** Streams (plugin events, live view, the device and task feeds, Web View relays)
   are checked when they start. A role change must end the streams the role no longer allows, not only refuse the
   next call; otherwise a user keeps watching a page after losing it.

## Decisions (2026-10-12)

1. One role per user.
2. The built-in Operator is editable, with "Reset to default".
3. A built-in Viewer role ships in the first version (editable like Operator).
4. Task permissions are finer from the start: task plugins declare sub-permissions, a run needs the ones its payload
   uses (see "Plugin SDK").
5. "Cancel tasks" and "Delete tasks" are one permission each and work on every task.
6. Users, roles, the credential list, plugins on / off, server settings and the audit log stay Administrator only.
7. No device scope in the first version; the role table keeps room for it.
8. New permissions of a plugin: Administrator at once, Operator by the plugin's default, Viewer only a monitoring
   page's `show`, custom roles nothing until granted.
9. Web View is off in the Operator default.
10. Viewer: devices, live view and the monitoring pages.
11. The upgrade tightens Operator: the risky task choices are off (remove users, firmware downgrade and factory
    default, application downgrade and removal, connection settings import).
12. Roles are a permission matrix (roles as columns) in a second card on the Users page.
13. The task sub-permissions are the proposed list (Firmware, Users, Applications, Import configuration split; the
    other tasks one permission each). Removing applications is off for Operator too.
14. Every user sees their own rights in a window opened from their entry in the rail.

## Open questions

- None. Each plugin README fixes its final permission names when it is built.
