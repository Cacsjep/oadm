# Device groups (planned)

Status: in work for OADM 4.2 on branch `feature/device-groups` (plan and decisions 2026-10-22, updated 2026-10-23 after 4.1.0; revised after an independent RBAC review and
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
  like a role change (`CallerStreams`).

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
  scan, export and monitor ids); every id-taking method and download checks it; ids of another user answer NOT_FOUND.
- Every bundled plugin moves to the call context before 4.2. Plugins built against SDK 0.3.0 load and run unchanged for
  users with all devices (new SDK members are default interface members); their methods are refused for group users.
- Plugin testing: `Oadm.Sdk.Testing` gets a fake call with a group and the canary test (see Tests).
- **Built (plugin host):** the call per call, the refusals, jobs with owners, event audiences (plus `DeviceScope(key)` for
  state kept per group), `ctx.DeviceScopes`, the sweep per scope and the plugin canary (`PluginCanary`): see
  `docs/spec/plugins.md` "Host side of the call context". A converted bundled plugin sets `minSdkVersion` 0.4.0 in its
  plugin.json and runs a `PluginCanary` in its tests (`CanaryTests.EveryBundledPluginIsConvertedOrListed`).

## Decisions

- **New devices:** a user with a group discovers and adds devices only at the sites of the group, so they land inside
  it; a group of tags or single devices only cannot add devices.
- **Tags:** users with a group tag devices of their group, but never add or remove a tag that any group uses (that would
  move devices between groups). The guard lives in `DeviceTagStore` (the only writer), also for file imports added later
  in the background (the waiting entry remembers who queued it).
- **Shared objects** (event rules, device triggers, compliance checks, Live View views, dashboards): each object stores
  its owning group (or all devices); an administrator editing it keeps the owner; a duplicate gets the group of the user
  who duplicates. Saving refuses device ids and tags outside the owner's view (NOT_FOUND), and it runs only on devices
  of that group (conditions and actions intersected when it fires; "All devices" and the "Any device" counts = the
  group; site events only for the group's sites). Users see and edit only objects of their own group; others are hidden.
  A deleted group disables its objects. The event catalog lists only devices of the user's view. **Test** of an action
  uses only devices of the view.
- **Send HTTP request / Send TCP message** in rules owned by a group: only to addresses on an administrator's allow-list
  (Settings, e.g. the VMS or alarm server); everything else is refused when saving and when sending.
- **Server-wide services** (NTP, DHCP, PKI and EST settings, the VAPIX Commander command library): managing them stays with users who have all devices; users with a group may view the pages, rows filtered to
  their group (by device, address or serial). The role and user editors say so.
- **Server log:** hidden for users with a group (its text names devices). System report, backups, audit log, support
  report: as today (administrator or users with all devices).
- **Sites page:** users with a group see only the sites of their group, with their group's counts.
- **Kept for users with all devices:** tag rename and delete, Delete all of tasks and the event log.
- **Credential list:** never used for users with a group: adding devices, Retry, Log in and the waiting list use only
  the password they type. (A fake camera at their site could otherwise collect every saved credential.)
- **Secrets in shared items** (library commands and configuration files with saved passwords): users with a group may
  run only items without saved passwords.
- **Host names in certificates:** PKI and EST put a device's host name into its certificate only when a user with all
  devices added it (a group user who controls the DNS at a site could otherwise get a certificate for any name). Built:
  `CertificateNames.For(..., trustNames)` and `EstEndpoint.DnsNames` read `IDeviceInfo.AddedByAllDevices` (IP addresses and
  `axis-<serial>.local` only).
- **Membership:** a single-device serial of a removed device is dormant; it counts again only when a user with all
  devices or a user of that group adds the device. Every change of a device's membership (tag, site, relocation, add,
  remove) is audited as "left / entered group X".
- **"Empty means all"** (Refresh, compliance Check now, sweeps, scans, reports): all means the caller's view.
- **Tasks:** task id methods (log, cancel, delete) go through the view; runnable sets are intersected with the view
  before they are encoded; a queued task checks the owner's view again when it leaves the queue.
- **Add and discovery sessions** belong to the user who started them; Retry and Commit take only that user's sessions;
  a found device must be on a route of the group; the network scan and zero-conf on the server's network only for
  groups with "This server"; the waiting list is scoped by site and checks the queuing user's current view.
- **Sessions:** a stream checks the user's group revision like the role right after it registers; a change of a group's
  rules or of a user's group ends every non-session stream of the affected users (like `OnUserRoleChanged`). The cache
  key is (group revision, tag and site catalog revision, `DeviceChangeFeed.Version`). The view never follows the "no
  caller = allow" pattern of today's helpers. Web interface cookies stay on the server.
