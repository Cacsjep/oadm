# Device groups

Status: built on branch `feature/device-groups` for OADM 4.2 (decisions 2026-10-22 and 2026-10-23, after an independent RBAC review and
an independent security review the same day). A user may be limited to part of the devices: everything the user sees and does follows that limit.

## Model

- **Device group:** a named object (table `DeviceGroups`, new migration): name, description, and its rules: **sites**
  (with "This server" = devices without a site) and **tags**, by id, plus **single devices** and **except** single
  devices, both by **serial number** (stable when a device is removed and added again, unlike its id).
- **On the device:** whether a user with all devices added it (a new column, true for every existing device). The
  dormant serial rule and the host-name rule of PKI and EST read it. A waiting entry stores the user who queued it. A device is in the
  group when a site, a tag or a single serial covers it and its serial is not excepted. Sites and tags keep up: a new
  device of a chosen site or tag is in the group at once.
- **On the user**: an explicit choice "All devices" or a group (a flag plus the group id; never "null = all devices").
  The foreign key is `OnDelete(Restrict)`. The migration sets "All devices" on every existing user, so an upgrade or
  the restore of a backup from before 4.2 changes nobody's devices; after that, a user without the flag and without an
  existing group has no devices. The role stays the rights; the group is the
  devices. "Operator" + group "Building B" instead of a role per site. Administrator always has all devices (a group on
  an administrator is ignored and the user form says so).
- **Deleted tag or site:** drops out of the groups that use it; deleting asks first ("Used by device group Building B").
  Deleting a group sets its users to **no devices** and disables the objects it owned in the same transaction (fail
  closed) until an administrator gives them another; a test proves it.
- **Group changes** (rules, a user's group): the user's streams that carry devices end and reconnect with the new view,
  like a role change (`CallerStreams`). A new name or description alone ends nothing (no streams, no jobs, no prune).

## Enforcement: explicit, fail closed

- **Explicit caller.** gRPC services build a `DeviceView` from the authenticated caller (`AuthenticatedCaller` carries
  the user's compiled group) and pass it down: `view.ListAsync`, `view.GetAsync(ids)` (an id outside the view answers like
  an unknown device, NOT_FOUND; a batch with any such id is refused whole, "N devices are no longer available", never run
  in part), `view.Contains(device)`. Unknown ids refuse the batch the same way for every user (today Remove and Refresh
  skip them), so the answer never tells an unknown id from one outside the view. No scope check is written per method; the methods get their data from the view.
- **Fail closed.** The global repository stays for system work, named as such. A scoped getter without a view throws; it
  never allows everything. `CallerContext` (AsyncLocal) stays only a backstop.
- **Background boundary.** The host never lets the caller flow into background work: it clears the caller where it starts
  task runs (`TaskEngine`), plugin jobs (the SDK job registry), timers and the handlers of its events (device feed, tags,
  waiting list, users, sites, plugin events). A task gets its owner's view explicitly. A plugin that starts its own work
  inside a user's call (`Task.Run`) keeps the caller, so `ctx.*` refuses there too (fail closed); it uses the job registry.
- **Compiled predicate, no patched index.** A group compiles to sets (site ids, tag names resolved from tag ids and
  rebuilt when tag definitions change, serials, excepted serials); a check is a few hash lookups on the device row. Lists
  cache the allowed id set lazily per cache key (see Sessions).
- **Streams:** each stream keeps the ids it has sent; on a change a device that left the view becomes Removed, one that
  entered becomes Added. Sessions bound to one device (live video, web interface, event streams, I/O and metadata
  monitors) end when that device leaves the user's view.
- **Tasks:** the engine checks the caller's view when a task is started (`TaskEngine.RunAsync` with the view passed in,
  also through the plugin task runner); task history is filtered in the query (a temp table or `json_each` for large
  views, never after the 10,000 snapshot limit). History of removed devices: only users with all devices.

## Plugins (SDK 0.4.0, OADM 4.2)

- **During the call of a user with a group, `ctx.Devices` / `ctx.Vapix` / `ctx.Tasks` / `ctx.EventStreams` refuse**
  (fail closed): a plugin method uses `call.*`; the full list only through `ctx.System`, outside user calls or from call
  sites on an allow-list with a reason. So a method nobody updated cannot reach devices outside the user's view. For
  users with all devices `ctx.*` works as before (decision 2026-10-23): plugins built against SDK 0.3.0 (public on NuGet
  since OADM 4.1.0) keep working for them and are refused for group users until they move to 0.4.0.
