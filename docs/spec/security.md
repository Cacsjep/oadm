# Security and access

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

## Secrets

- Device passwords: AES-256-GCM, key in `<datafolder>/master.key` (0600 on Unix), random nonce per record, stored as
  `nonce|ciphertext|tag` (no OS keyring). The credential list uses the same, with the entry id as associated data.
- Device credentials never leave the server; gRPC returns only "has credentials" and, for the prefill of the "Log in"
  dialog, the stored user name the selected devices share. The credential list returns ids and user names only; the one
  exception is the explicit reveal of an entry (`SettingsService.RevealCredential`, eye and copy buttons of the Credentials
  page): Admin only, every reveal in the audit log, refused while no user exists. Credentials typed for a login (RetryAuth,
  the context menu's "Log in") or a first password travel only client -> server.
- Never log passwords or digest headers.

## TLS

- On first start the server creates a self-signed certificate (ECDSA P-256, CN = server name, SAN = host name, every
  interface address and localhost, 20 years) and keeps it in `server-tls.json` (certificate PEM + private key encrypted
  with the master key, purpose `oadm:server-tls`). On Windows the key is instead one persisted named CNG key "OADM Server
  TLS <hash of the data folder>" (machine key store as a service, else the user store; not exportable; opened by name on
  every start, overwritten on regenerate) and the file holds its name. A key that cannot be read (e.g. after a master key
  replacement) creates a new certificate and logs that clients must use Forget server. `--Oadm:RegenerateTlsCertificate`
  makes a new one.
- Kestrel serves HTTP/2 over TLS on the listen URL (default `https://0.0.0.0:5080`; an `http://` URL is accepted for tests
  and logs a warning; a stored listen URL equal to the old default `http://0.0.0.0:5080` reads as the https default). The
  server binds to all interfaces so clients on other machines can connect.
- The client pins the certificate's SHA-256 per "host:port" on first connect (`Oadm.Contracts.Security.ServerCertificatePinning`,
  `client-settings.json` `PinnedServers`; `OadmChannel` creates the pinned channel and adds the token). The fingerprint
  is confirmed in the shared confirmation window ("Trust this server?", two lines of 16 pairs); a changed certificate is
  refused with "The server certificate changed ..." and **Forget server** re-pins. An address without scheme means https.

## Users, roles and sessions

Code: server `Oadm.Server.Auth` (`AuthInterceptor`, `AccessPolicy`, `CallerStreams`, `AuthGrpcService`, `UserGrpcService`,
`RoleGrpcService`, `AuditGrpcService`, `ServerTlsCertificate`, `AuthMaintenanceHostedService`,
`InProcessAccess.CreateTokenAsync` for in-process hosts), core `Oadm.Core.Auth` (`UserStore`, `RoleStore`,
`PermissionCatalog` + `HostPermissions`, `RolesUpgradeNotice`, `PasswordHasher`, `AuthTokenStore`, `LoginThrottle`,
`AuthManager`: login, first administrator, setup code; `AuditLog`; `CallerContext`), migrations `UsersAndAudit` (Users,
AuthTokens, AuditEntries) and `Roles`; client `Shell/AppShell`, `LoginViewModel`, `UserSession`, `MyRoleWindow`,
`Settings/UsersCardView` + `RolesCardView`, `Logging/AuditLogViewModel`.

- Table `Users`: Id, UserName (unique case-insensitive, 1..64), PasswordHash (PBKDF2-SHA256, 210,000 iterations, 16-byte
  salt), RoleId (table `Roles`), Disabled, CreatedUtc, LastLoginUtc, AllDevices + DeviceGroupId (the devices, migration
  `DeviceGroups`, see "Device groups" below). Passwords have no strength rule, only not empty.
- **First administrator**: while no user exists, the login window offers "Create the first administrator"; the server
  accepts it from a loopback client, or from a remote client with the one-time setup code the server writes to
  `<datafolder>/setup-code.txt` (admin-only file) and logs at startup.