- **Small leaks closed:** address checks answer only "in use"; conflict and log texts never name devices outside the
  view; discovery rows of such devices say only "already added"; Your role shows no excepted serials. "This server"
  means exactly devices without a site (devices kept from a removed site keep its id).
- **Existence leaks accepted** (documented, not hidden): "already added" on the add page and a duplicate add, the device
  limit message, the license's device count.
- **Release:** 4.2 (4.1.0 shipped the EST server on 2026-10-23), plugin SDK 0.4.0.

## The editors

- **Users page, new card "Device groups"** (administrator): list with name, rules summary ("Site Building B, tag PTZ,
  3 devices, except 1"), device count and users; New, Edit, Delete (red, says which users lose their devices).
- **Group editor:** the shared device picker (`DevicePickerWindow`) extended: left **Sites** (with counts and "This
  server") and **Tags** (with counts); right the searchable, virtualized device list: devices covered by a site or tag
  checked and locked ("Site Building B"), single devices tickable, an "Except" state; a live count ("412 of 4,812
  devices").
- **User form:** a **Devices** field (All devices or a group); **Your role** shows the group and its summary.
- Client code: `Settings/DeviceGroupsViewModel` + `DeviceGroupsCardView`, `DeviceGroupEditorViewModel` +
  `DeviceGroupFormView` (the picker's group mode, see visual-style.md "Shared controls"), `UsersViewModel.DeviceOptions`,
  `MyRoleViewModel.DevicesText`, `UserSession.HasAllDevices` (hides the all-devices-only actions, see security.md);
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
  fields of its methods. The test starts in stage 1 with a list of methods not converted yet; the list shrinks each
  stage and is empty when stage 4 ends.
- **Security tests:** credential list never sent for a group user; deleting a group and restoring an old backup stay
  closed; races (group change during stream registration, view shrinking with queued tasks, group save during tagging);
  job, stream and download ids of user A refused for user B; rule save, Test and Any-device counts within the view;
  empty ids touch only the view; runnable sets, the event catalog and address checks leak no ids or addresses; a
  removed serial does not join a group when re-added by someone outside it.
- Perf tests with 30,000 devices and 50,000 tasks: views, streams, task history paging.

## Code (foundation)

- **Model** (migration `DeviceGroups`): table `DeviceGroups` (`Oadm.Core.DeviceGroups.DeviceGroup`: Name unique
  case-insensitive 1..64, Description 0..200, `ThisServer`, `SiteIds`, `TagIds`, `Serials`, `ExceptSerials` as JSON
  columns, `Revision` from 1, at most 200 groups, 100 sites, 500 tags and 30,000 serials each); `Users.AllDevices` +
  `Users.DeviceGroupId` (FK Restrict); `Devices.AddedByAllDevices` + `AddedByGroupId` (no FK: a deleted group leaves the
  serial dormant); `WaitingDevices.QueuedByUserId`; `EventRules` and `ComplianceChecks` `OwnerAllDevices` +
  `OwnerGroupId` (FK Restrict). The migration sets `AllDevices`, `AddedByAllDevices` and `OwnerAllDevices` to true on
  every existing row; new rows have no database default (C# default false: every writer chooses).
- **Scope value**: `Oadm.Core.Auth.DeviceScope(AllDevices, GroupId)` for users and owners; `None` = no devices. The
  caller: `AuthenticatedCaller.Devices` (`CallerDevices(Access, GroupRevision)`, default `None`), `HasAllDevices`
  (Administrator always), `DeviceGroupId`, `SessionId` (opaque, for plugins). Sessions carry the access
  (`AuthSession.DeviceAccess`, `AuthTokenStore.SetDeviceAccess`).
- **Store**: `DeviceGroupStore` (in memory after `LoadAsync`; `SaveAsync(id?, name, description, rules,
  expectedRevision)`, `DeleteAsync`, `RevisionOf`, `GroupsUsingTag`, `GroupsUsingSite`, `TagIdsInUse`, events `Changed`
  (group id) and `Deleted`). Delete: users to no devices and every `IDeviceGroupOwnedObjects` (core: `CoreOwnedObjects`
  for event rules and compliance checks: owner cleared, turned off) in one transaction, then
  `UserStore.NotifyDeviceAccessChanged`. `UserStore`: `AddAsync`/`CreateAsync` with a `DeviceScope` (a group must exist,
  never "no devices" by choice; the overloads without it give all devices: first administrator, older clients),
  `UpdateAsync(..., DeviceScope? access, ...)`, event `UserDeviceAccessChanged`; texts `DeviceGroupTexts`.
- **Predicate**: `DeviceGroupFilter.Compile` (hash sets; tags by id resolved through `DeviceScopeCatalog` =
  `DeviceTagStore.DefinitionNames` (updated before a write publishes its devices) + the sites that exist
  (`ISiteStates.NameOf`); key `DeviceScopeCatalogKey`). The serial rule covers a device only when
  `AddedByAllDevices` or `AddedByGroupId` is the group.
- **Views**: `DeviceViewFactory.ForAsync(caller)` (throws without a caller), `ForScopeAsync(scope, user)`,
  `ForUserAsync(userId)`, `System` (background work), `CountAsync(rules)`, `CountGroupsAsync`; `DeviceView.ListAsync`,
  `AllAsync`, `GetAsync(ids)` / `GetOrAllAsync(ids)` (any unknown or outside id: `DevicesNotAvailableException`, NOT_FOUND
  `DeviceMessages.NoLongerAvailable(n)`), `FindAsync`, `Contains`, `IdsAsync` (cached per group revision, catalog key and
  `DeviceChangeFeed.Version`), `ScopeKey`. `ViewedDevices` (internal constructor, only `DeviceView` makes it). gRPC:
  `views.ForAsync(ServerCallContext)` (`Oadm.Server.Auth.CallerViews`, the caller from the call's `UserState`,
  `AuthInterceptor.CallerOf`); the interceptor maps `DevicesNotAvailableException` of any service to NOT_FOUND.
- **Access rule**: `AccessRule.AllDevices` (`.ForAllDevices()`): tag rename and delete, task Delete all, event log Clear,
  server log, compliance on / off, server settings Set and the credential list; refused "Only users with all devices can
  do this." (`DeviceGroupTexts.AllDevicesOnly`, `AuthInterceptor.RequireAllDevices` for checks inside a call).
- **Background boundary**: `CallerContext.Suppress`, `RunDetached`, `Detached`, `Raise`: task runs, discovery and login
  work, the retry timer of the local network, and the events of the device and task feeds, tags, users, roles, sessions,
  waiting list, sites, plugins, settings, compliance and event rules raise without the caller.
- **Streams**: `CallerStreams` ends the non-session streams of a user whose access changed and of the users of a group
  whose rules changed; a stream whose devices changed between check and registration ends with UNAVAILABLE "Your
  devices changed. Connect again.".
- **Tests**: `tests/Oadm.Core.Tests/DeviceGroups/*`, `Persistence/DeviceGroupsMigrationTests`;
  `tests/Oadm.Server.Tests/DeviceGroupServiceTests`, `DeviceGroups/ArchitectureTests` (allow-lists with the converting
  package), `DeviceGroups/CanaryTests` (cases, not-converted methods and plugins per package, methods without devices,
  Administrator and all-devices methods closed by the rule).

## Code (add devices)

- `Oadm.Server.AddDevices.AddReach` (from the caller, or from the user who kept a waiting entry as that user is now):
  `Routes` (`Oadm.Core.Discovery.DiscoveryRoutes`: local = "This server", the group's live sites; all for all devices),
  `CanAdd`, `UsesCredentialList` (all devices only), `AddedBy` (the `DeviceScope` a new device stores), `Takes(serial,
  route)` (on a route of the user and inside the group), `Waiting` (`WaitingScope`). `DeviceGroupFilter.ThisServer` and
  `Sites` expose the compiled rules.
- Core discovery sessions take `DiscoveryRoutes` (zero-conf, range scan, address probes); `CoreDiscoveryBackend` passes
  them and finds a discovered device only in its own session. `DiscoverySessionOwners`: the user of each add-page session.
- `DiscoveryAuthenticator`: the credential list only after `AllowCredentialList(session)` (fail closed).
- `DeviceAdder.AddAsync(..., DeviceScope addedBy, ...)` refuses `None`; every add path passes it (Commit: the caller,
  waiting list: the user who kept it, `DeviceAutoAddService`: all devices).
- Membership audit: `Oadm.Core.DeviceGroups.DeviceGroupMembership` (changes per group for adds, moves and removals;
  actions "Entered device group" / "Left device group"; also for tags and sites) and `Oadm.Server.AddDevices.MembershipAudit`
  (Commit, the waiting list, the automatic add, relocation and DHCP moves).
- Waiting list: `WaitingDeviceStore` methods with a `WaitingScope` (list, counts, ids, remove), `QueuedByUserId` written
  by `AddAsync(..., queuedBy, ...)`; `WaitingDeviceGrpcService` through the caller; `WaitingDeviceRunner` per entry.

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

- **Runs**: `TaskGrpcService.Run` takes `view.GetAsync(ids)` and calls `TaskEngine.RunAsync(pluginId, ViewedDevices,
  payload, TaskCaller)`; `TaskEngine.ForView(view, caller)` is the runner of a user's call (`call.Tasks`: outside ids
  refuse the run; cancel and the active check only for the user's own tasks when the user has a group). The raw-id
  `RunAsync` (`ITaskRunner`) is the system runner; a caller with a group in `CallerContext` is refused outside the view
  (backstop, never without `TaskEngine.Views`).
- **Queue check**: each task keeps its `TaskCaller` in memory; leaving the queue it reads the owner's current view
  (`ForUserAsync`) and fails with `DeviceMessages.NoLongerAvailable(1)` when the device is not in it (also a deleted user
  or group), before the device's state is read out or any request is sent. The plugin gets `ITaskExecutionContext.Caller`
  (`TaskCallContext` over that view; event streams not offered there yet).
- **History**: `TaskEngine.ListPageAsync/ListAsync/SnapshotAsync/SubscribeWithSnapshotAsync/GetAsync/GetLogAsync/
  CancelAsync/DeleteAsync(view, ...)`; a user with all devices takes the unfiltered paths. Groups: the view's ids
  (`view.IdsAsync`) filter the store query (`EfTaskStore`: `json_each` over one JSON parameter, then the rows by id);
  `TaskViewSubscription` follows the device feed per watcher (a changed device is checked against the compiled rules).
  A task is in the view while its device is: tasks of removed devices only for users with all devices.
- **Menus**: `TaskPluginRunnableCache.GetAsync(view)` intersects runnable sets and reasons per group (cached while the
  sets and the view's ids are the same objects).
- **Query**: `TaskPluginQueries.QueryAsync(pluginId, ViewedDevices, method, payload, ICallContext)`; the plugin gets the
  call and, for a group, `ITaskQueryContext.Devices` of the view.
- **Tests**: `tests/Oadm.Core.Tests/DeviceGroups/TaskViewTests` (runs, call, queue race, history, Watch, backstop),
  `Perf/TaskViewScaleTests` (30,000 devices, 50,000 tasks), the canary cases of TaskService.

## Code (shared-core)

- **Tags**: `DeviceTagStore.SetDeviceTagsAsync(ViewedDevices, ...)`: the guard (`TagInUseByGroupException`) reads the groups
  inside the BEGIN IMMEDIATE write transaction (a group save during the tagging waits for it); `ListAsync(view)` counts the
  view; `TagService` fills `group_names` for users with all devices and audits membership moves (`DeviceGroupMembership`
  with the rules captured before the write).
- **Owners**: `OwnerScopes` (the owner's compiled filter, its sites, `Changed` / `GroupDeleted`, `Sees`, `OwnerOf`),
  `OwnerRules` (only administrators choose the owner; tags of a group's object), `RuleOwners` (event types an owner may use,
  the allowed addresses), `IEventTypeOwners` (the compliance engine: owners of the check events), `DeviceViewRepository`
  (plugin choices list only the caller's view), `Mapping/OwnerMapping` (`DeviceOwner`).
- **Compliance**: `ComplianceCheck.Owner`, the engine intersects every check with its owner and re-reconciles the checks of
  groups on `OwnerScopes.Changed` (only devices that came in are evaluated), `ComplianceService` takes the caller's view.
- **Event rules**: `StoredRule.Owner`, `RuleEngine` (owner filter in `Selects` and `StateOf`: group counts of "Any
  device" and "Any site offline"), `ActionRunner` (`RuleEdgeJob.Owner`, targets intersected, `IsDestinationAllowed`),
  `EventRulesService` (the view on every method, save checks), `EventLog.ListAsync(scope)` / `EventLogScope`,
  `Core.Settings.ActionDestinations`.
- **Tests**: `Oadm.Core.Tests/DeviceGroups/TagGuardTests`, `Compliance/ComplianceOwnerTests`, `Events/RuleOwnerTests`, Perf
  `Compliance/ComplianceOwnerScaleTests`, `DeviceGroups/SharedCoreScaleTests`; `Oadm.Server.Tests/DeviceGroups/SharedCore/*`
  (tags, compliance, events, settings, server log); `Oadm.Plugins.Notifications.Tests/DestinationTests`; the canary cases of
  TagService, ComplianceService and EventService.

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

## Stages

1. Device groups (model, storage, compiled predicate), the editors, the user's group, audit; `DeviceView`, the filtered
   device feed and the device id methods; the architecture and canary tests.
2. Lists and counts (tasks, compliance, event log, tags, sites), the waiting list, discovery and the add page; sessions
   ending when a device leaves the view.
3. Shared objects of the core with owning groups: event rules, compliance checks.
4. SDK 0.4.0 call context, per-device events and the job registry; every bundled plugin, with its shared objects
   (device triggers, Live View views, dashboards) and the address allow-list of Send HTTP request / Send TCP message.
5. Perf tests with 30,000 devices, docs, review.