- Plugin methods get the user's call explicitly: `InvokeAsync`, `OpenDownloadAsync` and task `QueryAsync` gain
  overloads with an `ICallContext` (default interface members): `call.UserName`, `call.Has(permission)`,
  `call.Devices` / `call.Vapix` / `call.Tasks` / `call.EventStreams` (scoped to the user's group), `call.Contains`,
  `call.HasAllDevices`. A plugin declares its methods for users with all devices (saves of NTP, DHCP, PKI, EST, the
  command library); the host refuses them for group users before the plugin runs, and the canary test reads the list.
  `ctx.Devices` and the other `ctx.*` stay the full, system view for background work (sweeps, the EST server, the
  trigger runtime, NTP, DHCP). Background work never carries a user.
- Events: `IPluginEvents.Publish(topic, json, audience)` with the audience a device, a device set, a user or a session;
  the host delivers each event (and filters rows of a set per watcher) only to watchers it covers; the old
  `Publish(topic, json)` goes only to watchers with all devices. Sweep state (ActionRules, EdgeStorage, ImageHealth) is
  kept per group, not per plugin.
- **Jobs, streams and downloads have an owner:** an SDK job registry keyed to the caller's user and session (report,
  scan, export and monitor ids); every id-taking method and download checks it; ids of another user or session answer
  NOT_FOUND, and an ended session forgets its jobs, finished ones too. A shared job (`IPluginJobs.StartShared`, one scan
  per scope) is found and stopped by every user with its device scope; it belongs to the scope, not to the user who
  started it (lead decision 2026-10-23): the starter's logout, new role or other devices leave it running, it ends only
  when the rules of its group change, on a cancel or when its plugin stops. A job started in a call whose user's devices, role
  or session changed after the call began is refused ("Your devices changed. Connect again.").
  When the owner's devices change (another group, new rules of the group, another role) the owner's own jobs end and are
  forgotten, finished ones too, and `Find` answers only a caller with the job's device scope. When devices move into or out
  of the group (tag, site, add, remove; the coalesced `HostDeviceScopes.Changed`) the finished jobs of its users are
  forgotten (a Snapshot Report PDF, an event list read) and the running ones when they end. Tasks a call or a job
  starts through `call.Tasks` belong to the caller (`TaskEngine.ForView`: owner, queue check, `ITaskExecutionContext.Caller`).
  `ctx.DeviceScopes` answers a group user's call (and its jobs) only for the caller's own scope key; a method with a
  `SystemAccessReason` also gets `Exists` and `Contains` of other keys (yes or no about devices it has from `ctx.System`),
  never their device lists or names.
- **Uploads have an owner:** an upload (`FileService.Upload`) belongs to the user and session that sent it
  (`UploadOwner`); `FileService.Delete`, a backup restore and the tasks of another user or session answer like an unknown
  id; tasks the system starts (no caller) read any upload. Uploads from before 4.2 and of the system belong to nobody.
- Every bundled plugin moves to the call context before 4.2. Plugins built against SDK 0.3.0 load and run unchanged for
  users with all devices (new SDK members are default interface members); their methods are refused for group users.
- Plugin testing: `Oadm.Sdk.Testing` gets a fake call with a group and the canary test (see Tests).
- **Built (plugin host):** the call per call, the refusals, jobs with owners, event audiences (plus `DeviceScope(key)` for
  state kept per group), `ctx.DeviceScopes`, the sweep per scope and the plugin canary (`PluginCanary`): see
  `docs/spec/plugins.md` "Host side of the call context". A converted bundled plugin sets `minSdkVersion` 0.4.0 in its
  plugin.json and runs a `PluginCanary` in its tests, or `CanaryTests.PluginsConverted` gives the reason it needs none
  (`CanaryTests.EveryBundledPluginIsConvertedOrListed`).

## Decisions

- **New devices:** a user with a group discovers and adds devices only at the sites of the group, so they land inside
  it; a group of tags or single devices only cannot add devices.
- **Tags:** users with a group tag devices of their group, but never add or remove a tag that any group uses (that would
  move devices between groups). The guard lives in `DeviceTagStore` (the only writer), also for file imports added later
  in the background (the waiting entry remembers who queued it). The same for the tags a plugin protects (decision
  2026-10-24, 802.1X review round 2: the tags of the VLAN rules, else a group user would choose a device's VLAN; docs/spec/tags.md
  "Protected tags").
- **Shared objects** (device triggers, compliance checks, Live View views, dashboards): each object stores its owning
  group (or all devices); an administrator editing it keeps the owner; a duplicate gets the group of the user who
  duplicates and, for a user with a group, only the devices the user sees now. Saving refuses device ids and tags outside the owner's view (NOT_FOUND), and it runs only on devices of that
  group ("All devices" = the group). Users see and edit only objects of their own group; others are hidden. A deleted
  group disables its objects. A user without devices (no group, or the group was deleted) sees no shared object, saves
  none ("Your account has no devices.", `OwnerScope.HasNoDevices`: a scope key that is not a group id) and gets no
  shared job or slot (the hardening scan refuses), whatever key the host gives such a user.
- **Event rules are not limited by device groups** (decision 2026-10-23): rules are for administrators and operators
  with all devices. Users with a group get no event rules: the rules, the event log, the event catalog and **Test**
  (every `EventService` method) are `AccessRule.AllDevices`, and the client hides the Event rules page for them. Rules
  have no owner and run on all devices as before device groups. Send HTTP request, Send TCP message and Send email send
  to any address (no allowed-address list), as in 4.1.0.
- **Server-wide services** (NTP, DHCP, PKI and EST settings, the VAPIX Commander command library): managing them stays with users who have all devices; users with a group may view the pages, rows filtered to
  their group (by device, address or serial; DHCP leases by MAC address only, never by address). The role and user editors say so.
- **Server log:** hidden for users with a group (its text names devices). System report, backups, audit log, support
  report: as today (administrator or users with all devices).
- **Sites page:** users with a group see only the sites of their group, with their group's counts.
- **Kept for users with all devices:** tag rename and delete, Delete all of tasks and the event log.
- **Credential list:** never used for users with a group: adding devices, Retry, Log in and the waiting list use only
  the password they type. (A fake camera at their site could otherwise collect every saved credential.)
- **Secrets in shared items** (library commands and configuration files with saved passwords): users with a group may
  run only items without saved passwords.
- **Host names in certificates:** PKI and EST put a host name into a device's certificate only when a user with all
  devices added it (a group user who controls the DNS at a site could otherwise get a certificate for any name). When a
  device group covers the device, only what that user stated counts: the stored address, never the host name, FQDN or
  further IP addresses the camera reports (a group user can change them on the camera). A device no group covers keeps
  the reported names and addresses. Always: the stored IP address and `axis-<serial>.local`. Built:
  `CertificateDeployment.NamesFor(device, network, inAnyGroup)` and `EstEndpoint.DnsNames(device, inAnyGroup)` read
  `IDeviceInfo.AddedByAllDevices` and `IDeviceScopes.InAnyGroup` (SDK 0.4.0, default true; no scopes: covered).