- **Tokens**: `AuthService.Login(user, password, remember)` returns an opaque random token (32 bytes, base64url); the
  server stores only its SHA-256 with user, client address and expiry (8 h sliding, 30 days with "Remember me"; table
  `AuthTokens`, the sliding expiry is cached and written at most once a minute). Clients send `authorization: Bearer
  <token>` in the gRPC metadata. `AuthInterceptor` answers UNAUTHENTICATED without a valid token (open: `AuthService.Status`,
  `Login`, `CreateFirstAdmin`) and PERMISSION_DENIED with what the role lacks ("Your role does not allow: Delete tasks.",
  `AccessPolicy.DeniedMessage`); it sets `CallerContext` (user, role, the role's `PermissionSet`), so a task's Owner is
  "<user>@<client machine>". `Logout` revokes. Failed logins: 5 per user in 5 minutes, then 5 minutes locked, logged.
  `AuthMaintenanceHostedService` removes expired sessions and old audit entries hourly. A new password or a disabled user
  ends the sessions; a new role applies to the next call (`AuthTokenStore.SetRole`).
- **Roles and permissions.** Every user has one role (table `Roles`: Id, Name 1..64 unique case-insensitive,
  Description 0..200, Kind Administrator / Operator / Viewer / Custom, PermissionsJson = the granted keys, KnownJson = every
  key the role has seen, CreatedUtc, ChangedUtc; at most 50 custom roles). Built-in roles with fixed ids
  (`BuiltInRoles`): **Administrator** (every permission, also later ones; not editable, not deletable), **Operator**
  (editable, "Reset to default") and **Viewer** (devices, live view and the monitoring pages; editable). The migration
  `Roles` maps the old Admin to Administrator and everyone else to Operator. A permission is a key: host keys
  (`HostPermissionKeys` in the SDK: `devices.addRemove`, `devices.refresh`, `devices.login`, `devices.webInterface`,
  `devices.liveView`, `devices.opticsControl` ("Optics Control": zoom, focus and the RTSP port), `devices.export`, `tags.assign`, `tags.manage`, `tasks.cancel`, `tasks.delete`, `tasks.deleteAll`,
  `compliance.view` ("See compliance", Operator and Viewer yes), `compliance.manage` ("Change compliance checks", Operator no),
  `events.view`, `events.manage`, `events.clearLog`, `logs.server`, `sites.view` ("See sites", Operator and
  Viewer yes)), "<plugin id>:<key>" for core plugins (every page gets "show"
  automatically) and "task:<id>" / "task:<id>:<key>" for task plugins. `PermissionCatalog.Build` collects them from every
  loaded plugin (on or off) at start; `RoleStore.SyncCatalogAsync` gives a role a permission it has not seen once: Operator
  and Viewer by the permission's default, custom roles nothing, so a limited role never gains a right by itself. Keys a
  role has but no loaded plugin declares are kept. **Administrator only, never grantable**: users and roles, the
  credential list (list, add, remove, reveal), plugins on / off, `SettingsService.Set`, the audit log. Every gRPC method
  has a rule in `AccessPolicy` (Open, SignedIn, a permission, InCall, Admin; a test checks that every contract method is
  listed, an unlisted one is Admin): device list, tags, tasks list and settings Get need only a login; plugin calls and
  task runs are checked in the call (`InCall`). The operator default withholds the risky rights:
  Web View, remove users on devices, firmware factory default and downgrade, application downgrade and removal, connection
  settings import; event rules, tag management and Delete all stay off as before. `RolesUpgradeNotice` records what
  operators lost at the first start of an installation that had users; administrators see it as a banner with "Check
  roles" until they dismiss it (per client).
- **Device groups** (`docs/spec/device-scope.md`): every user has "All devices" or a device group (`Users.AllDevices`,
  `DeviceGroupId`; Administrator always all devices). The interceptor puts the caller with its devices
  (`AuthenticatedCaller.Devices`, the group's revision) into the call's `UserState` (`AuthInterceptor.CallerOf`); services
  get their devices from `DeviceViewFactory.ForAsync(context)`, never by id from the repository. `AccessRule.AllDevices`
  marks methods only users with all devices may call (PERMISSION_DENIED "Only users with all devices can do this.").
  `CallerContext` is a backstop only and never flows into background work (`CallerContext.Suppress`, `RunDetached`,
  `Raise`: task runs, event handlers, detached work, timers). A deleted group leaves its users with no devices (fail
  closed). Audit: "Created device group", "Changed device group" (what changed), "Deleted device group" (who has no devices
  now, what was turned off), a user's devices in "Changed user" ("devices Building B") and "Added user" (only for a group: "Role Operator, devices Building B").
- **Open streams**: `CallerStreams` registers every streaming call and the interceptor checks the session and the role
  once more right after the registration (a change in between never leaves a stream with old rights); a role change ends
  the streams the role no longer allows (the HTTP request is aborted, the client reconnects and is refused), a user with
  another role or other devices gets all streams ended, so do the users of a group whose rules changed (a stream whose
  devices changed between check and registration ends with UNAVAILABLE "Your devices changed. Connect again."). `AuthService.WatchSession` (not ended by role changes) sends the user with role and
  permissions after every change. A logout, a new password, a disabled or deleted user end every stream of those sessions,
  the session stream too (`AuthTokenStore.SessionsEnded`); revoking deletes the token rows before the cache entry, so a call
  in between cannot bring the session back.
- **Plugin methods**: `CorePluginHost.AccessOf` resolves the permission of a page method: the plugin's
  `RequiredPermission(method)` when it names a declared key or "show"; otherwise Operator methods need "show" (a login
  for a plugin without a page) and Admin methods "manage" (automatic for a plugin that declares nothing but answers
  `RequiredRole` itself). Anything else, and a plugin that throws, is Administrator only. `PluginService.Watch` needs
  "show"; `ListCorePlugins` leaves out pages the role may not show. Calls where `ICorePlugin.IsAudited(method)` (DIM: the
  Admin methods) are audited as "Plugin action" (target the plugin, detail the method); VAPIX Commander `tryRequest`
  and `rollout` are audited; "Install in trusted root store" on the server asks with the CA's SHA-256 fingerprint and is
  audited. `TaskService.Run` needs every key `PermissionCatalog.RunKeys` names for the payload (refused before any task
  starts), `Query` any key of the task; `ListTaskPlugins` leaves out tasks without a key. Users with a device group
  (docs/spec/device-scope.md "Plugins"): only packages with `minSdkVersion` 0.4.0 or later, never the methods a plugin
  declares with `RequiresAllDevices`, and the plugin's `ctx.Devices` / `Vapix` / `Tasks` / `EventStreams` / `System`
  refuse during their calls, in work started from the call and in their jobs (PERMISSION_DENIED); jobs, job ids and
  downloads belong to the user who started them (another user's id answers NOT_FOUND like an unknown one).
- **Audit log**: table `AuditEntries` (TimeUtc, UserName, ClientAddress, Action, Target, Detail), 365 days or 200,000
  entries. Logged: login ok / failed, logout, user changes, roles ("Created role", "Changed role" with "+Web View: Open
  devices, -Restart", "Deleted role"), settings changes, credential list add / remove / reveal, PKI
  actions, DHCP / NTP save, task runs (plugin, device count), Delete all, device remove, device login and first password
  (counts), devices added or moved automatically (user "system"), the waiting list (kept, removed), tags ("Tagged 12 devices: +Building A -PTZ"), plugins
  on / off, event rules, VAPIX Commander send / rollout, Web View ("Opened web interface", the device), zoom and focus ("Changed zoom or
  focus", "Zoom 1.50"), RTSP port ("Changed RTSP port in OADM"). The Logs page has an **Audit** tab (Admin only, newest 10,000,
  virtualized, search).

## Client: login and users

- `Shell/AppShell` shows `LoginWindow` (`LoginViewModel`) before the main window, again after Log out and when a call
  answers UNAUTHENTICATED ("Your session has ended. Log in again."). Fields: Server (editable select with the recent
  servers, default `localhost:5080`; `--server` preselects it), User name, Password (`ui:PasswordBox`), Remember me (keeps
  {user, token} per server in `RememberedLogins`, never the password; resumed with `AuthService.Me` at start), Log in;
  first-administrator mode adds Confirm password and, for remote servers, the setup code. Errors below the fields,
  connection problems below the server field; a narrow label column (`StackPanel.compactForm`), no product mark, **Quit**
  on the left; a refresh icon right of the server field ("Connect again") repeats the start without restarting the client.
  Fake mode signs in as "admin" without the window.
- The title bar shows the logged-in user (initial avatar + name, tooltip "Logged in as ... (role) on ...") with Log out; a
  click on the user opens **Your role** (`Shell/MyRoleWindow`, `MyRoleViewModel`, `RoleService.Mine`): the role's name
  and what it allows, grouped like the matrix, read-only. `UserSession` keeps role name and permission keys
  (`Has(key)`, `PermissionsChanged`), fed by Login / Me and the session stream (`ServerConnection`); a server without
  roles sends none and counts as before (Administrator everything, others everything but the Administrator-only parts).
  `UserSession.IsAdmin` (the Administrator role) hides the Users, Credentials and Plugins pages and the Audit tab and
  shows the server settings as plain text ("Only administrators can change server settings."). Everything else follows
  the permissions live: toolbar entries (`IToolbarPlugin.RequiredPermission`, `DeviceToolbar.ApplyPermissions`), the
  context menu entries, the live view click, the tasks pane's Cancel / Delete / Delete all, the Event rules rail entry
  (`events.view`) and its changes, tag administration, the rail's plugin pages and the task menu (reloaded from the
  server, which filters them). The server checks every call again. `UserSession.HasAllDevices` (Administrator, All
  devices, or a server before device groups: `UserInfo.all_devices` unset) hides what only users with all devices may do
  (docs/spec/device-scope.md): tag rename, recolor and delete, Delete all of the tasks pane, Clear of the event log, the
  Server tab of the Logs page, turning compliance on or off; the client SDK contexts (`IToolbarContext`,
  `ICorePluginClientContext`, `ITaskDialogContext`) answer `HasAllDevices` from it. **Your role** shows the devices on
  top: "Devices: All devices", "Devices: Building B (412 devices)" with the group's rules below (`MyRole.devices`, never
  the excepted devices), or "Devices: No devices".
- **Users page** (Admin only, subtitle "Who can log in to this server and what each role may do."; `Settings/UsersPageView`
  = the users card on top at the height it needs and the roles card filling the rest, with a splitter): **New user** (secondary button) above the list, then users with role
  (plain text), last login, state; row icon buttons Edit user, Change password, Disable / Enable, Delete (red; Disable
  and Delete confirmed; the own row offers only Change password); never the last enabled administrator, never yourself.
  The list has a **Devices** column: All devices, the user's device group, or No devices (the group was deleted).
  Add user (user name, password, role, devices), Edit user (the role and the devices; Administrator with a hint) and
  Change password (new + confirm) are one single-operation dialog `Settings/UserFormWindow` (`UsersViewModel.Form`),
  errors and refusals below the fields. **Devices** offers All devices and the groups (`DeviceAccessOption`): for the
  Administrator role it is locked with "Administrators always have all devices."; with a group the hint says "Users with
  a device group view the NTP server, DHCP server and PKI pages but cannot change them." (`DeviceGroupWords`, also in
  the role dialog); a user without devices must get a choice before Save. **Device groups card** below the users
  (`DeviceGroupsCardView`, `DeviceGroupsViewModel`, `DeviceGroupService`): Group (description as tooltip), Rules ("Site
  Building B, tag PTZ, 3 devices, except 1", from the server), Devices (right-aligned), Users; **New device group**, row
  Edit and Delete (red; the confirmation names the users who have no devices afterwards). New and Edit open the group
  editor (`DeviceGroupEditorViewModel`, `DeviceGroupFormView.CreateWindow`): the shared device picker in its group mode
  with Name and Description (Optional) on top; the device list is built off the UI thread; Save sends the rules with the
  revision the editor started from; name errors below the name, refusals of the rules (too many, changed meanwhile)
  directly below the device list. **Roles
  card** (`RolesCardView`, `RolesViewModel`): the permission matrix, rows = permissions in groups (Devices, Tasks, Event
  rules, then one group per plugin in rail order; collapsible, a three-state check box per role for the whole group),
  columns = roles (built in code-behind; header with name, "3 users", "Starts on Compliance" and a menu Edit, Copy, Reset to default, Delete; the Administrator role Edit and Copy),
  the name column frozen; the permission's explanation as the tooltip of its name, an amber warning icon right after
  the name for full device control (tooltip `RolesCardView.FullDeviceControlTip`), "Plugin is off" for rights of a
  plugin that is off. Toolbar New role (secondary button), Save, Discard, search; changes are collected and saved together after one
  confirmation that lists them per role; a new role, a copy and a rename open the dialog `Settings/RoleFormWindow`
  (Name, Description, Start from, **Start page**) and stay "Not saved" until Save; Edit opens the same dialog (the
  Administrator role: name and description locked, only the start page).
- **Start page per role** (`RoleEntity.StartPage`, migration `RoleStartPage`, empty = Devices): the page users of the role
  see first after a login: Devices, Compliance, Sites, Event rules, any core plugin page, Logs (the rail keys, `HostPages`,
  "plugin:<id>"). The server takes only those (`RoleGrpcService.StartPageOf`, a plugin page also while the plugin is off)
  and audits "start page Compliance"; `UserInfo.start_page = 11` brings it with Login / Me / WatchSession
  (`UserSession.StartPage`). The client opens it after the login (`MainWindowViewModel.Start`): at once when its rail
  entry exists, a plugin page as soon as the connect added it; a page the user opens first wins, and a page that does not
  come (no right, plugin off) leaves Devices. Tests `RoleTests.ARoleStartsOnItsPage...`, client `RolesMatrixTests`,
  `ShellNavigationTests`.

## Automatic login on the add page

- **Axis check first**: before any credential is sent, the device must answer the anonymous `basicdeviceinfo.cgi`
  `getAllUnrestrictedProperties` with a 12-hex serial number equal to the discovered one and a ProdNbr. AXIS OS 8.x
  answers 401 there; then the realm `AXIS_<serial>` of the login challenge must name the discovered serial
  (`DiscoveryAuthenticator.JudgeRealm`, `VapixAuthenticationException.RealmSerial`). Then **HTTPS first** (Digest, then Basic), then HTTP (Digest, then Basic): such a
  verified device may get Basic over plain HTTP when it offers nothing else (`VapixConnectionOptions.AllowBasicOverHttp`,
  on the server's network only; `DiscoveryAuthenticator.Judge`, `LoginSchemes`). Outside this login Basic goes over plain
  HTTP only while an administrator turned on the server setting **Allow Basic login over HTTP**
  (`Devices.AllowBasicOverHttp`, default off, audited with the other settings as "Allow Basic login over HTTP Off -> On"):
  then for every device connection on every route (`docs/spec/vapix.md`). Known residual risk: a device that imitates the anonymous answer or the realm passes the check.
- "Save to credential list" of the login editor is shown to administrators only and the server ignores it for other
  roles (`RetryAuth`), like everywhere the credential list is written.
- Candidates: the credential list and credentials typed on the page; passwords of managed devices are never tried on new
  devices; at most 10 rejected credentials per device. Details in "Add Devices Page".
- Users with a device group: the credential list is never tried for them (adding, Retry, Log in, the waiting list): a
  session tries the list only after `DiscoveryAuthenticator.AllowCredentialList` (users with all devices, the server's
  automatic add), else only what was typed for it (fail closed). Test `AddDevicesScopeTests` (no credential request reaches
  the device, also after the list changed and through the waiting list).
- Tests: `AxisCheckTests`, `FastAddTests`; read-only hardware test `FastAddHardwareTests` (the development camera passes
  on both schemes).

## Master key

- A key check value is stored in `Setting` `Security.KeyCheck` (base64 HMAC-SHA256 of "OADM master key check v1" with
  the key, `CredentialProtector.ComputeKeyCheck`). `Oadm.Core.Security.MasterKeyCheck` runs in `DatabaseInitializer` after
  the migrations, before anything is served. The key is replaced (**warn and continue**) when `master.key` is missing,
  has the wrong size or does not match the stored value, or, without a stored value, when encrypted rows exist and none
  decrypts.
- An existing file is renamed to `master.key.replaced-<UTC time>`, never deleted (`MasterKeyFile.Replace`); the running
  `CredentialProtector` switches to the new key. One transaction deletes the unreadable `DeviceCredentials` and
  `CredentialListEntries` rows, sets those devices to Credentials required, stores the new check value and the notice
  `Security.KeyReplaced` ({id, replacedUtc, devices, credentialListEntries}). The error log names the counts and that
  plugin secrets of the old key (saved command passwords, CA keys, the TLS key) cannot be read either; the PKI shows "CA
  key cannot be read".
- `ServerSettings.key_replaced = 20` (`KeyReplacedNotice`) in every `SettingsService.Get` / `Set` reply; the client shows
  a warning banner below the connection banner ("The server's key was replaced: 3 devices need their credentials
  again." + ", 1 credential list entry was removed.", `Border.banner.warning`) with Dismiss; dismissed ids are kept per client
  (`DismissedKeyNotices`), so a later replacement shows again.

## Limits

- Device HTTP answers at most 16 MB (`VapixClient.MaxResponseBytes` = HttpClient `MaxResponseContentBufferSize`; over it
  `VapixResponseTooLargeException` "The device answer is larger than 16 MB and was not read."). Streamed answers (`VapixRequestOptions.StreamResponse`) go to disk with the caller's own limit (System
  report: 256 MB per device). Upload requests (firmware, ACAP) are streamed and not limited.
- XML from a device only through `Oadm.Sdk.Vapix.DeviceXml.Parse` / `ParseElement`: 1 MB of characters, DTD prohibited,
  no resolver; callers cannot lift the limits. `CodeView` keeps its own 512 K plain-text limit.
- RTSP / video access units at most 8 MB (`NalDepacketizer.MaxAccessUnitBytes`: a larger one is dropped like a damaged
  one and counted in `OversizedAccessUnits`, the stream resumes at the next keyframe).
- Uploads: 10 GB for all uploads together and at least 1 GB free disk (`UploadStore.QuotaBytes` / `MinFreeDiskBytes`): a
  new upload reserves its declared size and removes the oldest finished uploads until both fit, else
  `UploadRejectedException { TooLarge }` = RESOURCE_EXHAUSTED;
  a single upload may be up to `Uploads.MaxMegabytes`. Free space unknown (network share): only the quota applies.
- Requests stay on the device: `VapixClient.SendAsync` refuses (before sending) an absolute URI with another scheme, host
  or port or with user info, and a relative URI with "\". VAPIX Commander paths must pass `CommandValidator.IsDevicePath`
  (one leading "/", no "\", "//", "..", "@" before the query, spaces or control characters; "The path must start with /
  and stay on the device: no "..", "//", "\" or host name.", `CommandValidator.PathProblem`), checked again after the
  fields are filled in. Tests `DeviceLimitsTests`, `RendererTests` (`/\/host`, `//host/x`, an absolute URL).

## Crash handling (client)

- Plugin dialogs, pages and toolbar entries run inside try/catch: "The <name> dialog failed: <message>" (nothing runs,
  `TaskPluginRunner`), a page shows "The <name> page failed: <message>" and its rail entry stays (the other pages load,
  `MainWindowViewModel.SyncCorePluginPages`), a failing toolbar entry is left out ("The toolbar entry <id> failed:
  <message>", `DeviceToolbar`).
- `Infrastructure/CrashHandling` (installed by Program only: the process handlers before Avalonia starts, the UI handler
  in `AfterSetup`; handlers for `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` and
  `Dispatcher.UIThread.UnhandledException`) logs every unhandled error with the client log file path
  (`<datafolder>/logs/client-yyyyMMdd.log`); a UI thread error is handled and shown once
  at a time in `ui:MessageWindow` ("Something went wrong", the message, "OADM keeps running. Details are in the client
  log: <path>"); fatal errors (out of memory, access violation, invalid program, bad image) are logged as Critical and
  end the app; unobserved task errors are logged and observed.

## Service: firewall and folders

- **Firewall (Windows service only)**: the MSI opens TCP 5080 (Domain, Private). The NTP and DHCP plugins open their UDP
  rule ("OADM Server (NTP, UDP 123)", "OADM Server (DHCP, UDP 67)", Domain and Private, for the server exe) when enabled
  and remove it when disabled: SDK `ICorePluginContext.Firewall` (`IFirewallRules`, null outside the Windows service) and
  `FirewallRuleKeeper` (the first sync always applies, so a rule left by a crash is removed at start; errors logged,
  retried on the next change; `FirewallRule.ForService("NTP", Udp, 123)`); server `NetshFirewallRules` (`netsh
  advfirewall`: `delete rule name=...`, which ignores "No rules match", then `add rule ... dir=in action=allow
  protocol=UDP localport=<port> program=<exe> profile=domain,private`) through `ICommandRunner`.
- **Folders** (the service runs as SYSTEM / root): at start (step 0 of `OadmServerHost.StartAsync`, service mode only)
  the data folder, the runtime extraction folder and every plugin folder must be writable only by administrators / root
  (`Oadm.Server.Hosting.FolderGuard` over `IFolderPermissions`: `WindowsFolderPermissions`, `UnixFolderPermissions`). Windows: owner and every entry SYSTEM / Administrators / TrustedInstaller, no
  write, append, delete, change permissions or take ownership for anyone else (CREATOR OWNER allowed); fixed by owner
  Administrators and a protected ACL SYSTEM + Administrators full control (+ Users read and execute for plugin folders).
  Unix: owner root, no group or world write (`find <dir> ! -type l ( ! -user 0 -o -perm -0020 -o -perm -0002 )`; fixed
  by `chown -R 0:0`, `chmod -R go-w`). An unfixable data folder stops the start, an unfixable plugin folder is skipped
  with a load error (`PluginLoader.FolderCheck`). The development plugin folder
  (`artifacts/plugins`) is used only outside service mode.

## Tests

`tests/Oadm.Server.Tests`: `AuthTests` (every service per role, the role table against the contracts, plugin method
roles, first administrator, sessions, throttling, audit), `TlsPinningTests` (real Kestrel with TLS), `MasterKeyCheckTests`,
`MasterKeyFileTests`, `KeyReplacedNoticeTests`, `DeviceLimitsTests`, `UploadStoreTests`; `MethodRoleTests` in the DHCP,
NTP, PKI and VAPIX Commander test projects; `tests/Oadm.Client.Tests`: `LoginTests`, `CrashHandlingTests`.
`TestServerHost` calls as the administrator "admin" (`InvokerFor(token)` / `InvokerForUserAsync(name, role)` for others,
`createAdmin: false` for an empty user table, PBKDF2 with 1,000 iterations for speed).
