# Plugin system

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Two kinds of plugins, one packaging format, one loader.

- **Task plugins**: appear in the device context menu and optionally the toolbar. Run
  directly or open a dialog first. Executed on the server per device.
- **Core plugins** (first one: Snapshot report): long-running services with their
  own UI page in the navigation rail, e.g. NTP server, DHCP server, IDP. They get full access
  to the device table and VAPIX via `ICorePluginContext`. **A Core plugin can also contribute
  Task plugins**: `ICorePlugin.TaskPlugins` returns the Task plugins it owns. Example: a PKI
  Core plugin runs an internal CA with its own page, and contributes a "Deploy certificate"
  task that issues a cert for the selected cameras and installs it over VAPIX. Likewise an
  NTP Core plugin contributes "Use this NTP server". The loader registers contributed tasks
  exactly like standalone Task plugins, so the context menu does not care where a task came
  from, and a contributed task can reach its owning Core plugin's state via the context.

## SDK (Oadm.Sdk, no UI dependencies)

The interfaces with their members and comments are the code: `src/Oadm.Sdk/Plugins/Plugins.cs`,
`src/Oadm.Sdk/Plugins/TaskSteps.cs`, `src/Oadm.Sdk/Devices/IDeviceInfo.cs`; usage and samples in `plugins/README.md`.
New members are default interface members (DIM), so older plugins keep loading; a DIM that the host does not offer
returns null or throws `NotSupportedException`.

- `IPlugin`: `Id` ("oadm.restart"), `DisplayName`, `IconKey`.
- `IDeviceInfo`: the read-only device view for plugins (server and client): id, serial, address, host name, model,
  firmware, status, category, `HasVideo`, `Apis` (API list of the last full refresh), `CredentialUserName` (never a
  password), certificate end and trust, `DhcpEnabled` / `HttpsEnabled` / `Dot1xEnabled` (null = not known), `Tags`.