- **Membership:** a single-device serial of a removed device is dormant; it counts again only when a user with all
  devices or a user of that group adds the device. Every change of a device's membership (tag, site, relocation, add,
  remove) is audited as "left / entered group X".
- **"Empty means all"** (Refresh, compliance Check now, sweeps, scans, reports): all means the caller's view.
- **Tasks:** task id methods (log, cancel, delete) go through the view; runnable sets are intersected with the view
  before they are encoded; a queued task checks the owner's view again when it leaves the queue.
- **Add and discovery sessions** belong to the user who started them; Retry and Commit take only that user's sessions;
  a found device must be on a route of the group; the network scan and zero-conf on the server's network only for
  groups with "This server"; the waiting list is scoped by site (entries without a site: only the user's own) and checks the queuing user's current view.
- **Sessions:** a stream checks the user's group revision like the role right after it registers; a change of a group's
  rules or of a user's group ends every non-session stream of the affected users (like `OnUserRoleChanged`). The cache
  key is (group revision, tag and site catalog revision, `DeviceChangeFeed.MembershipVersion`: it moves only when a
  device is added or removed or its serial, site, tags or adder change, never for a status, so at 30,000 devices a polling
  round reads no allowed ids again; at the start `HostDeviceScopes.LoadAsync` seeds the feed with every stored device,
  `DeviceChangeFeed.SeedMembership`, so the first polling round moves nothing either). Every writer of the device table
  publishes the device as stored after its write, never the copy it read before (`StoredDevicePublisher`: the row read
  again and published behind the gate of the device, 64 gates per feed striped by id, so reads and publishes of a device
  keep their order; repository (one connection for the read, the write and the read after it), the polling's status
  note. Batch writers, tags and credentials, hold every gate from before their transaction until they published
  (`StoredDevicePublisher.HoldAsync`, gates first, then the database write lock): the rows read inside the transaction
  are what the table holds, published without a second read): a status write that read the tags before a tag change never puts the old tags back into the
  feed, the scope rows or the grids of the old group. One read is shared by concurrent callers and never runs on one caller's token. The view never follows the "no
  caller = allow" pattern of today's helpers. Web interface cookies stay on the server.
- **One phrase for a device the user cannot reach:** plugin answers to a user's call about an unknown id, or one outside
  the user's devices, say "1 device is no longer available." (`DeviceMessages.NoLongerAvailable`), also as a row or
  state text (monitors, reports, snapshots, event list reads, trigger saves); "The device was removed from OADM."
  (`DeviceMessages.Removed`) stays for background state of a stored object whose device is gone (a trigger's device row)
  and for the host.
- **Small leaks closed:** address checks answer only "in use"; conflict and log texts never name devices outside the
  view; discovery rows of such devices say only "already added"; Your role shows no excepted serials. "This server"
  means exactly devices without a site (devices kept from a removed site keep its id).
- **Existence leaks accepted** (documented, not hidden): "already added" on the add page and a duplicate add, the device
  limit message, the license's device count.
- **Shared limits accepted** (security review 2026-10-23): the server-wide caps are shared by all users, so one user with
  a group can fill them for everyone: 200 device triggers, 200 dashboards, 200 Live View views, 16 open event streams of
  the Metadata Monitor and 20 of the I/O Monitor. They are no per-group quotas; an administrator deletes or stops what
  fills them.
- **Address takeover accepted** (security review 2026-10-23): a group user can still give a camera of the group the
  address of a device outside it through raw requests of the VAPIX Commander or in the web interface (Network settings and
  Assign IP address refuse it). The harm is small: over HTTPS the pinned certificate stops the polling of the other device,
  over HTTP the camera gets only a digest response, never the password. The check of Network settings and Assign IP
  address compares the stored IP addresses only: a device stored by host name is not compared (OADM keeps no resolved
  address of it, and a name at a site resolves only there), so a group camera can take the address such a device's name
  points to, with the same small harm.
- **Shared waiting entries accepted**: a waiting entry with a site of the group is shared by the group's users: each
  sees it, removes it and uses Try now, and it is added as the user who queued it.
- **Release:** 4.2 (4.1.0 shipped the EST server on 2026-10-23), plugin SDK 0.4.0.

## The editors

- **Permissions page (formerly Users), card "Device groups"** right of the users (administrator): name, rules summary ("Site Building B, tag PTZ,
  3 devices, except 1") and users; New, Edit, Delete (red, says which users lose their devices). On a page narrower than
  1,640 px (small or scaled screens, `UsersPageView.TwoColumnWidth`) the card moves below the users and both scroll in
  the upper half, the roles card below the splitter.
- **Group editor:** the shared device picker (`DevicePickerWindow`) extended: left **Sites** (with counts and "This
  server") and **Tags** (with counts); right the searchable, virtualized device list: devices covered by a site or tag
  checked and locked ("Site Building B"), single devices tickable, an "Except" state; a live count ("412 of 4,812
  devices") that counts like the server (an excepted device is out, also when it is a single device; ticking it takes it
  back). An except stays stored while no chosen site or tag covers the device. Owner fields of compliance checks are left
  out for a server before device groups (UNIMPLEMENTED).
- **User form:** a **Devices** field (All devices or a group); **Your role** shows the group and its summary. For an
  administrator the field is locked and shows what the account stores (a group stays stored and applies once the role
  changes); every role change below Administrator sends the Devices choice the form shows.
- Client code: `Settings/DeviceGroupsViewModel` + `DeviceGroupsCardView`, `DeviceGroupEditorViewModel` +
  `DeviceGroupFormView` (the picker's group mode, see visual-style.md "Shared controls"), `UsersViewModel.DeviceOptions`,
  `MyRoleViewModel.DevicesText`, `UserSession.HasAllDevices` (hides the all-devices-only actions, see security.md),
  `MainWindowViewModel.RefreshMyDevicesAsync` (Your role's devices for Add, read again after every device stream connect:
  a change of the group's rules keeps its name but ends the stream);
  fake mode (`FakeOadmApi.DeviceGroups`) has Building B, Building A, Head office and Lobby, tech2 in Building B and
  guard in Lobby. Tests `DeviceGroupsTests`, `Perf/DeviceGroupEditorScaleTests`.
- Audit: created, changed (rules), deleted groups; a user's group changed.

## Tests

- **Capability type:** the services the gRPC classes use (login, first password, credentials, polling, live view,
  RTSP ports, tasks) take a `ViewedDevices` value only `DeviceView` can make, never raw device ids. The architecture
  test fails for a service that takes raw ids or the global repository, except an allow-list with a reason per entry.
  `AccessRule.AllDevices` marks gRPC methods only users with all devices may call (tag rename and delete, Delete all,
  Clear log), enforced by the host and covered by the table test; plugin methods use the plugin's declaration (see
  Plugins).
- **Canary test over every method** of `AccessPolicy` and every bundled plugin method (opting out needs a written reason):
  the out-of-view id injected in every id field; NOT_FOUND, no state change, and no VAPIX, RTSP, task or event stream
  contact with that device (a recording client factory). Plugin methods take JSON, so each plugin's tests name the id
  fields of its methods. Every method is converted: the test has no list of methods left to convert.
- **Security tests:** credential list never sent for a group user; deleting a group and restoring an old backup stay
  closed; races (group change during stream registration, view shrinking with queued tasks, group save during tagging);
  job, stream and download ids of user A refused for user B; every `EventService` method refused for a group user;
  empty ids touch only the view; runnable sets and address checks leak no ids or addresses; a
  removed serial does not join a group when re-added by someone outside it.
- Perf tests with 30,000 devices and 50,000 tasks: views, streams, task history paging.

## Code (foundation)

- **Model** (migration `DeviceGroups`): table `DeviceGroups` (`Oadm.Core.DeviceGroups.DeviceGroup`: Name unique
  case-insensitive 1..64, Description 0..200, `ThisServer`, `SiteIds`, `TagIds`, `Serials`, `ExceptSerials` as JSON
  columns, `Revision` from 1, at most 200 groups, 100 sites, 500 tags and 30,000 serials each); `Users.AllDevices` +
  `Users.DeviceGroupId` (FK Restrict); `Devices.AddedByAllDevices` + `AddedByGroupId` (no FK: a deleted group leaves the
  serial dormant); `WaitingDevices.QueuedByUserId`; `ComplianceChecks` `OwnerAllDevices` + `OwnerGroupId` (FK
  Restrict; `DeviceGroups` gave `EventRules` the same columns, the migration `EventRulesWithoutOwner` drops them again).
  `DeviceGroups` sets `AllDevices`, `AddedByAllDevices` and `OwnerAllDevices` to true on every existing row; the migration
  `DeviceGroupFlagsWithoutDefault` removes the database default 1 it left on the three columns, so a row inserted without
  them fails (NOT NULL) instead of getting all devices; C# default false: every writer chooses.
- **Scope value**: `Oadm.Core.Auth.DeviceScope(AllDevices, GroupId)` for users and owners; `None` = no devices. Scope keys
  (`ICallContext.DeviceScopeKey`, `DeviceView.ScopeKey`): "all", the group id, for a stored object without devices "none"
  (`DeviceScope.NoDevicesKey`), and for a user without devices a key of that user only, "none:" and the user id
  (`DeviceScope.NoDevicesKeyOf`; a view without a user: a random one): two users who lost their groups never share saved
  views, dashboards, triggers or a shared scan, and no scope (`ctx.DeviceScopes`) answers such a key (fail closed). The
  caller: `AuthenticatedCaller.Devices` (`CallerDevices(Access, GroupRevision)`, default `None`), `HasAllDevices`
  (Administrator always), `DeviceGroupId`, `SessionId` (opaque, for plugins). Sessions carry the access
  (`AuthSession.DeviceAccess`, `AuthTokenStore.SetDeviceAccess`).
- **Store**: `DeviceGroupStore` (in memory after `LoadAsync`; `SaveAsync(id?, name, description, rules,
  expectedRevision)`, `DeleteAsync`, `RevisionOf`, `GroupsUsingTag`, `GroupsUsingSite`, `TagIdsInUse`, events `Changed`
  (group id; created or new rules), `Updated` (every save that changed anything, also name or description: Your role) and
  `Deleted`). Delete: users to no devices and every `IDeviceGroupOwnedObjects` (core: `CoreOwnedObjects`
  for compliance checks: owner cleared, turned off) in one transaction, then
  `UserStore.NotifyDeviceAccessChanged`. `UserStore`: `AddAsync`/`CreateAsync` with a `DeviceScope` (a group must exist,
  never "no devices" by choice; the overloads without it give all devices: first administrator, older clients),
  `UpdateAsync(..., DeviceScope? access, ...)`, event `UserDeviceAccessChanged`; texts `DeviceGroupTexts`.
- **Predicate**: `DeviceGroupFilter.Compile` (hash sets; tags by id resolved through `DeviceScopeCatalog` =
  `DeviceTagStore.DefinitionNames` (updated before a write publishes its devices) + the sites that exist
  (`ISiteStates.NameOf`); key `DeviceScopeCatalogKey`). The serial rule covers a device only when
  `AddedByAllDevices` or `AddedByGroupId` is the group.
- **Views**: `DeviceViewFactory.ForAsync(caller)` (throws without a caller), `ForScopeAsync(scope, user)`,
  `ForUserAsync(userId)`, `System` (background work), `CountAsync(rules)`, `CountGroupsAsync`; `DeviceView.ListAsync`,
  `AllAsync`, `GetAsync(ids)` / `GetOrAllAsync(ids)` (in the order of the request; any unknown or outside id: `DevicesNotAvailableException`, NOT_FOUND
  `DeviceMessages.NoLongerAvailable(n)`), `FindAsync`, `Contains`, `IdsAsync` (cached per group revision, catalog key and
  `DeviceChangeFeed.MembershipVersion`), `CachedIds()` (the cached ids when current, never a read), `ScopeKey`. `ViewedDevices` (internal constructor, only `DeviceView` makes it). gRPC:
  `views.ForAsync(ServerCallContext)` (`Oadm.Server.Auth.CallerViews`, the caller from the call's `UserState`,
  `AuthInterceptor.CallerOf`); the interceptor maps `DevicesNotAvailableException` of any service to NOT_FOUND.
- **Access rule**: `AccessRule.AllDevices` (`.ForAllDevices()`): tag rename and delete, task Delete all, every `EventService` method (rules, event log,
  catalog, Test, Clear),
  server log, compliance on / off, server settings Set and the credential list; refused "Only users with all devices can
  do this." (`DeviceGroupTexts.AllDevicesOnly`, `AuthInterceptor.RequireAllDevices` for checks inside a call).
- **Background boundary**: `CallerContext.Suppress`, `RunDetached`, `Detached`, `Raise`: task runs, discovery and login
  work, the retry timer of the local network, and the events of the device and task feeds, tags, users, roles, sessions,
  waiting list, sites, plugins, settings, compliance and event rules raise without the caller.
- **Streams**: `CallerStreams` ends the non-session streams of a user whose access changed and of the users of a group
  whose rules changed; a stream whose devices changed between check and registration ends with UNAVAILABLE "Your
  devices changed. Connect again.".
- **Tests**: `tests/Oadm.Core.Tests/DeviceGroups/*`, `Persistence/DeviceGroupsMigrationTests`;
  `tests/Oadm.Server.Tests/DeviceGroupServiceTests`, `DeviceGroups/ArchitectureTests` (no gRPC class takes the global
  device access; service methods with raw ids on an allow-list with a reason), `DeviceGroups/CanaryTests` (cases,
  methods without devices, plugins without a canary and why, Administrator and all-devices methods closed by the rule).

## Code (add devices)

- `Oadm.Server.AddDevices.AddReach` (from the caller, or from the user who kept a waiting entry as that user is now):
  `Routes` (`Oadm.Core.Discovery.DiscoveryRoutes`: local = "This server", the group's live sites; all for all devices),
  `CanAdd`, `UsesCredentialList` (all devices only), `AddedBy` (the `DeviceScope` a new device stores), `Takes(serial,
  route)` (on a route of the user and inside the group), `Waiting` (`WaitingScope`). `DeviceGroupFilter.ThisServer` and
  `Sites` expose the compiled rules.
- Core discovery sessions take `DiscoveryRoutes` (zero-conf, range scan, address probes); `CoreDiscoveryBackend` passes
  them and finds a discovered device only in its own session. `DiscoverySessionOwners`: the user, login session and group
  of each add-page session; the session lives while a watch of it is open (`Hold`), so Add and Retry work however long the
  page stays open, then 30 minutes after its last use; then a timer stops it on the server and forgets the owner entry and
  its logins (`DiscoveryAuthenticator.Forget`). `SessionsEnded` (logout, expiry, password, disabled), `UserRoleChanged`,
  `UserDeviceAccessChanged`, `DeviceGroupStore.Changed` and `Deleted` stop the affected users' sessions at once; the change
  is recorded in `AccessEpochs` first and `Register` checks it after adding the entry, so a start whose call began before
  the change is stopped and refused (PERMISSION_DENIED "Your devices changed. Connect again.").
- `DiscoveryAuthenticator`: the credential list only after `AllowCredentialList(session)` (fail closed); the login
  session of a discovery session that still exists does not expire by itself, so it keeps that permission, the expected
  device and the typed passwords until `DiscoverySessionOwners` stops the session (a session made again after it expired
  tries only typed passwords).
- `DeviceAdder.AddAsync(..., DeviceScope addedBy, ...)` refuses `None`; every add path passes it (Commit: the caller,
  waiting list: the user who kept it, `DeviceAutoAddService`: all devices).
- Membership audit: `Oadm.Core.DeviceGroups.DeviceGroupMembership` (changes per group for adds, moves and removals;
  actions "Entered device group" / "Left device group"; also for tags and sites) and `Oadm.Server.AddDevices.MembershipAudit`
  (Commit, the waiting list, the automatic add, relocation and DHCP moves).
- Waiting list: `WaitingDeviceStore` methods with a `WaitingScope` (list, counts, ids, remove, add), `QueuedByUserId` written
  by `AddAsync(..., queuedBy, scope, ...)`: an entry without a site of a user with a group has its own key
  (`KeeperKey`: the address, "#" and the user's id; `AddressPart` for the comparison with managed devices), so the user
  never changes or learns of another user's entry for the same address, and the reply's total counts the user's scope; `WaitingDeviceGrpcService` through the caller; `WaitingDeviceRunner` per entry.

## Code (devices-core)

- **Device list**: `DeviceGrpcService` takes `DeviceViewFactory` and `DeviceActions` (Core: remove, refresh, credentials,
  Log in, Set password, passphrase policies, the relay's VAPIX client, status notes; only `ViewedDevices`), never the
  repository. `List` and the `Watch` snapshot read `DeviceView.PagesAsync(1,000)` (a group: the cached allowed ids, rows
  per page); `DeviceView.Subscribe()` gives a `DeviceViewFeed` (subscribed at once; `Sent(id)` for the snapshot,
  `ReadAsync(max)`): changes outside the view are dropped, a device that leaves becomes Removed, one that enters Added; a
  change of the tag and site catalog (deleted tag, removed site) compares the sent ids with `IdsAsync` and reads the
  entering rows in pages. All devices: every change passes as it is, no ids kept (today's feed). Remove, Refresh,
  SetCredentials, LogIn, GetCredentialUserName, SetFirstPassword, GetPassphrasePolicies and GetWebUiUrl go through
  `view.GetAsync(ids)` (Refresh without ids: `GetOrAllAsync`); unknown ids refuse the whole request for everyone.
- **Sessions bound to one device**: `DeviceViewWatcher.WatchAsync(ViewedDevices one)` gives a `DeviceViewLease` whose
  `Left` token fires (on the thread pool) when the device leaves its view: Removed, a device change outside the view, a
  catalog change, the group's rules changed or the group deleted (checked again with `FindAsync`); registered first, then
  checked, so nothing in between is missed; views with all devices get a lease that never ends. Used by `LiveViewService.Watch`
  and `DeviceWebService.Exchange` (both end NOT_FOUND "1 device is no longer available."). `ViewedEventStreams`
  (`IDeviceEventStreams` of one view): opens only devices of the view, an open source throws
  `DevicesNotAvailableException` when its device leaves, RTSP ports only of viewed devices; for `ICallContext.EventStreams`.
- **Live view**: `LiveViewGrpcService` takes `LiveViewAccess` (Core: hub, sources, optics, PTZ, RTSP ports; only
  `ViewedDevices`); every method gets its device or devices from the view; GetRtspPort and SetRtspPort refuse unknown ids
  whole (no more partial write).
- **Sites page**: `SiteService.ListSites` / `WatchSites` (and the page `SetAccept` returns): users with all devices see every
  row with `Site.group_names` (`DeviceGroupStore.GroupsUsingSite`); a group sees only bound sites its rules name
  (`DeviceGroupFilter.CoversSite`; no removed sites, no waiting nodes) with `device_count` from
  `DeviceView.CountsBySiteAsync` (one read of the scope rows per push, at most once a second).
- **Address checks**: none in this area name another device (Log in and Set password say "Another device answers at this
  address."); the address checks of plugins (Network settings, DHCP) belong to their packages.
- **Tests**: `Oadm.Core.Tests/DeviceGroups/DeviceViewStreamsTests` (feed, catalog changes, pages, counts by site, leases,
  event streams), `Oadm.Server.Tests/DeviceGroups/DeviceGroupStreamsTests` (feed over gRPC next to an administrator's,
  new rules end the stream, unknown ids refused, live video ends, Sites page), canary cases for every converted method and
  `Filtered` (List, Watch, ListSites, WatchSites: the reply never shows the outside device), perf
  `DeviceGroupWatchScaleTests` (3,000 of 30,000 devices: snapshot, a burst over every device, 1,000 in and 1,000 out).

## Code (tasks)

- **Runs**: `TaskGrpcService.Run` takes `view.GetAsync(ids)` (the devices in the order of the request: the client pairs
  the task ids with its device ids) and calls `TaskEngine.RunAsync(pluginId, ViewedDevices, payload, TaskCaller)`; for a
  group user a task of a package below SDK 0.4.0 is refused whole (`PluginNeedsAllDevices`, also through `call.Tasks`
  and again when a queued task leaves the queue) and left out of `ListTaskPlugins`; a group owner's task runs under a
  `PluginCallMark` without all devices, so the package's `ctx.*` refuses inside it like in the owner's call; `TaskEngine.ForView(view, caller)` is the runner of a user's call (`call.Tasks`: outside ids
  refuse the run; cancel and the active check only for the user's own tasks when the user has a group, another user's
  active task answers null, "cannot tell"; `AnyActiveAsync` answers for the tasks of the view's devices). The raw-id
  `RunAsync` (`ITaskRunner`) is the system runner; a caller with a group in `CallerContext` is refused outside the view
  (backstop, never without `TaskEngine.Views`).
- **Queue check**: each task keeps its `TaskCaller` in memory; leaving the queue it reads the owner's current view
  (`ForUserAsync`: the user's row by its id, never the whole users table, kept by `UserStore.GetAsync` until the next
  write of the users table) and fails with `DeviceMessages.NoLongerAvailable(1)` when the device is not in it (also a deleted user
  or group), before the device's state is read out or any request is sent. The plugin gets `ITaskExecutionContext.Caller`
  (`TaskCallContext` over that view; event streams not offered there yet).
- **History**: `TaskEngine.ListPageAsync/ListAsync/SnapshotAsync/SubscribeWithSnapshotAsync/GetAsync/GetLogAsync/
  CancelAsync/DeleteAsync(view, ...)`; a user with all devices takes the unfiltered paths. Groups: the view's ids
  (`view.IdsAsync`) filter the store query (`EfTaskStore`: `json_each` over one JSON parameter, then the rows by id);
  `TaskViewSubscription` follows the device feed per watcher (a changed device is checked against the compiled rules),
  the tag and site catalog and the group's rules (the view's ids compared again, like `DeviceViewFeed`): the sent tasks of a
  device that left go out as Removed at once, the tasks of a device that entered as Added (its active tasks and the newest
  up to the snapshot limit, one query per batch of devices).
  A task is in the view while its device is: tasks of removed devices only for users with all devices.
- **Menus**: `TaskPluginRunnableCache.GetAsync(view)` intersects runnable sets and reasons per group (cached while the
  sets and the view's ids are the same objects).
- **Query**: `TaskPluginQueries.QueryAsync(pluginId, ViewedDevices, method, payload, ICallContext)`; the plugin gets the
  call and, for a group, `ITaskQueryContext.Devices` of the view.
- **Tests**: `tests/Oadm.Core.Tests/DeviceGroups/TaskViewTests` (runs, call, queue race, history, Watch with devices that
  leave and enter, a removed site and new rules, backstop), `Perf/TaskViewScaleTests` (30,000 devices, 50,000 tasks),
  `Perf/TaskQueueCheckScaleTests` (5,000 queued tasks of a group user leave the queue, 501 users), the canary cases of
  TaskService.

## Code (task plugins)

- Restart, Users, Date and time, Upgrade firmware, Applications, Network settings, Assign IP address, Device
  configuration, Set output and the VAPIX Commander set `minSdkVersion` 0.4.0. Their queries implement the call overload
  and read only the queried device; tasks touch only their own device. The host scopes their runs and
  queries (server canary cases of TaskService); `CanaryTests.PluginsConverted` names these packages with that reason
  (a `PluginCanary` over them would test only the fake host). The VAPIX Commander has a page and runs one.
- Address checks: `ITaskQueryContext.ManagedAddressesAsync` (host: `TaskPluginQueries`, every device on the route); Network
  `checkAddresses` names only the view's devices and marks the others `UsedByDevice` ("In use"). It probes addresses only
  on a route the caller's group names (`ICallContext.CoversRoute(siteId)`, SDK 0.4.0: the group's sites, the server's
  network only with "This server"; a device in the group through a tag or a serial does not open its network); otherwise
  it answers from the managed devices only, as without probes. At run time
  `ITaskExecutionContext.ManagedAddressesAsync` (host: the engine's execution context, every other device on the task's
  route, `Core/Tasks/ManagedAddresses`): Network settings and Assign IP address refuse, for every user and before the
  first write, a static address another managed device has ("Another device managed by OADM uses this address. Nothing
  was changed."), so a group user never takes over the address of a device outside the group (whose polling would then
  send that device's login to the new camera). Both checks take at most 256 addresses per call and read only id and address
  of the route's devices (`DeviceRepository.ListAddressesOnRouteAsync`, nothing when no address is an IP address).
- VAPIX Commander: devices, clients and tasks through the call; `save` / `delete` / `import` `RequiresAllDevices`; stored
  passwords of saved commands only with all devices (`ResolveAsync(..., allowSavedSecrets)`). Run VAPIX command and Set
  output are event rule actions and stay as they are (event rules are for users with all devices).
- Device configuration keeps no shared files on the server (files stay on the client), so its rule on saved passwords
  has nothing to cover.

## Code (shared-core)

- **Tags**: `DeviceTagStore.SetDeviceTagsAsync(ViewedDevices, ...)`: the guard (`TagInUseByGroupException`) reads the groups
  inside the BEGIN IMMEDIATE write transaction (a group save during the tagging waits for it) and asks
  `ProtectedTagRegistry` in the same transaction (`ProtectedTagException`, both `TagNeedsAllDevicesException`); `ListAsync(view)` counts the
  view; `TagService` fills `group_names` for users with all devices and audits membership moves (`DeviceGroupMembership`
  with the rules captured before the write).
- **Owners**: `OwnerScopes` (the owner's compiled filter, its sites, `Changed` / `GroupDeleted`, `Sees`, `OwnerOf`),
  `OwnerRules` (only administrators choose the owner; tags of a group's object), `DeviceViewRepository`
  (plugin choices list only the caller's view), `Mapping/OwnerMapping` (`DeviceOwner`).
- **Compliance**: `ComplianceCheck.Owner`, the engine intersects every check with its owner and re-reconciles the checks of
  groups on `OwnerScopes.Changed` (only devices that came in are evaluated), `ComplianceService` takes the caller's view.
  A user without devices saves no check (PERMISSION_DENIED "Your account has no devices.",
  `DeviceGroupMessages.NoDevicesRefusal`, the text the plugins use). A check keeps the device ids it was saved with; the
  replies of a group user (ListChecks, WatchChecks, GetCheck, SaveCheck, SetCheckEnabled, SetEnabled) name only the ids of the user's view now, so a device that left the group is never shown
  and saving the check again works (it then keeps only the group's devices).
- **Event rules**: no owner, no view (decision 2026-10-23): every `EventService` method is `.ForAllDevices()`, the
  engine, the action runner and the event log work as in 4.1.0; the canary counts EventService as closed by the rule.
- **Tests**: `Oadm.Core.Tests/DeviceGroups/TagGuardTests`, `Compliance/ComplianceOwnerTests`, Perf
  `Compliance/ComplianceOwnerScaleTests`, `DeviceGroups/SharedCoreScaleTests`; `Oadm.Server.Tests/DeviceGroups/SharedCore/*`
  (tags, compliance, events: every EventService method refused for a group user, a deleted group changes no rule); the
  canary cases of TagService and ComplianceService.

## Code (client2)

- **Shared texts**: `Oadm.Sdk.Devices.DeviceGroupMessages` (This server, All devices, No devices, AllDevicesOnly,
  ChooseDevices, CannotAdd, `UsedBy`); Core `DeviceGroupTexts`, Server `AddDeviceTexts.CannotAdd` and client
  `Settings/DeviceGroupWords` forward to it.
- **Client**: Event rules hidden for group users (`SyncEventRulesItem`); `UserSession.AddDevicesRefusal` from
  `MyRole.devices` (`RefreshMyDevicesAsync` on connect and when the user's devices change) disables the Add button and
  the empty page's Add devices (`AddToolbarPlugin.FollowRefusal`); the compliance check dialog's Owner field for
  administrators (`ComplianceCheckEditorViewModel.OwnerChoices`, `Settings.DeviceAccessOption`) and the owner on the
  cards (`ComplianceCardViewModel.OwnerText`); "Used by device group ..." in the tag delete and Remove site questions;
  the tagging dialog shows the server's PERMISSION_DENIED text.
- **Fake mode as a group user**: `--fake-user tech2` or `OADM_FAKE_USER=tech2` (`AppOptions.FakeUser`,
  `FakeOadmApi.CurrentUserName`, `FakeOadmApi.GroupUser.cs`): the role's permissions, the group's devices (list and
  Watch), Your role, the group's sites, the tag guard, the add refusal for "guard" (Lobby, single devices only); compliance
  owners and a deleted group turning its checks off.
- **Tests**: `tests/Oadm.Client.Tests/DeviceGroupActionsTests` (+ screenshots).

## Code (page plugins after the security review)

- **Sweep tables** (Action Rule Editor, Edge storage, Image Health Dashboard): `DeviceSweep.SnapshotAsync(caller)` answers
  only rows of devices the caller sees now and drops the others from the table (a tag or site change, before any prune);
  `ReadOneAsync` removes the row of a device no longer in the scope; no rows when the caller's devices cannot be listed.
  What the sweep publishes in the background (the state and its counts) comes from `ScopedSnapshot()`: rows of devices
  no longer in the scope (`IDeviceScopes.Contains(key, deviceId)`, a few hash lookups) are dropped first. A device that
  leaves the scope during a sweep is not read, a read that ends after it left stores no row, and a changed row of such a
  device goes out as removed. `SnapshotAsync(caller)` on the sweep of the caller's scope answers from the same lookups
  (no device list per state read; the list only on hosts without groups); a task end asks `HasRow(id)`.
- **Action Rule Editor**: user calls never use the guarded `ctx.Tasks` and declare no `SystemAccessReason`; Undo asks the
  caller's runner whether the run's tasks ended (`call.Tasks.AnyActiveAsync`: a group user's answer covers the tasks of
  the group's devices, whichever user of the group started them). A task end counts the run down in O(1), whatever the
  run's size; a run whose tasks were cancelled while queued is checked by Undo and forgotten after 6 hours. A
  task is bound to its run before it reads the device: the run of its user's scope, an undo task only for a run Undo
  marked.
- **VAPIX Commander**: a rollout belongs to the user who started it; a task of another user naming its id is refused.
- **Dashboards**: group users never see the device triggers of another owner, also on devices both can see (decision
  2026-10-23, "Shared objects: others are hidden"): an event type names its owner (`EventTypeInfo.OwnerScopeKey`, SDK
  0.4.0; Device triggers fill it, the host fills it for compliance checks) and Dashboards hides the types of another
  owner. Group users see device events with a device list of their devices, OADM's own device status events and
  the site events of their sites; events without a device list from other sources (compliance checks, which carry no
  owner) are hidden, also as watch keys, and so are the device triggers of other owners (all devices or another group),
  also on the group's own devices (`EventTypeInfo.OwnerScopeKey`, SDK 0.4.0, filled by Device triggers). Every push checks
  the group's devices now; a renewal naming a device that left ends the old watch. A group user's watches are bound to one
  job per session (kind `watch`): when the user's devices, role or session change the host ends it and the session's
  watches go at once (no values for the old devices until the next renewal); the session's last unwatch ends it. Two
  watches of a session that start together share the running job of the same scope, and a watch still registering
  counts as busy, so an unwatch meanwhile never ends its job.
- **Saved objects** (`OwnerScope.LeftOwnerAsync`): a user with all devices who saves a group's view or dashboard whose
  devices left the group gets the save without them (`Dropped` in the answer, never stored); devices the save adds outside
  the group and every outside id of a group user stay NOT_FOUND. The page says so below its toolbar in plain secondary
  text (`Notice`), not as an error.
- **Device triggers**: tags are not checked on save (the SDK has no tag catalog); a group's tag covers only the group's
  devices. `get` answers a group user only the stored devices the user sees now; a user with all devices who saves a
  group's trigger whose devices left the group gets the save without them (`SaveReply.Dropped`, `LeftOwnerAsync`). Its
  event types carry the trigger's owner (`OwnerScopeKey`).
- **Edge Recording Export**: a group user's export stream checks the camera's membership every 5 s and ends when it left.
- **Hardening scan**: a device that leaves a group user's devices during the scan gets no stored or published row. One
  scan per device scope, shared by the scope's users: any of them may stop it, whoever started it (found by the caller's
  scope key; a scan of another scope answers like an unknown id).
- **I/O Monitor**: the reading of a device starts detached from the first page's call (it carries no user) and reads
  through the call of one of its pages.

## Stages

1. Device groups (model, storage, compiled predicate), the editors, the user's group, audit; `DeviceView`, the filtered
   device feed and the device id methods; the architecture and canary tests.
2. Lists and counts (tasks, compliance, event log, tags, sites), the waiting list, discovery and the add page; sessions
   ending when a device leaves the view.
3. Shared objects of the core with owning groups: compliance checks (event rules: all devices only).
4. SDK 0.4.0 call context, per-device events and the job registry; every bundled plugin, with its shared objects
   (device triggers, Live View views, dashboards).
5. Perf tests with 30,000 devices, docs, review.
