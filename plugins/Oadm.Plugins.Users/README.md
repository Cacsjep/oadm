# Oadm.Plugins.Users

Task plugin `oadm.users`: **Users** in the device context menu, group Users, no toolbar button. One dialog
to **Add** a user, **Change** a password or role, or **Remove** users on every selected device (one task
per device). Task names: "Add user joe", "Change password joe" (password only), "Change role joe" (role or
PTZ only), "Change user joe" (both), "Remove user joe", "Remove users joe, ann". Never the password.

| Part | Project | Assembly |
|---|---|---|
| Server (task + read-only query) | `plugins/Oadm.Plugins.Users` | `Oadm.Plugins.Users.Server.dll` |
| Client (Avalonia dialog) | `plugins/Oadm.Plugins.Users.Client` | `Oadm.Plugins.Users.Client.dll` |
| Tests | `tests/Oadm.Plugins.Users.Tests` | |

Both parts deploy to `artifacts/plugins/oadm.users/`. The client references the server project for
the shared payload, roles and validation rules (`UsersContract.cs`, `CredentialRules.cs`,
`UserChangePlanner.ValidatePayload`). The SDKs, Avalonia and CommunityToolkit.Mvvm come from the host
and are never copied.

## VAPIX research

- **API discovery id `user-management` is pwdgrp.cgi** (VAPIX "System settings > User
  management", `/axis-cgi/pwdgrp.cgi`, AXIS OS 5.00+). AXIS P3265-V on AXIS OS 12.11.77 lists it
  as `user-management 1.2`. Actions `add`, `update`, `remove`, `get`; parameters `user`
  (1-14 characters a-z, A-Z, 0-9), `pwd`, `grp` (primary group, `users`), `sgrp` (colon separated
  access groups), `comment`, `strict_pwd`.
  Answers are plain text with HTTP 200 for success **and** for most errors:
  `Created account joe.`, `Modified account joe.`, `Removed account joe.`, `Error: ...`.
- `action=get` returns `admin="..."`, `operator="..."`, `viewer="..."`, `ptz="..."`,
  `digusers="..."` (all accounts). An administrator is listed in admin, operator and viewer.
- The device-config REST API `/config/rest/user-management/v2` (listed only in `/config/discover`,
  not in `apidiscovery.cgi`) has **no user accounts**: only `settings.passphraseComplexity`
  (`policy`, `policies`: none, length, complex). `/config/rest/user-management/v1` does not exist
  on AXIS OS 12.11.
- Roles (as in ADM and the device web UI): Administrator = `admin:operator:viewer`,
  Operator = `operator:viewer`, Viewer = `viewer`; PTZ control appends `:ptz`.
- AXIS OS before 11.5: the first account must be `root` (Administrator with PTZ), cannot be
  deleted and is created only once. Since 11.5 the first administrator may have any name; the only
  rule is "Administrator with PTZ".
- AXIS OS 12: passphrase policy (systemready `passphrasepolicy`: none, length = at least 15
  characters, complex = at least 12 with upper, lower, digit, special). SSH accounts are a separate
  REST API (`/config/rest/ssh/v2/users`, API `ssh`); this plugin does not touch SSH users.
- `usergroup.cgi` returns the name and groups of the authenticated account (`root` /
  `admin operator viewer ptz`); the plugin uses it to know which account OADM itself uses.

## Decision table (per device, decided again in `ExecuteAsync` from a fresh API list)

| Fresh `getApiList` contains | Used API | Result |
|---|---|---|
| `user-management` 1.x (1.0 or later) | `pwdgrp.cgi` (form POST) | runs |
| `user-management` 2.x or later only | none | `DeviceNotCompatibleException`, nothing changed (different major = different API) |
| no `user-management` (or no API list) | none | `DeviceNotCompatibleException`, nothing changed |
| `systemready` 1.x | `systemready.cgi` for the passphrase policy | policy enforced before writing |
| no `systemready` | none | base rule only (1-64 printable ASCII); the device still enforces its own policy |

`CanRun` applies the same `user-management` 1.0 check to the cached `device.Apis`, so the menu entry
is hidden for devices whose API list is unknown or incompatible.

## Per-device steps

1. Validate the payload (user name, password against the base rule, mode). Bad input fails the
   device with "Nothing was changed" before any request.
2. `GetApiListAsync` + `Require("user-management", "1.0")`.
3. Read-only: systemready (policy), usergroup.cgi (OADM account), `pwdgrp.cgi action=get`.
4. Plan (`UserChangePlanner`): password vs. device policy, existence checks, lock-out rules.
5. One write (`add`, `update`), or one `remove` per user. Arguments go in the POST body, never in
   the URL. Remove validates every user first: one protected user fails the device before anything
   is removed.
6. Read back with `action=get`: the user exists, has the role, or is gone.

Task steps shown to the user (one per request, planned up front): **Check compatibility**
("user-management 1.2"), **Read password policy** (Skipped without systemready), **Identify OADM
account**, **Read users** ("4 users"), **Validate change**, the write **Add user joe** / **Update user
joe** / **Remove user joe** (one per user: **Remove user joe**, **Remove user ann**), **Verify users**
(detail: "User joe added as Operator with PTZ.", "Users joe, ann removed."), then the engine's
**Completed**.
A failure marks its step Failed and the later steps Skipped. **Validate change** ends as Warning
(Done with warnings) when the user to add already exists or the user to change or remove does not
exist. The write and Verify users are then Skipped; with several users to remove only the missing ones
are Skipped. Passwords never appear in step names or details, logs, warnings, exception messages or
URLs (tested).

## Lock-out protection

Refused with "Nothing was changed" (device fails, other devices continue):
- removing, demoting, or changing the password of the account OADM is authenticated as
  (from usergroup.cgi, case-insensitive);
- removing or demoting the last administrator;
- removing `root` on AXIS OS older than 11.5 (or unknown firmware);
- changing or removing any user when the OADM account cannot be determined.
Remove payloads: `userNames` (list, at most 50, duplicates dropped; `userName` alone still works).
The dialog shows no general warning text; in Change mode it warns when the user name is the OADM
account of the first selected device. In Remove mode protected rows (the OADM account, the last
administrator) are greyed and cannot be selected; their tooltip says why.

## Dialog

Mode (Add / Change / Remove), user name (Add and Change only), password + confirm with the policy hint,
role (Administrator, Operator, Viewer) and PTZ, the existing users of the first selected device (read-only
query `listUsers`, with a note when several devices are selected), and a summary for N devices. Clicking a
user in Add or Change fills the user name (still editable) and switches Add to Change. In Remove mode the
users are picked in the Existing users list (click, Ctrl/Shift for several; the window forwards the grid
selection to `SetSelectedUsers`). Users missing on other selected devices are skipped there with a
warning. Plain Avalonia controls with the host theme
classes (`primary`, `secondary`, `fieldLabel`, `card`, ...), no colors or font sizes of its own.
Returns the payload JSON; the password lives only in memory.