- `ITaskPlugin`: `Group` (context menu submenu, `TaskGroups`), `ShowInToolbar`, `RequiresDialog`, `CanRun` (cached data
  only) and `NotSupportedReason` (only when `CanRun` is false: plain language, what is missing and what to do, a device
  detail last as " (this device ...)"; empty or throwing = "Not supported on this device"; helpers `TaskSupportReasons`),
  `MaxParallelDevices` (only lowers the setting), `ShowInMenus` (false: started by its core plugin only), `GetTaskName`
  (see Tasks), `Permissions` (`PermissionDefinition` {Key, Name, Description, OperatorDefault, ViewerDefault,
  FullDeviceControl}: finer rights inside the task, "task:<id>:<key>"; empty = one "task:<id>") and
  `RequiredPermissions(payload)` (the keys one run needs; no answer = all declared), `ExecuteAsync`. Optional
  `ITaskPluginQuery.QueryAsync`: read-only reads for the dialog (`ITaskQueryContext.Devices`: every managed device; for
  a user with a device group only the devices of the user's view). The host calls the overload with the user's call.
  Declared today: Upgrade firmware (upgrade, factoryDefault, downgrade), Users (add, update, remove), Applications (install,
  downgrade, startStop, remove, license), Import configuration (import, connection), Configure devices (configure, dangerous).
- `ICorePlugin`: `TaskPlugins` (contributed tasks), `StartAsync` / `StopAsync`, `InvokeAsync` (the page backend),
  `HasPage` (false: no rail entry), `Group` (`CorePluginGroup`: the rail group), `RequiredRole` / `IsAudited` per method,
  `Permissions` (named rights "<id>:<key>"; "show" is automatic for a page, declared only to change its defaults; empty
  = "manage" for the Admin methods) and `RequiredPermission(method)` (see "Plugin methods" under Security and access).
- `ITaskExecutionContext`: `Vapix` (pre-authenticated for the device), `Logger`, `Owner` (the contributing core plugin),
  `Files` (uploads by id), steps (`PlanSteps`, `BeginStep` -> `ITaskStep` with `ReportProgress`, `Complete`, `Warn`,
  `Skip`, `Fail`; disposing without an end = Done, an escaping exception = Failed; helpers `StepAsync`, `SkipStep`;
  `TaskStepList` is the state machine, `Oadm.Sdk.Testing.StepRun` runs a plugin with the engine's end rules),
  `ReportProgress`, `ReportWarning` (Done with warnings), `Log` (never secrets), `MarkCredentialsInvalid`,
  `UpdateCredentialsAsync`, `CreateClientForAsync` (the device at another address, caller disposes),
  `UpdateDeviceAddressAsync` and `UpdateDeviceTlsAsync` (the server verifies serial and certificate first, else
  `DeviceIdentityException` and the record stays; see "Following moved devices" and the PKI tasks).
- `ICorePluginContext`: `Devices`, `Vapix` (client factory), `Tasks` (start and cancel tasks, `IsActive`: queued or running; `AnyActiveAsync(ids)`, SDK 0.4.0: whether any of
  them is, on `call.Tasks` of a group user for the tasks of the user's devices whoever started them), `Settings` (namespaced per
  plugin), `Records` (`IPluginRecords`, 4.1: the plugin's records in the table PluginRecords, named collections of JSON
  values by key, one row per record, `SetManyAsync` writes many in one transaction; for data that grows with the devices,
  where one settings value would be rewritten whole; `MemoryPluginRecords` for tests and hosts without a database; null
  on older hosts), `Logger`, `PluginDirectory` (never `Assembly.Location`), `Secrets` (AES-256-GCM with the master key),
  `Events` (live events to the plugin's page), `TrustAnchors` (CAs the server trusts when rating device certificates;
  SDK 0.4.0 `SetExpiryWarning(daysFor, defaultDays)`: the days of "expires soon" per device for the device grid and a new
  Compliance check, only PKI's count, docs/spec/pki.md "Code (stage 3)"),
  `EventStreams` (a device's RTSP event stream with the stored credentials; the RTSP port per device), `Firewall` (UDP
  rules, Windows service only; `FirewallRuleKeeper`), `DataDirectory` (`<datafolder>/plugin-data/<id>`: working files, never a
  shared temp folder; the plugin cleans up), `EventPublisher` (event rules), `EventSubscriptions` (every event the server
  handles, see "Event rules"), `AutoAdd` (add or follow an Axis device like
  the add page), `Network` (`IPluginNetwork`: routes, `ListenUdpAsync` / `ListenTcpAsync` on the server's own network,
  both also on the addresses of one interface, UDP with SDK 0.4.0; an address of the interface that is not ready is left
  out while the others serve, `IRelay.BoundAddresses` (SDK 0.4.0 default member, null = the host does not tell: a relay at
  a site, an older host) lists the addresses bound now, empty while not listening; `FakePluginNetwork.LastUdpAddresses` /
  `LastTcpAddresses` and `RefuseAddress` (leave an address out, as the host does) in tests).
- **SDK 0.4.0 (OADM 4.5), device groups** (`docs/spec/device-scope.md`, `src/Oadm.Sdk/Plugins/CallContext.cs`):
  `ICallContext` (UserName, SessionId, DeviceScopeKey "all" or the group id, `Has`, `HasAllDevices`, `Devices`, `Vapix`,
  `Tasks`, `EventStreams` scoped to the user's view: outside ids answer null or throw
  `Oadm.Sdk.Devices.DevicesNotAvailableException` = NOT_FOUND `DeviceMessages.NoLongerAvailable`, `Contains`). Overloads
  with the call, default members forwarding to the old ones: `ICorePlugin.InvokeAsync(caller, method, payload, ct)`,
  `ICorePluginDownloads.OpenDownloadAsync(caller, ...)`, `ITaskPluginQuery.QueryAsync(ctx, caller, device, ...)`.
  `ICorePlugin.RequiresAllDevices(method)` (saves of server-wide services; the host refuses them for group users) and
  `SystemAccessReason(method)` (the few methods that may use `ctx.System` in a group user's call). `ICorePluginContext.System`
  (`ISystemAccess`: every device, for background work; default the context's own members) and `Jobs` (`IPluginJobs`:
  work owned by the user and session that started it, `Find` answers null for another user's or session's id; `StartShared`:
  a job of the owner's device scope, found and stopped by every user of it) and `DeviceScopes` (`IDeviceScopes`:
  `Exists`, `Contains` (a device, or a device id: from the host's group fields of each device, false when unknown),
  `Devices`, `NameOf` per scope key, `Changed`, `InAnyGroup`: objects a plugin keeps per group run only on its
  devices, a deleted group's are off). `ITaskExecutionContext.Caller` (the
  user who started the task, with the user's view as it is when the task leaves the queue; null for tasks the system
  or a core plugin's background work started; host side `Oadm.Core.Tasks.TaskCallContext`, its `Tasks` from
  `TaskEngine.ForView`). `IPluginEvents.Publish(topic, json, PluginEventAudience)` (AllDevices, Device, DeviceRows,
  User, Session; the old `Publish` reaches only watchers with all devices on 4.5). `IDeviceInfo.AddedByAllDevices`.
  `EventTypeInfo.OwnerScopeKey` (the device scope key of the object behind an event type: a device trigger or a compliance
  check of a group, "all" for one of all devices; null = nobody's): pages that list event types to users hide another
  owner's; the host fills it for compliance checks (`DeviceScope.ScopeKey` of the check's owner) and passes the plugins' own.
  Actions get nothing new (event rules are for users with all devices). Client SDK: `HasAllDevices` on
  `ICorePluginClientContext`, `ITaskDialogContext`, `IToolbarContext` (default true). `SdkInfo.Version` 0.4.0,
  `SdkInfo.CallContextVersion`: users with a device group may call only packages whose `minSdkVersion` is at least
  0.4.0; older packages keep working for users with all devices. Test fakes: `Oadm.Sdk.Testing.FakeCallContext`
  (`AllDevices`, `WithGroup`, `Refused`; `Over`, `OverGroup` over a test's own device and VAPIX fakes; task
  `IsActive` / `Cancel` / `AnyActiveAsync` like the host, also over a test's own runner: unknown and ended tasks false; a
  cancelled task of `FakeCoreContext` ends at once, the host's when its run stops), `FakePluginJobs`, `FakeDeviceScopes` (also over a test's own
  device list), `FakeCoreContext.EndTask` (its tasks stay active until then), `FakeCoreContext.CallAsGroup` /
  `CallAsAllDevices`.
- **Host side of the call context** (`src/Oadm.Core/Plugins/PluginCalls.cs`, `CorePluginHost`, `PluginGrpcService`):
  `PluginService` makes a `PluginCaller` (authenticated caller + `DeviceView`) per call; `CorePluginHost.InvokeAsync(caller,
  ...)` / `OpenDownloadAsync(caller, ...)` refuse for a group user before the plugin runs: a package below 0.4.0
  (`PluginOrigin.MinSdkVersion`, `SupportsCallContext`; refused also for Watch and left out of `ListCorePlugins`,
  `DeviceAccessRefusedException.PluginNeedsAllDevices`) and `RequiresAllDevices(method)` (a throwing answer counts as
  true); then they call the plugin's call overload inside a `PluginCallMark` (`CallerContext.PluginCall`, cleared by
  `CallerContext.Suppress`). The call (`HostCallContext`) reads devices from the view, refuses VAPIX clients, task runs
  (whole batch) and event streams (`ViewedEventStreams`: they end when the device leaves the view) outside it with
  `DevicesNotAvailableException`, and lets a group user cancel only tasks started through its own calls (`IsActive` of another user's active task: null, "cannot
tell"; unknown and ended tasks: false for everyone; `AnyActiveAsync` answers for every task of the user's devices, null when one runs on a device outside them). Its `Tasks` is
  `TaskEngine.ForView(view, TaskCaller.Of(caller))`, also in the user's jobs: the caller owns the tasks (the owner text is
  the caller's "user@machine", never what the plugin passes), each task checks the caller's view again when it leaves the
  queue and gets `ITaskExecutionContext.Caller`. The context's
  `Devices`, `Vapix`, `Tasks`, `EventStreams`, `AutoAdd` and `System` are wrapped by `PluginCallGuard`: they refuse
  (`DeviceAccessRefusedException`, PERMISSION_DENIED "Only users with all devices can do this.", logged once per plugin,
  method and member) while the mark or the caller is a user without all devices; `System` works there only in methods
  with a `SystemAccessReason`. A `Task.Run` or timer started in the call keeps the mark and is refused too.
  `ctx.DeviceScopes` is wrapped the same way: in a group user's call (and its jobs) every member refuses a key other than
  the caller's own (`PluginCallMark.ScopeKey`; a repository from `Devices(key)` checks again when read); methods with a
  `SystemAccessReason` reach only `Exists` and `Contains` of every scope (yes or no about devices they have from
  `ctx.System`), never another scope's `Devices` or `NameOf`.
  `CorePluginHost.InvokeAsync(pluginId, ...)` without a caller stays for in-process host work and tests (no user,
  background). Task plugin queries: `TaskPluginQueries.QueryAsync(caller, ...)` (the device through the view, the query
  context's `Devices` = the view, the 0.4.0 rule; `ITaskQueryContext.ManagedAddressesAsync(addresses)`: which candidates a
  managed device on the queried device's route has, over every device, never naming one, for address-in-use checks;
  `ITaskExecutionContext.ManagedAddressesAsync(addresses)`: the same for a running task, the task's own device excluded;
  both at most 256 addresses per call).
  **Jobs**: `PluginJobRegistry` (`ctx.Jobs`): random 128-bit ids, owner
  = the host's call (user id + session); `Find` / `List` answer only the owner in the owner's session and only while the
  caller has the job's device scope; a shared job (`StartShared`, SDK 0.4.0: one scan per scope) answers every caller
  with the job's device scope, so users of the same group (or all users with all devices) follow and stop it; it runs
  with its owner's call but belongs to the scope (lead decision 2026-10-23): the starter's logout, new role or other
  devices leave it running for the scope's other users; it ends only on `Cancel`, when its plugin stops and when the rules
  of its group change (`CancelGroup`). A job started in a call whose user's access, session or
  group changed after the call began is refused ("Your devices changed. Connect again.", `AccessEpochs`: the interceptor
  captures the epoch before it reads the session, `CancelOwner`, `CancelGroup` and `CancelSession` record the change
  before they look, the new job is registered before it is checked; at most 4,096 changes are kept, the older half is
  dropped and becomes a floor below which every capture counts as changed, fail closed); a job runs detached
  (`CallerContext.Suppress`) with the owner's mark, ends when its work ends, on `Cancel`, when its plugin stops and when
  the owner's devices or role change, the rules of the owner's group change (`CancelGroup` on `DeviceGroupStore.Changed`
  and `Deleted`; a new name or description raises only `Updated`) or the session or the user's sessions end (also an
  expired session: `AuthTokenStore.SessionsEnded`); cancelling never throws into the host's events (a plugin callback on
  the token that throws is logged); for a change
  of devices, role, group or session the job is also forgotten (a finished report of the old devices answers NOT_FOUND). When
  devices move into or out of the owner's group (`HostDeviceScopes.Changed`, the coalesced membership signal:
  `ScopeDevicesChanged`) its finished jobs are forgotten at once and its running ones when they end; at most 50 per user and plugin (finished
  ones dropped first, else "Wait until your earlier work has finished."). **Events**: `PluginEventHub.WatchAsync(pluginId,
  PluginWatcher, ct)`; the old `Publish(topic, json)` = `PluginEventAudience.AllDevices`; `Device` / `DeviceRows` are
  left out before they are queued for a group watcher when its view's ids are cached and name none of their devices (so
  events of other devices never push its own out of its 256 places; no read in the publisher), and always checked
  against the watcher's view ids in the watcher's own loop (rows filtered in order; a payload that is not an array
  of one element per id is dropped for group watchers and logged); `User` (name, case-insensitive), `Session`,
  `DeviceScope` (scope key, SDK 0.4.0 addition for state kept per group). **Device scopes**: `HostDeviceScopes`
  (`ctx.DeviceScopes`, over `DeviceGroupStore` + the factory's filters; unknown and deleted keys cover nothing;
  `Changed` without a caller: at once for new rules and a deleted group, and coalesced over one second
  (`HostDeviceScopes.CoalesceWindow`, a `TimeProvider` timer) for the groups whose devices may have changed: the tag and site
  catalog, devices added, removed, moved or tagged; it keeps each device's `DeviceScopeRow`, so an update that changes none
  of those fields (polling) costs one comparison; perf `HostDeviceScopesScaleTests`). Shared sweeps: `plugins/Shared/DeviceSweep.cs` runs its reads detached and has
  `DeviceSweeps<TRow>` (one sweep per scope key, publish with `DeviceScope(key)`, a deleted group's sweep dropped, a
  changed one pruned). Fakes: `FakeCoreContext.InvokeAsync` / `OpenDownloadAsync` / `QueryAsync` apply the same rules
  (`RefusedAccesses`, `ClientsCreated`, `PublishedEvents`, `EventsSeenBy(watcher)`), `PluginCanary` runs every method of a
  plugin with the outside device (plugins/README.md "Call context"). Tests: `tests/Oadm.Core.Tests/Plugins/
  PluginCallContextTests`, `HostDeviceScopesTests`, `PluginEventAudienceTests`, `TaskQueryCallTests`, `PluginCanaryTests`,
  `tests/Oadm.Server.Tests/DeviceGroups/PluginServiceGroupTests`, `tests/Oadm.Plugins.EdgeStorage.Tests/DeviceSweepsTests`.
- Event rules: `IEventSource`, `IDynamicEventSource`, `IEventPublisher`, `IActionPlugin`, `IActionSource` (see "Event
  rules").
- Compliance: `IComplianceCheckType` (standalone in a `*.Server.dll`, or through `IComplianceSource` of a core plugin),
  `IComplianceContext` with shared `Reads`, `ComplianceResult`; `ActionField.File` and `ChoicesFromDevice` for its
  settings (see "Compliance"). Tests: `Oadm.Sdk.Testing.FakeComplianceContext`.

Task plugin names and groups: `DisplayName` is at most `TaskPluginNames.MaxDisplayNameLength` (32) characters and never
ends with "..." (dialog tasks too); it is the menu and toolbar name only, the tasks pane shows `GetTaskName`. The server
registry strips trailing "..." / "…" and shortens longer names to 31 characters + "…" (logged); empty groups become
`General`. Bundled task plugins: Restart, Upgrade firmware, Date and time, Export and Import configuration (Maintenance),
Applications (ACAP) (Applications), Users (Users), Network settings and Assign IP address (Network), the PKI tasks
(Security, contributed by `oadm.pki`); `TaskPluginNamesTests` checks every plugin deployed to `artifacts/plugins`.

## Client SDK (Oadm.Sdk.Client)

The code: `src/Oadm.Sdk.Client/ClientPlugins.cs`, `ToolbarPlugins.cs`; usage in `plugins/README.md`. Everything runs on
the UI thread.

- `ITaskPluginDialog.ShowAsync(ctx, devices, owner)` returns the payload JSON (null = cancel); `ITaskDialogContext` gives
  `QueryAsync` (TaskService.Query), `UploadAsync` (FileService.Upload with progress), `Has(permission)` (the dialog
  hides the choices the role lacks: Users modes, firmware factory default and downgrade, application actions, the
  connection settings area, dangerous parameters), `AllDevices` (every device of the client, e.g. a source device that is
  not selected; DIM empty) and `CanRunTasks` + `RunTaskAsync(deviceIds, payload, progress, ct)` (DIM false / throws): a
  dialog that stays open starts its task itself (TaskService.Run, the tasks show in the tasks pane too) and follows it in
  the client's task mirror (`TaskDialogContext`, `TaskStore`: no extra stream) as `TaskRunState` {DeviceId, TaskId,
  `TaskRunStatus`, Step, Message} until every task ended; it then returns null from `ShowAsync`. Records:
  `IPluginRecords.GetAsync(collection, key)` reads one record (DIM through `ListAsync`; the host and
  `MemoryPluginRecords` read only that row). First user: Certificate
  enrollment (EST). Tests `TaskDialogAndLogTests.DialogContextRunsItsTask...`.
- `ICorePluginPage.CreateView(ctx)`: the rail page; `HasOwnCards` (the view lays out its own cards), `ShowTasksPane` (the
  shared tasks pane below the page). `ICorePluginClientContext`: `InvokeAsync`, `WatchEventsAsync` (PluginService.Watch),
  `Devices` + `DevicesChanged`, `SelectedDevices`, `OwnerName`, `ShowMessageAsync`, `ConfirmAsync`, `OpenAsync`, `Owner`,
  `DeviceWeb` (`IDeviceWebAccess`: a device's web interface for an embedded browser through the server, see "Web View"),
  `LiveVideo` (`ILiveVideoAccess.Create(LiveVideoPlace)` -> `ILiveVideo` {View, Start(device, camera), Close, Camera,
  CameraChosen}: the host's live video component, `Oadm.Client/LiveView/LiveVideoAccess`; the page's videos are disposed
  with the page; see "Live View"), `OpenDownloadAsync(method, payload, ct)` (a file of the server part through
  `PluginService.Download`: `PluginDownload` {Content, FileName, Length}; the page reads the stream and disposes it),
  `Has(permission)` + `PermissionsChanged` (pages hide or lock what the role lacks, e.g. Save of the NTP / DHCP server),
  `GetLocalSetting` / `SetLocalSetting` (short texts a plugin keeps on this computer, `ClientSettings.PluginSettings`,
  at most 64 KB each; e.g. a page's last device choice), `IsPluginOn(packageId)` (from the client's package list, null
  when unknown; Dashboards' empty state), `OpenAsync("plugin:" + id)` opens another plugin's page while it is on.
- **Pages shown as tabs** (SDK 0.4.0, stage 7 of `docs/spec/pki.md`): `ICorePluginPage.TabOf` (DIM null) names the core
  plugin whose page shows this page as a tab. The host then adds no rail entry for it; it creates the view as for a rail
  page (its own `ICorePluginClientContext`, its own plugin id for `InvokeAsync` and events) and hands it to the host page:
  `ICorePluginClientContext.Tabs` (`CorePluginTab` {PluginId, Title, View}, in the server's order, only pages the role may
  show) with `TabsChanged`. `OpenAsync("plugin:" + tab id)` (and a role's start page) opens the host page and raises
  `OpenTabRequested(tab id)` on its context; `OpenAsync("plugin:" + host id)` raises it with the host's own id (its first
  tab). A tab page whose host page is not listed for the user (no "show" permission, host plugin missing) is a rail page
  as before. A window of the host page ("Open in new window") gets its own copies of the tab views; a tab that leaves or
  comes closes the host's windows. Older hosts ignore `TabOf` (every page in the rail). First user: PKI with the 802.1X
  and EST servers (`MainWindowViewModel` `TabPages`, tests `Oadm.Client.Tests` `PluginTabTests`).
- `IToolbarPlugin` (`Id`, `Order`, `Group`: Add, Manage, Tasks, Plugins; `CreateControl` once; `RequiredPermission`:
  the entry hides while the role lacks it) with `IToolbarContext` (selection, devices, task plugins, `CanRunTask` /
  `CannotRunTaskReason`, `RunTaskAsync`, `OpenAsync`, `RemoveDevicesAsync`, `RefreshDevicesAsync`, `QueryAsync`,
  `UploadAsync`, `InvokePluginAsync`, `Has` + `PermissionsChanged`, messages).

### Toolbar plugins

The Devices page toolbar is made of toolbar plugins. Built in (compiled into the client,
`Devices/Toolbar/BuiltInToolbarPlugins.cs`, registered in the container like plugin parts): **Add** (`oadm.toolbar.add`,
group Add, the main one (`IsPrimary`, flat), icon `add`, a `MenuFlyout` with Discovery (`HostPages.AddScan`, icon `search`), Network range
(`AddIpRange`, `range`), Add manually (`AddManually`, `add`), Import from file (`AddImport`, `download`) and Import from ADM
(`AddImportAdm`, `download`): see "Add Devices Page"), Remove (Manage; confirmation, the context menu
uses the same flow), **Refresh** (Manage, order 5, icon `refresh`, enabled with a selection; `IToolbarContext.RefreshDevicesAsync`
= one `DeviceService.Refresh` for the whole selection: the server queues a full refresh per device; the context menu entry
"Refresh" uses the same flow), **Export** (Manage, order 10, icon `export`, `HostPages.ExportDevices`, the selected devices, or every device the search shows when none is selected, the
tooltip says which; `Devices/DeviceListCsv`: header + every grid column as text (MAC address, Status, Address,
Tags, Site, Host name, Model, Firmware, SoC, Architecture, Category, Product type, DHCP, HTTPS, Certificate expires as ISO date yyyy-MM-dd,
Certificate, IEEE 802.1X, Compliance) in the grid's unsorted order, RFC 4180 quoting (`Infrastructure/Csv`), CRLF, UTF-8 with
BOM, a cell starting with = + - @ gets a leading `'` (no formula injection), no password (the client has none);
save picker `IDialogService.SaveFileAsync`, default name `oadm-devices-<yyyy-MM-dd>.csv`; nothing to export or a
failed save = `ui:MessageWindow`; O(n), 5,000 devices tested), one generic plugin with a button per task plugin that declares
`ShowInToolbar` (Tasks; enabled when it can run on the whole selection, otherwise disabled with the reason as tooltip) and **AXIS OS - Release Notes** (Plugins,
order 1000, an icon-only `ui:ToolbarButton` (icon `externalLink`, class `trailing`: right of the toolbar next to Group by
tag and Columns), opens https://help.axis.com/en-us/axis-os-release-notes in the default browser). External ones come from
`*.Client.dll` like dialogs and pages. `DeviceToolbar` orders them by group, order, id, creates each
control once (failures logged, entry left out), keeps a `ToolbarSeparator` only between groups that
show something and moves the same controls into a new page view. Buttons are `ui:ToolbarButton`
(icon key + text; `IsPrimary` marks the main one, still flat). The Columns button and the search box are host parts. Sample:
`tests/TestPlugins/Oadm.TestPlugins.Sample.Client` (deployed to `artifacts/test-plugins/oadm.sample`).
API guide: `plugins/README.md`.

Dialogs and pages are real Avalonia views with view models, styled by the host theme. They use
the shared controls from `Oadm.Sdk.Client.Controls` (`IconLabel`, `SearchBox`, `PasswordBox`, `OadmIcon`,
`DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip`, `FileRow`, `ProgressRow`, `FormField`, `StatCard`,
`ToolbarButton`, `ToolbarSeparator`, `ToolbarOverflowPanel`, `MessageWindow` (every message and confirmation popup:
`MessageWindow.ConfirmAsync` / `ShowMessageAsync`, used by the host dialog service and plugin dialogs);
usage in `plugins/README.md`) and
the theme resources (styles, colors, `Icon.*` geometries) of the host application; the icon keys
are listed in `plugins/README.md`. Plugins never talk to devices from the client; payloads go to
the server. gRPC errors reach the dialog as `RpcException` (Status.Detail is the message).

`VapixRequestOptions.Timeout` (`HttpRequestOptionsKey<TimeSpan>` "Oadm.RequestTimeout") on a
request passed to `IVapixClient.SendAsync` overrides the default 15 s timeout for that request
(firmware and ACAP uploads use minutes; plugins use the SDK constant, never their own key); request
bodies are streamed, never buffered. `VapixRequestOptions.StreamResponse` (`HttpRequestOptionsKey<bool>`
"Oadm.StreamResponse") returns the answer after its headers without buffering it (`HttpCompletionOption.ResponseHeadersRead`):
the caller reads the body as a stream (e.g. into a file in `ICorePluginContext.DataDirectory`), enforces its own size
limit and bounds the body read with its own token (the timeout covers the headers only); the 16 MB limit of buffered
answers is unchanged. Used by the System report for server reports.

## Behaviour changes since OADM 4.1 (SDK 0.4.0)

What a plugin built for 4.1 (SDK 0.3.0) or rebuilt for 4.5 notices:

- `ui:FileRow.IsSingleButton` defaults to true: the button alone, without the "No file chosen" line; set it false for
  the older row.
- `ui:ToolbarButton.IsPrimary` no longer fills the button: it adds the class `main` (toolbars have no filled buttons,
  decision 2026-10-25).
- `IPluginEvents.Publish(topic, payload)` (without an audience) reaches only watchers with all devices; events for users
  with a device group need `Publish(topic, json, PluginEventAudience)`.
- `Oadm.Plugin.Versions.props` sets `OadmMinSdkVersion` 0.4.0: a plugin rebuilt on this SDK writes `minSdkVersion` 0.4.0
  into `plugin.json`, so a 4.1 host refuses it. A plugin that uses no 0.4.0 member may set `OadmMinSdkVersion` 0.3.0 in
  its project to stay loadable on 4.1 (it is then refused for users with a device group).
- `Oadm.Sdk.Client` references `Avalonia.Controls.DataGrid` 12.1.2 (for `IssuedCertificateList`); the host ships it,
  plugins never copy it.
- `SdkInfo.Version` and `SdkInfo.CallContextVersion` are constants: a plugin that reads them gets the value of the SDK
  it was built against, not the host's.

## Loading and packaging

- Server scans `<app>/plugins/*/`, `<datafolder>/plugins/*/` and, in a repository checkout,
  `<repo>/artifacts/plugins/*/` (`PluginPaths.Development`) on startup, loads each
  `*.Server.dll` in its own collectible `AssemblyLoadContext`, resolves `ITaskPlugin` and
  `ICorePlugin` implementations via reflection, then registers the Core plugins' `TaskPlugins`
  too. Client does the same for `*.Client.dll` with `ITaskPluginDialog`, `ICorePluginPage` and
  `IToolbarPlugin` over the same three roots (`ClientPluginLoader.DefaultRoots`).
- Plugin development without installing (developer guide `website/src/content/docs/dev/plugin-sdk.md`: NuGet packages, templates, the `oadm` tool): server
  `--Oadm:PluginDir=<folder>` (several separated by ";", `OadmServerHost.PluginDirKey`) and client `--plugin-dir <folder>`
  (repeatable, `AppOptions.PluginDirs`) load a plugin package straight from a build output, before every other folder, so it
  wins over an installed copy of the same package (the server's later copy is a load error "already loaded from another
  folder", the client skips it); as a service the server ignores the option and logs a warning.
  `--print-folders` (server `Hosting/FolderReport`, client `Plugins/ClientFolderReport`) prints the data, logs and plugin
  folders, the deploy folder and (server) the service name as JSON and exits without opening anything. Every release
  also has the portable `OADM-Server-<version>-<rid>.zip` and `OADM-Client-<version>-<rid>.zip` (the publish folders,
  job `installers`). The `oadm` tool (`tools/Oadm.Tools`) runs them as a development server (`oadm dev start`: own data in
  `.oadm/server-data` of the plugin, `https://localhost:5090`, every `.oadm/plugins/*`, restarted when a build changes the
  files the server loads) and client, deploys into and debugs with the installed OADM (one elevation; the service
  definition is read from the service manager and the folders follow the server's own rules: **an installed program is
  never run to ask**, an older one would start on the live data), packs `.oadmplugin` files and makes license keys.
- Shared assemblies (server: `Oadm.Sdk`, `Oadm.Sdk.Client`, `Microsoft.Extensions.*`; client: also Avalonia, the MVVM
  toolkit, SkiaSharp, HarfBuzzSharp) resolve to the host's copy whatever version a plugin was built against
  (`PluginLoadContext.FromHost`, the client's own copy in `ClientPluginLoader`): `minSdkVersion` decides, so plugins built
  with the NuGet packages of another release load; one the host does not have comes from the plugin folder. Server and
  client load a plugin's main assembly from memory with its pdb (`LoadFromFileWithoutLock`, `LoadWithoutLock`), the client
  also every other assembly of the plugin's own folder (a page's reference to its server part): a build never meets a
  locked file. Windows: a console run on the service's data folder (`oadm debug --installed`) finds the service's TLS key
  in the machine key store and keeps it (`ServerTlsCertificate.OpenWithNamedKey`), never a new certificate.
- Plugin folder: `plugin.json` (id, version, minSdkVersion, displayName, description, optional `enabledByDefault`,
  default true, `alwaysOn`, default false, and `pro`, default false: an OADM PRO plugin, "OADM PRO plugins" below),
  `<Name>.Server.dll`, optional `<Name>.Client.dll`, private dependencies. SDK assemblies are shared from the
  host and never copied into the plugin folder.
- Plugin failures (load or execution) are logged and isolated; a broken plugin never
  prevents server or client from starting.
- A package may hold several core plugins (PKI: EST server, PKI, 802.1X server; the two servers' pages are tabs of PKI's,
  "Pages shown as tabs"): the loader
  registers them by full type name, so the rail keeps one order inside a group; settings, records, data folder, events,
  permissions and services belong to the core plugin id, on/off and licenses to the package.
- **Merged packages** (`Oadm.Core.Plugins.MergedPlugins`, the client's `ClientPluginLoader.MergedPackages`): a plugin folder
  `oadm.est` or `oadm.radius` (OADM 4.1, parts of PKI since 4.5) is never loaded by the server or the client, with one
  log line "... skipped: Merged into PKI" (not a load error; the installers remove such folders). On every start, before
  the stored choices apply, `MergedPlugins.TakeOverAsync` moves an entry of either id in `Plugins.Enabled` to the server's
  own choice (`Plugin:<id>:serverOn`, unless one is stored) and removes it from `Plugins.Enabled` (idempotent).

### Plugins on and off

The rail page **Plugins** lists every plugin package (one plugin folder = one row: its core
plugins, contributed and standalone tasks and client parts together) and administrators turn each on or off; the change
applies at once without a restart. Own rail page (not a Settings card); core functionality
is always on.
- Default: the manifest's `enabledByDefault` (`PluginManifest.EnabledByDefault`, `PluginOrigin.EnabledByDefault`).
  **Off by default: Action Rule Editor, Dashboards, Device triggers, Edge Recording Export, Edge storage, Hardening scan, Image Health Dashboard, I/O Monitor, Live View and Metadata Monitor**, and inside PKI the EST and 802.1X servers (their own Turn on, below). The administrator's choices are the
  server setting `Plugins.Enabled` (JSON object package id -> on; packages without a choice use their default),
  applied before the core plugins start (`Oadm.Server.Plugins.PluginActivation.LoadAsync`).
- **Always on** (manifest `alwaysOn`, `PluginOrigin.AlwaysOn`): Restart, Upgrade firmware, Date and time, Applications
  (ACAP), Users, Network settings, PKI, System report, Device configuration, Outputs and Notifications (core functionality). Enabled whatever
  the stored choice says; `SetEnabled` throws, `SetPackageEnabled` answers FAILED_PRECONDITION "<name> is always on.".
  Switchable: Snapshot report, VAPIX Commander, Metadata Monitor, NTP server, DHCP server, Hardening scan, Image Health
  Dashboard, I/O Monitor, Device triggers, Web View, Live View, Dashboards, Edge Recording Export, Edge storage, Action Rule Editor. Third-party plugins are switchable unless their manifest says otherwise.
- Manifest `description` (`PluginOrigin.Description`): one short sentence shown on the Plugins page; every bundled
  plugin has one (PKI adds a second: "802.1X and EST servers need OADM PRO.").
- **OADM PRO plugins** (manifest `pro`, `PluginOrigin.Pro`; `docs/spec/licensing.md`): listed for everyone, on only while
  OADM PRO is active (`PluginRegistry.ProActive`): `IsEnabled` is the stored choice (or default) **and** an active license,
  so the choice is kept and a new license brings the plugin back. Turning one on without the license: `SetEnabled` throws,
  `SetPackageEnabled` answers FAILED_PRECONDITION `ProLicenseTexts.NeedsPro` ("Needs an OADM PRO license or trial.").
  When the license changes (imported, removed, ran out) `PluginActivation.FollowPro` starts or stops the core plugins.
  Plugins page: the License column shows the chip "OADM PRO" (ok while active, tooltip `NeedsPro` otherwise), the check
  box is locked without the license and **Activate** (administrators) opens Settings with the License card; the client
  reloads the list after it imported or removed the license. No bundled package is a PRO plugin since OADM 4.5.
- **PRO parts of PKI** (`docs/spec/pki.md` "One plugin", `plugins/Oadm.Plugins.Pki/Servers/ServerSwitch.cs`): the EST
  server (`oadm.est`) and the 802.1X server (`oadm.radius`) are core plugins of the always-on PKI package. The core plugins
  always run; each server inside runs, and opens its port, only while its stored choice (plugin setting `serverOn` of its
  core plugin, JSON true / false, none = off) is on **and** `ICorePluginContext.Pro` is active; it follows `Pro.Changed`
  with the kept choice. Page methods `getServer` (everyone with the page), `turnOn` (refused with `NeedsPro` without the
  license) and `turnOff` (Admin, the server's "manage", users with all devices, audited); event topic `server`. Their pages
  show only the Server / General card with the status and **Turn on** while the server does not run, **Turn off** left of
  Save while it runs (`plugins/Oadm.Plugins.Pki.Client/Servers/ServerSwitchView`). The PKI row of the Plugins page has no
  PRO chip; its description says "802.1X and EST servers need OADM PRO.".
- **Plugin services** (`ICorePluginContext.Services`, `IPluginServices`: `Offer<T>`, `Withdraw<T>`, `Find<T>`; server
  `PluginServiceRegistry`): core plugins offer each other services. The contract is an SDK interface (plugins load in
  assembly contexts of their own, only SDK types are shared); one offer per interface (a second plugin's offer throws);
  the host withdraws a plugin's offers when it stops, faults or its package is turned off, so users ask with `Find` each
  time and never keep the result. Offered today: `Oadm.Sdk.Certificates.IDeviceCertificateAuthority` by PKI (the OADM
  CA: `IsReady`, `Chain`, `Changed`, `SignAsync`; OADM's names, never the request's; SHA-1/MD5 requests and weak keys
  refused with `CertificateRequestRefusedException`; device certificates go into PKI's issued registry with their
  `Source`; SDK 0.4.0 `DeviceCertificateRequest.IssuerId` names the CA of PKI's list that signs, an imported CA with its
  key or the OADM CA for null, docs/spec/pki.md "Several CAs and the policy cleanup"). SDK 0.4.0 adds default members for the list of issued certificates and the revocation list (PKI implements
  them; docs/spec/radius-server.md "Certificates and revocation"): `IsRevoked(serial)` (a hash lookup, any notation of
  the serial, `CertificateSerials.Normalize`), `FindRevocation`, `Revocations`, event `RevocationChanged` (it belongs to the
  offer: subscribe again when `Find` returns another instance), `ListIssuedAsync(IssuedCertificateQuery)`,
  `GetIssuedCertificateAsync(serial)` (DER), `RevokeAsync(serials, RevocationReason, userName)`, `PreviousChains` (the
  previous OADM CAs with their chains, DER, for servers that check device certificates: the 802.1X server; PKI reads them
  once per change), `PurposeOf(serial)` (the use of a listed certificate, a lookup in memory); the request's
  `Purpose` (`DeviceCertificatePurposes`). `CertificateDescriber` (SDK) and the client SDK's `CertificateWindow` show a certificate
  in plain names. Test fake: `FakePluginServices` (`FakeCoreContext.FakeServices`).
- **Protected tags** (`ICorePluginContext.ProtectedTags`, `Oadm.Sdk.Devices.IProtectedTags.Protect(names, refusal)`, SDK
  0.4.0; server `ProtectedTagRegistry`): a core plugin whose settings act on devices by tag names those tags; only users
  with all devices then add them to or remove them from devices (docs/spec/tags.md "Protected tags"). Each call replaces
  the plugin's set; `Protect(names, refusal, changeRefusal)` adds the text for a rename or delete, which the host refuses
  for every user while the plugin runs (allowed while it is stopped, faulted or off); `Protect` returns once the tag writes in flight ended; the host keeps the set while the plugin is installed (also stopped, faulted, turned off, PRO lapsed, over a
  restart) and forgets it only when the plugin is removed; a late call of a stopped plugin changes nothing (decision
  2026-10-24, docs/spec/tags.md "Protected tags"). Null on older hosts: the plugin treats its tag rules as unsafe (the 802.1X server's tag
  rules then match nothing). Test fake: `FakeProtectedTags` (`FakeCoreContext.FakeProtectedTags`).
- `PluginRegistry`: `TaskPlugins`, `CorePlugins`, `TryGetTaskPlugin`, `TryGetCorePlugin` see only enabled packages
  (so ListCorePlugins, ListTaskPlugins, Run and Query skip a package that is off; the runnable cache follows the plugin
  set); `AllTaskPlugins` / `AllCorePlugins` / `Packages` (`PluginPackageState`: origin, enabled, has page, menu task
  count) see all. `SetEnabled` + `CorePluginHost.SyncEnabledAsync` (once a change starts, storing it and starting / stopping the plugins
  always finish, also when the caller cancels; a failed store undoes it; the audit entry is written either way): core plugins of a package turned off are stopped
  (trust anchors removed), turned on are started. Invoking a core plugin that is off: FAILED_PRECONDITION "The <name>
  plugin is turned off.". Tasks of the package already queued or running finish.
- gRPC `PluginService.ListPackages` (any user: clients hide toolbar entries of packages that are off) /
  `SetPackageEnabled` (Admin, audited "Turned plugin on" / "Turned plugin off", target the plugin name);
  `PluginPackageInfo.always_on = 8`, `description = 9`, `group = 10` (`PluginPackageState.Group`: the rail group of the
  package's core plugin with a page, else of its first core plugin; without a core plugin "Extensions", like the rail).
- Client: `Plugins/PluginPackageStore` (loaded on every connect; `Changed` reloads the rail pages and the task catalog
  and hides the toolbar entries of packages that are off: `DeviceToolbar.ApplyPackageStates`, the package of a client
  part from the plugin.json next to its dll, `IClientPluginRegistry.PackageOf`). Rail page **Plugins** (Admin only,
  key `HostPages.Plugins`, icon `plugin`, first of the bottom entries with a divider below it; subtitle "A plugin that is off shows no page, menu entry or
  toolbar button."; `Settings/PluginsPageView` + `PluginsViewModel`): one card with a DataGrid On (check box), Plugin,
  Description, grouped like the navigation rail (one collapsible group per rail group that has
  plugins, in rail order, the same look as the device grid's tag groups without color, theme `DataGrid.groups`; sorted by
  name inside a group; the view groups in code-behind with `DataGridCollectionView` + `PluginsViewModel.Groups`);
  always-on packages are not listed; a refused change is undone with the reason below the list. Other clients see a change
  on their next connect. Fake mode: `FakeOadmApi.Plugins.cs` (PKI, Restart, System report always on).
- Tests: `tests/Oadm.Server.Tests/PluginPackageTests` (also the bundled manifests: off by default, always on,
  descriptions; the takeover of the 4.1 choices), `tests/Oadm.Core.Tests/Plugins/MergedPluginsTests` (PKI with three core
  plugins and the ids of 4.1, folders of 4.1 skipped, the takeover from a 4.1 fixture), `tests/Oadm.Client.Tests/PluginListTests`,
  `ShellTests`, `ToolbarPluginTests` (a client folder of 4.1 skipped); the switch: `tests/Oadm.Plugins.Est.Tests/ServerSwitchTests`,
  `tests/Oadm.Plugins.Radius.Tests/PluginTests`.

## Plugin licenses

Commercial plugins (decision 2026-10-17): OADM checks, the plugin decides. User and developer docs:
`website/src/content/docs/dev/licenses.md`, `admin/plugins.mdx` "Licenses".
- **Manifest** `license` (`Oadm.Sdk.Licensing.PluginLicenseManifest`): `publicKey` (base64 SubjectPublicKeyInfo of an ECDSA
  P-256 key), optional `features`, `trialDays` (from the day the server first loaded the package; 0 = none). Without it the
  plugin is free. No buy link.
- **License key** (`LicenseFile`, decision 2026-10-17: one line, no raw content in the UI): `OADM1.<content>.<signature>`,
  base64url; content = JSON {plugin, server, customer, issued, expires|null, features (optional), limits (optional)}; the
  signature (ECDSA P-256, SHA-256) covers the content bytes; spaces and line breaks are ignored; a file (`.oadmlicense`)
  holds the same line; checked offline: signature, plugin id, server ID, last day (server local date). At most 16 KB.
  `LicenseFile.Peek` reads the content unchecked (the dialog's summary).
- **Server** (`Oadm.Core.Plugins.PluginLicenses`, initialized at start step 4a before any plugin runs, re-checked hourly by
  `PluginLicenseRefreshHostedService`): one live `IPluginLicense` per package (`PluginRegistry.Licenses`), handed to
  `ICorePluginContext.License`, `ITaskExecutionContext.License`, `IActionContext.License` (DIM null); `Changed` when the
  state changes. States `LicenseStatus`: NotRequired, Valid, Trial, TrialEnded, Missing, Expired, WrongServer, Invalid;
  `IsValid` = Valid, Trial or NotRequired; `Has(feature)` (empty list = every feature; always during the trial), `Limit(name)`
  (null = none or trial, 0 without a valid license). A stored license that turns invalid while the trial still runs counts
  as Trial. Only a Valid license is stored.
- **gRPC** `PluginService`: `PluginPackageList.server_id = 2`, `PluginPackageInfo.license = 11` (`PluginLicenseState`: status,
  reason, customer, issued, expires, features, limits, trial_ends, available_features); `SetPackageLicense(id, license)` and
  `RemovePackageLicense(id)` (Admin, audited "Entered plugin license" / "Removed plugin license"; INVALID_ARGUMENT with the
  reason, NOT_FOUND, FAILED_PRECONDITION for a free plugin).
- **Client**: Settings page, card License: "Server ID" + copy (`docs/spec/licensing.md`; `ServerSettings.server_id = 23`, read only). Plugins
  page: a **License** column only while a plugin needs a license (no server ID, Settings shows it): status chip
  (Licensed ok, Trial warning, else error; reason as tooltip), Enter / Change license, Remove (red, confirmed); a licensed
  package is listed even when always on. Dialog `Settings/PluginLicenseWindow` (`PluginLicenseViewModel`, single operation):
  server ID + copy, "License key" (one-line text box, mono), its hint = what the key says ("ACME GmbH, until 2027-12-31,
  reports") or where it comes from, Choose file, the format error or the server's refusal below the field.
  `ICorePluginClientContext.LicenseOf(packageId)` for pages. Fake mode: the sample package `example.site-reports` (trial,
  `FakeOadmApi.SampleLicenseKeys`, server ID `FakeOadmApi.FakeServerId`).
- **Vendor tool** `oadm license keys [--out]` (never overwrites a key; prints the project properties `OadmLicensePublicKey`,
  `OadmLicenseFeatures`, `OadmLicenseTrialDays` and the plugin.json entry) and `oadm license sign --key --plugin --server
  --customer [--expires] [--features] [--limit name=n]... [--out]` (`tools/Oadm.Tools`; `manage license ...` passes the
  arguments unchanged).
- Tests: `tests/Oadm.Server.Tests/PluginLicenseTests`, `tests/Oadm.Tools.Tests`, `tests/Oadm.Client.Tests/PluginListTests`.
- **OADM PRO**: `ICorePluginContext.Pro` and `ITaskExecutionContext.Pro` (`IProLicense`, DIM null): the server's OADM PRO
  license for PRO plugins (`docs/spec/licensing.md`); test fake `FakeProLicense`.

## Plugin documentation

Every plugin has its own `README.md` in its folder (`plugins/<name>/README.md`): decisions, page and dialog, steps and
task names, device API and verified device behavior, settings, tests. `docs/spec/bundled-plugins.md` keeps a short entry per plugin: id, what
it does, rail or menu group, on or off by default, the rules that must never break (device safety, secrets, roles,
limits) and a pointer to the README. A new plugin gets both in the same change; a changed decision updates both.
