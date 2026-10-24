# OADM - Open AXIS Device Management

Open source, cross-platform alternative to AXIS Device Manager (ADM). Users coming from ADM
must feel at home: same workflows, same information, but our own modern dark Fluent look.

- Name: **OADM**, root namespace `Oadm`, folder `odam`.
- License: Apache-2.0 (`LICENSE` at repo root).
- Main branch: `main`.
- Language of UI and code: English.

Every rule here and in `docs/spec/` is agreed with the maintainer; change one only after asking.

# Tech Stack

| Area | Choice |
|---|---|
| Runtime | .NET 10 LTS, C# 14 |
| Server | Generic Host worker service + Kestrel, gRPC (Grpc.AspNetCore), EF Core + SQLite |
| Client | Avalonia 12, Fluent theme, dark (default) or light, CommunityToolkit.Mvvm |
| Client <-> Server | gRPC with protobuf contracts. Server-streaming for live device and task updates |
| Logging | Serilog (console + rolling file) behind Microsoft.Extensions.Logging; log files roll daily or at 20 MB (server) / 10 MB (client), only the last 14 days and at most 30 files are kept |
| Tests | xUnit, NSubstitute for mocks |
| Device API | VAPIX only (HTTP/HTTPS, digest auth). No ONVIF |
| Live video | RTSP (server, own client) relayed as encoded H.265/H.264 over gRPC, decoded in the client with FFmpeg (LGPL, bundled) |
| Embedded browser | `Avalonia.Controls.WebView` (MIT; WebView2, WKWebView, WPE WebKit / WebKitGTK of the OS), in the client host for the Web View plugin |

Cross-platform is a day-one requirement: Windows, Linux, macOS. No Windows-only APIs
(no DPAPI, no registry, no WMI). Paths via `Path.Combine`, data folder via
`Environment.SpecialFolder.LocalApplicationData/Oadm` unless `--Oadm:DataDir=<folder>` or env
`OADM_DATA_DIR` sets it (the installed services always do, see Packaging). Every release must build on all three
(`release.yml` tests on all three for every release tag `v*.*.*`) and the server must run as a plain console process on all three.

# Solution Layout

```
Oadm.sln
src/
  Oadm.Contracts/      protobuf files + generated gRPC stubs, shared enums
  Oadm.Sdk/            plugin SDK: interfaces, attributes, context objects, Layout/TileLayout (tile grid rules). No Avalonia, no EF
  Oadm.Sdk.Testing/    fakes for plugin tests (NuGet Oadm.Sdk.Testing): StepRun, FakeCamera, FakeDevice, FakeSettings,
                       FakeTaskContext, FakeActionContext, FakeCoreContext
  Oadm.Plugin.Sdk/     MSBuild project SDK of plugins (NuGet Oadm.Plugin.Sdk, Sdk/ + targets/): assembly names, SDK
                       references without copies, plugin.json from properties, the plugin folder; Oadm.Plugin.Repo.* for
                       plugins inside this repository; Oadm.Plugin.Versions.props = the host's versions
  Oadm.Sdk.Client/     client-side plugin SDK: dialog/page/toolbar interfaces, ITaskDialogContext,
                       IToolbarContext, shared controls (Controls/: IconLabel, SearchBox, OadmIcon,
                       DialogTitleBar, CardHeader, DialogFooter, StatusChip, StatCard, FileRow, ProgressRow, LoadingOverlay,
                       ToolbarButton, ToolbarSeparator, ToolbarOverflowPanel, PasswordBox, MessageWindow, FormField, DevicePickerWindow, DeviceIcon,
                       GroupedChoice, DeviceTree, PageTab, TileGrid, PageDeviceChoice, ProgressRing, InfoIcon,
                       WizardSteps, CodeView, CodeEditor, CertificateWindow, IssuedCertificateList, RowActions), LiveVideo (ILiveVideoAccess), DeviceTexts, ServerErrors, PluginEventWatch,
                       form validation (Validation/: ValidatingViewModel, FormValidator), Network/InterfaceSelection,
                       Collections/RangeObservableCollection (bulk list of the host grids and plugin pages)
  Oadm.Core/           domain model, VAPIX client, discovery, task engine, persistence (EF Core)
  Oadm.Server/         host: gRPC services, plugin loader, polling, Serilog setup
  Oadm.Client/         Avalonia app: views, view models, gRPC client, plugin loader
  Oadm.Theme/          the OADM theme (colors of both appearances, icons, OadmTheme.axaml, OadmColors) and the shared
                       TermsWindow, used by the client and the OADM Site Node app
  Oadm.Sites/          the link between server and site node: Kestrel over a connection the node opened,
                       TLS with pinned fingerprints, pairing code and J-PAKE binding
  Oadm.SiteNode/       OADM Site Node service and command line (README with the manual test plan)
  Oadm.SiteNode.Admin/ the node's local channel, texts and parsing shared by service, command line and app
  Oadm.SiteNode.App/   the OADM Site Node app (Avalonia)
plugins/                (layout and SDK guide: plugins/README.md)
  Oadm.Plugins.Restart/   first Task plugin (server only)
  Oadm.Plugins.Firmware(.Client)/         Upgrade firmware
  Oadm.Plugins.Users(.Client)/            Users (accounts on the devices)
  Oadm.Plugins.DateTime(.Client)/         Date and time
  Oadm.Plugins.Network(.Client)/          Network settings, Assign IP address
  Oadm.Plugins.Acap(.Client)/             Applications (ACAP) and their license keys
  Oadm.Plugins.SnapshotReport(.Client)/   first Core plugin: rail page + PDF maintenance report
  Oadm.Plugins.VapixCommander(.Client)/   core plugin: VAPIX command library, raw requests, rollouts
  Oadm.Plugins.NtpServer(.Client)/        core plugin: NTP server (RFC 5905 server mode) + "Use OADM as NTP server"
  Oadm.Plugins.DhcpServer(.Client)/       core plugin: DHCP server (RFC 2131) with static leases and lease list
  Oadm.Plugins.MetadataMonitor(.Client)/  core plugin: live event stream of one camera (RTSP metadata, port of AXIS Metadata Monitor, off by default)
  Oadm.Plugins.Pki(.Client)/              core plugin: PKI (one CA for device certificates, trusted root store); one package with
                                          the core plugins EST server (Est/) and 802.1X server (Radius/), its OADM PRO parts
                                          (off by default, Turn on on their pages, each with its own source-available LICENSE)
  Oadm.Plugins.SystemReport(.Client)/     core plugin without a page: toolbar "System report" (server reports in one ZIP)
  Oadm.Plugins.HardeningScan(.Client)/    core plugin: read-only scan against the AXIS OS hardening guide (off by default)
  Oadm.Plugins.ImageHealth(.Client)/      core plugin: Image Health Dashboard, AXIS Image Health Analytics status of the chosen cameras (off by default)
  Oadm.Plugins.DeviceConfig(.Client)/     Maintenance: Export / Import configuration, Configure devices, Create configuration file
  Oadm.Plugins.Outputs/                   event rule action "Set output" (relays, outputs)
  Oadm.Plugins.Notifications/             event rule actions "Send email", "Send HTTP request" and "Send TCP message"
  Oadm.Plugins.IoMonitor(.Client)/        core plugin: live inputs and outputs of one device (off by default)
  Oadm.Plugins.DeviceTriggers(.Client)/   core plugin: events of the devices for the event rules (WebSocket, off by default)
  Oadm.Plugins.WebView(.Client)/          core plugin: the web interface of the devices in tabs, logged in through the server
  Oadm.Plugins.LiveView(.Client)/         core plugin: saved views of several cameras (a grid of live videos), opened in tabs (off by default)
  Oadm.Plugins.Dashboards(.Client)/       core plugin: live values of events (device triggers) and live video on saved dashboards (off by default)
  Oadm.Plugins.EdgeRecordingExport(.Client)/ core plugin: downloads the recordings on the cameras' SD cards to the client (off by default)
  Oadm.Plugins.EdgeStorage(.Client)/      core plugin: the SD cards of the chosen cameras and their tools (format, check, encryption; off by default)
  Oadm.Plugins.ActionRules(.Client)/      core plugin: Action Rule Editor, the devices' own action rules changed for many at once (off by default)
  Shared/                                 source linked into several plugins (CachedApiCheck, DeviceSweep, SweepRows,
                                          ActionEngine: the devices' action rules over SOAP, DeviceEvents: a device's event list)
  Oadm.Plugins.<Name>/          server part: Oadm.Plugins.<Name>.Server.dll + plugin.json
  Oadm.Plugins.<Name>.Client/   optional Avalonia part: Oadm.Plugins.<Name>.Client.dll
                                (both copy their output to artifacts/plugins/<plugin id>/)
tests/
  Oadm.Core.Tests/
  Oadm.Server.Tests/
  Oadm.Client.Tests/
  Shared/              RecordingFirewall, RecordingStreams, CallWatchers (linked into tests; StepRun moved to src/Oadm.Sdk.Testing)
tools/                 Oadm.DiscoveryProbe (manual discovery tests), Oadm.Notices (THIRD-PARTY-NOTICES.txt generator),
                       Oadm.ApiDocs (SDK reference of the website), Oadm.NewPlugin ("manage new", the templates),
                       Oadm.Tools (the "oadm" command, NuGet Oadm.Tools; "manage license")
samples/               the plugins of the developer tutorials: Oadm.Samples.UpnpName (task with a dialog),
                       Oadm.Samples.FirmwareOverview (core plugin with a page), Oadm.Samples.VirtualInput (event rule
                       action), each with its tests (Oadm.Sdk.Testing); on the plugin project SDK from source (plugin.json
                       from their properties); in Oadm.sln, built and tested by CI, never deployed
                       (samples/Directory.Build.props sets OadmSkipPluginDeploy)
templates/             Oadm.Plugin.Templates.csproj: packs the templates generated from samples/ (not in Oadm.sln)
packaging/             installers (windows/, linux/, macos/), icons/, notices/licenses/ (license text templates)
docs/spec/             the feature specs (see "Feature specs"), part of the spec like this file
```

# Architecture

Two processes, like ADM:

- **Oadm.Server** owns the truth: device table, credentials, task table, discovery, polling,
  plugin execution. Listens on `https://0.0.0.0:5080` (gRPC over TLS) by default, configurable. Clients log in; the
  token travels in gRPC metadata, never in messages (see `docs/spec/security.md`).
- **Oadm.Client** is a thin UI: no device logic, no VAPIX calls. Everything goes through gRPC.
  It subscribes to `DeviceService.Watch` and `TaskService.Watch` streams (with snapshot end marker; the
  task snapshot is limited to `TaskStore.MaxTasks` = 10,000 plus the active tasks) and keeps an
  in-memory mirror for the grids. Stream changes reach the UI thread through a coalescing
  `ChangeBatcher` (one dispatcher post per burst) and are applied to the stores as batches. The rows of a snapshot are
  built on the stream thread (`DeviceStore.Prepare`, `TaskStore.Prepare`); the UI thread only attaches the shared stores
  and swaps them in (30,000 devices: 500 ms less on the UI thread). `TaskPluginCatalog` reads the task list and builds the
  runnable sets on the thread pool; the plugin pages of a connect are created one per UI job below rendering
  (`IUiDispatcher.RunInBackgroundAsync`, a newer list takes over), so the start splash keeps its frames.
- Server change feeds (devices, tasks) deliver every change in order while a watcher keeps up; beyond
  1,024 waiting changes they coalesce per entity (`KeyedCoalescingChannel`: the latest snapshot wins,
  an Added stays Added), so a slow client never loses a final state and never blocks the server.
- gRPC responses are gzip-compressed (live view frames excepted); server `MaxReceiveMessageSize` 16 MB,
  client 32 MB.

# Feature specs

The details of every feature live in `docs/spec/` and are part of the spec like this file. Read the file of the area you
change before you change it, and update it in the same change. One paragraph each here: what it is and the rules that
must never break.

- **[Packaging, CI and website](docs/spec/packaging.md)**: self-contained single-file exes (`PublishSingleFile`,
  `IncludeNativeLibrariesForSelfExtract`, `PublishReadyToRun`), plugins next to each exe, generated
  `THIRD-PARTY-NOTICES.txt`; terms of use accepted on the first client start; installers MSI (Windows x64), .deb
  (amd64, arm64), .pkg (macOS x64) with "Server and client" / "Client only", the OADM Site Node MSI and .deb; one
  workflow `release.yml` on tags `v*.*.*` (tests on three OS, installers, smoke, release, NuGet; a `-rc.N` tag is a
  pre-release); `pr.yml` for pull requests; every Action pinned by hash; the website (Astro Starlight, oadm.app).
  Never Native AOT or trimming for the host apps; never `Assembly.Location`; FFmpeg LGPL builds only.
- **[gRPC services](docs/spec/grpc.md)**: every service and method, its role and errors. Every call needs the token in
  the metadata except `AuthService.Status`, `Login`, `CreateFirstAdmin`; every method has a rule in `AccessPolicy`.
- **[Data model](docs/spec/data-model.md)**: EF Core + SQLite (WAL, `synchronous=NORMAL`), the entities, device statuses
  (`SiteOffline` computed, never stored), categories, certificate trust. Migrations are never deleted or squashed.
- **[Device discovery](docs/spec/discovery.md)**: mDNS (`_axis-video._tcp`) and the IP range scan only (no SSDP, no
  WS-Discovery); following moved devices (by a task, the periodic re-find, a DHCP lease; names never move).
- **[Device communication (VAPIX)](docs/spec/vapix.md)**: all VAPIX in `VapixClient`; Digest over HTTP, Basic only over
  HTTPS (one exception: the add page's verified login); trust on first use with a pinned certificate; every connection
  through `DeviceRouter` on the device's route; banned socket APIs in `src/` and `plugins/`.
- **[Add devices page](docs/spec/add-devices.md)**: one page, no wizard: Discovery, Network range, Add manually, Import
  from file, Import from ADM, the waiting list; automatic login with the credential list after the Axis check, at most
  10 rejected credentials per device, passwords of managed devices never tried on new devices; at most 30,000 devices.
- **[Visual style](docs/spec/visual-style.md)**: dark (and light) Fluent look of MultiDrive, the colors in
  `Oadm.Theme`, the rail, cards, dialogs, status chips, small screens, and the catalogue of shared controls.
- **[Main window](docs/spec/main-window.md)**: the device grid and its columns, toolbar, context menu, tasks pane, Logs
  page, polling (60 s status, 10 min full refresh, spread over the round).
- **[Live view](docs/spec/live-view.md)**: RTSP on the server, frames over gRPC, FFmpeg in the client (GPU decoding
  tested in a separate process first), the shared live video component, zoom, focus, PTZ, RTSP port.
- **[Device tags](docs/spec/tags.md)**, **[Compliance](docs/spec/compliance.md)** (off by default, read only, Not checked
  for devices that are not Ok), **[Event rules](docs/spec/event-rules.md)** (events and actions from plugins, the engine,
  limits, the log), **[Tasks](docs/spec/tasks.md)** (one task per device, named steps, never persisted payloads,
  parallelism per plugin).
- **[Plugin system](docs/spec/plugins.md)**: task and core plugins, SDK and client SDK, loading, plugins on and off,
  licenses, plugin documentation (every plugin has its `README.md`). New SDK members are default interface members.
- **[Bundled plugins](docs/spec/bundled-plugins.md)**: one entry per plugin: id, what it does, on or off by default, the
  rules that must never break; the details in each plugin's README.
- **[Settings and backup](docs/spec/settings-backup.md)**: server settings and the Settings page; encrypted backups
  (`.oadmbackup`) that restore on another computer.
- **[Security and access](docs/spec/security.md)**: AES-256-GCM secrets with the master key, never logged; TLS with a
  pinned server certificate; users, roles and permissions, sessions, the audit log; limits on device answers; the
  service's firewall rules and folder checks.
- **[OADM PRO license](docs/spec/licensing.md)**: a signed license key (the plugin license format, ECDSA P-256) for
  this server's ID; the public keys in the repo, the private keys never; only PRO plugins and the PRO parts of PKI (EST and
  802.1X servers) read it, **OADM itself is never limited**; Settings card License.
- **[EST server](docs/spec/est-server.md)**: a PRO part of PKI (core plugin `oadm.est` in the PKI package, off by default,
  Turn on on its tab of the PKI page, runs only with OADM PRO, its own source-available license): RFC 7030 on TCP 9454, cameras enroll
  with their Axis device ID and only if OADM manages them, the OADM CA of PKI signs, cameras renew by themselves (renewals
  the OADM CA issued directly never show Certificate changed), also at sites (a relay of the site node).
- **[Device groups](docs/spec/device-scope.md)** (OADM 4.5): a user limited to a device group (sites, tags,
  single devices by serial, except); explicit `DeviceView` from the caller, fail closed; plugins get the call explicitly
  (`call.Devices`); shared objects owned by a group; Administrator always all devices.
- **[PKI, the certificate control center](docs/spec/pki.md)** (OADM 4.5): one package with the EST and 802.1X servers
  (their 4.1 folders skipped, "Merged into PKI", their choices taken over once); one rail entry with the tabs Certificates, Policies,
  802.1X server, EST server; every rule about certificates is a PKI policy (issuing, 802.1X, EST, Expiry warning;
  no overlaps; converted once from the old settings), the servers keep no rules of their own; an import adds a CA, any CA
  with its key can issue (the policy's Issued by); the certificate list holds
  every CA and each camera's current certificate per use (a new one replaces the old once confirmed on the camera; the old
  one is then no longer accepted) with create, sign request, import, export and delete; certificates start 5 minutes early and
  the install dialogs ask when the camera's clock is behind.
- **[802.1X server](docs/spec/radius-server.md)** (OADM 4.5, a PRO part of PKI: core plugin `oadm.radius`, off by default,
  Turn on on its tab of the PKI page, runs only with OADM PRO): RADIUS on UDP 1812 with EAP-TLS for the switch ports, only devices OADM
  manages with a certificate of a CA of an 802.1X trusted CA policy pass, fragmentation both ways, a deep session state and plain
  TLS alert texts, switches changed while it runs; its rules (its certificate, TLS, trusted CAs, VLAN, Block) are PKI's policies
  (docs/spec/pki.md), never settings of its own.
- **[Sites](docs/spec/sites.md)**: devices in networks the server cannot reach, through an OADM Site Node that only
  connects out (TCP 5081), bound with a pairing code (J-PAKE); routing, Site offline, the node and its app, plugins at
  sites.

# HARD RULE: device safety for task plugins

Any plugin that changes a device (users, network, firmware, ACAP, ...) must never risk
breaking it:
1. **Compatibility first.** Declare the VAPIX API ids and minimum versions it needs. `CanRun`
   checks them against the cached `device.Apis` (from `apidiscovery.cgi getApiList`, refreshed
   on every full refresh); `ExecuteAsync` re-checks with a fresh `ctx.Vapix.GetApiListAsync()`
   and `Require(...)` BEFORE the first write. A different major version is a different API.
   Unsupported devices fail with `DeviceNotCompatibleException` ("Nothing was changed").
2. **Validate before writing.** Validate all input (addresses, masks, passwords, files)
   before touching the device. Prefer device-side dry runs or validation methods when the API
   offers them.
3. **No lock-out.** Never remove or demote the account OADM itself uses, never apply network
   settings that make the device unreachable without a clear warning in the dialog.
4. **Secrets.** Payloads are kept in memory only, never persisted, never logged.
5. **Read-only queries.** `ITaskPluginQuery` is read-only by contract.
6. **Hardware tests** for write operations are opt-in only (`Category=HardwareWrite`, never in
   `manage test hardware` or `manage test all`), and never run against a device without the developer explicitly asking.

Shared SDK pieces for this: `DeviceApi` + `DeviceApiExtensions.Supports/Require`,
`IVapixClient.GetApiListAsync`, `IDeviceInfo.Apis`, `ITaskPluginQuery` (dialog reads current
state through `ITaskDialogContext.QueryAsync`), `IUploadedFiles` (dialog uploads through
`ITaskDialogContext.UploadAsync`, task reads by id), `ReportWarning` (Done with warnings) and
`Log` (persisted per-task log).

# Wording

HARD RULE for every UI text and every doc. We write for installers who know Axis devices and come from ADM.

- Short and plain. Intros and subtitles: one short sentence, or none. Never explain what a visible control already shows.
- Tooltips only when they add something, at most about 8 words. Never repeat the label.
- No protocol, API or internal names in UI text (mDNS, Bonjour, zero-conf, fwmgr 1.x, stratum, ASCII, passphrase
  policy, full refresh, device record, ports, internal limits). Say what the user can do instead.
- One phrase per concept, everywhere, kept as a shared constant: statuses, refusal and HTTP error texts, the removed
  device, "Default router", "Discovery".
- Only point to actions that exist, by their real name.
- "Optional" once per field. No "(s)" plurals: write the count or rephrase.
- Step names follow one pattern: imperative, with the article ("Wait for the device to come back").
- No filler: no "easily", "seamlessly", "simply", "this allows you to", "please note", em-dash asides, "not X but Y".
- Docs use the same style. README and user docs are for users. Developer docs (CONTRIBUTING, plugins/README, plugin
  READMEs, specs) keep the technical detail developers need, in short sentences, without repetition. TERMS.md is fixed
  by the user and is never edited.

# Coding Rules

- `Directory.Build.props`: Nullable enable, ImplicitUsings enable, TreatWarningsAsErrors,
  LangVersion latest, analyzers on. `.editorconfig`: file-scoped namespaces, 4 spaces,
  `var` when the type is obvious.
- Async all the way, `CancellationToken` on every I/O method, no `.Result` / `.Wait()`.
- MVVM with CommunityToolkit (`[ObservableProperty]`, `[RelayCommand]`). No logic in
  code-behind. View models testable without Avalonia.
- Dependency injection everywhere (Microsoft.Extensions.DependencyInjection), also in the
  client.
- Organize by feature (`Devices/`, `Discovery/`, `Tasks/`, `Plugins/`), not by layer.
- **HARD RULE, reuse UI components.** Anything that appears in more than one place is one
  shared control, never a copy: in `src/Oadm.Sdk.Client/Controls` (namespace
  `Oadm.Sdk.Client.Controls`, XAML prefix `ui`) when plugin dialogs need it too, else in
  `src/Oadm.Client/Controls`. The catalogue of the shared controls (what each is for, where it is used) is in
  `docs/spec/visual-style.md` "Shared controls". Before
  writing new XAML, check both `Controls/` folders and reuse; if a second place needs something
  that exists only inline, extract it into a control first.
- Plugin projects copy their output to `artifacts/plugins/<id>/` after every build. A running
  client or server keeps those files open; build or test with `-p:OadmSkipPluginDeploy=true` (e.g.
  together with `--artifacts-path`) to skip the copy while the apps run.
- **HARD RULE, validation errors directly below the field.** On every input of the client and of every plugin, a
  validation error appears directly below that input, like Vuetify: small red text (`Oadm.StatusErrorBrush`,
  `Oadm.FontSizeSmall`) aligned with the input's left edge plus a red input border, no space reserved without an
  error, the label stays level with the input. Never collected elsewhere: no error lists at the bottom of dialogs, no
  summary lines under cards (row errors stay in the row's Status column, a whole-table error directly below the
  table). Labeled rows are `ui:FormField` (label, input, hint or error below the input); the error itself is Avalonia's
  `DataValidationErrors` styled in `OadmTheme.axaml` from `INotifyDataErrorInfo`. Form view models derive from
  `Oadm.Sdk.Client.Validation.ValidatingViewModel` (rules per property on its `FormValidator`: `Rule`, `Rules`,
  `ShowAll`, `Reset`, `SetServerError`); errors show once the field was edited, the user left it (`FormField` on lost focus tells
  `IFormFieldVisits.FieldLeft` the bound property, an empty required field says so) or a submit was
  tried, never on an untouched form; submit buttons stay disabled while any error exists and their tooltip says why. Usage:
  `plugins/README.md` "Validation".
- **HARD RULE, loading feedback.** Everything that can take longer than 1 s at 30,000 devices shows that it is working
  from the first moment: a page or dialog that waits for a read `ui:LoadingOverlay` (ring and text, Cancel stays usable),
  a list or card that reloads `ui:ProgressRow` or a `ui:ProgressRing` in place, a button that started the work disabled
  while its row shows progress. The window never freezes without one: work that would block the UI thread for longer
  runs elsewhere or shows the ring first (the device grid: `DevicesView.RunBusyAsync`, a ring over the grid before a
  sort or regroup of a large selection). A new feature checks its waits at 30,000 devices.
- **HARD RULE, scale to thousands of devices.** A server manages up to 30,000 devices. Every list of
  devices, discovered devices, tasks, sources or tiles is virtualized (DataGrid, virtualizing
  ListBox/ItemsRepeater; never an ItemsControl/StackPanel creating one control per item), search,
  filter, sort and select-all are O(n) without per-item UI work, server calls are batched (one call
  for many devices, bounded parallelism on the server), per-device work is lazy for visible rows
  or summarized ("4,812 compatible, 188 missing API"), and images/snapshots load only for visible
  tiles. Every feature with a device list has a test with at least 30,000 fake devices (the device limit) that keeps
  filtering/selection/summary fast. Target size: **30,000 devices and 50,000 tasks in the history**.
  Concretely:
  - Collections bound to a grid are `RangeObservableCollection` (`Oadm.Sdk.Client.Collections`, plugin pages too): a
    filter, select-all, snapshot or batch is one `ReplaceAll`/`AddRange`/`InsertRange`/`RemoveAll`
    (one Reset), never one event per item. `GridSelection` hands a multi-item selection change to the
    view model collection in one step (`IResettableList`).
  - No `List.Contains`/`FirstOrDefault`/`Count(predicate)` over all rows inside a per-row loop or a
    per-event handler: index by id (`Dictionary`, `HashSet`) and keep counters up to date per change
    (add page summary, `TaskStore.ActiveCount`).
  - Stores apply a change in O(1) (`DeviceStore.ApplyBatch`, `TaskStore.ApplyBatch`), raise one
    `Changed` per batch with the affected ids, and an update that changes nothing raises nothing.
    Derived data follows only the affected ids (task device labels by device id).
  - Server-to-UI streams go through `ChangeBatcher` (one dispatcher post per burst).
  - A plugin page that reads or changes devices starts from the user's choice (`PageDeviceChoice` first in its toolbar,
    nothing read before a choice; one device: `DevicePickerWindow.ShowOneAsync`), never from every device by itself
    (`plugins/README.md` "Choose devices first").
  - Server: per-device work has bounded parallelism and is spread over time (polling); writes for many
    devices or tasks are one transaction (`RemoveManyAsync`, `SetManyAsync`, `AddRangeAsync`,
    `DeleteManyAsync`); lists that can grow without bound are paged or limited (task history, Watch
    snapshot) and have a retention; derived per-device results are cached per device-table version.
  - gRPC: never one message whose size grows with the device or task count when a stream or a page
    can carry it; sets of ids are sent in their shorter form (compact runnable sets).
  - Tests: `[Trait("Category", "Perf")]` for scale tests that take longer than about a second; they run
    with `manage test perf` (not in `manage test unit`), each with a generous but meaningful time
    budget, and write their measured times to the test output.
  - **HARD RULE, tests that never flake** (decision 2026-10-17: the former `Timing` category and the CI retry are gone;
    every unit test runs in CI once). A test never depends on how fast the machine is or what runs next to it:
    - Waiting for another thread: `Oadm.Sdk.Testing.Eventually.TrueAsync(condition, "what")` (checks every 10 ms, 30 s
      ceiling, returns as soon as it holds), never a fixed `Task.Delay` / `Thread.Sleep` before an assert. "Nothing else
      happens" checks wait first for a positive sign that the work ended; `Eventually.StaysTrueAsync` only when none exists.
    - Time in the code (timeouts, intervals, retries, debounce): a `TimeProvider`, and the test passes a
      `FakeTimeProvider` and advances it. Real timeouts in tests are generous (30 s); an upper bound only proves "it did not
      hang". Concurrency limits are tested with gates that hold the work, not with overlapping delays.
    - Speed is checked only in `Perf` tests (a budget about 10x the measured time, the measured time in the message);
      the functional part of such a test is a normal test.
    - Shared state: static knobs are restored after the test (better: per-instance settings); tests that set process-wide
      environment variables share one collection with `DisableParallelization`; one Avalonia headless session per test
      process (`Oadm.Client.Tests` runs without test parallelization: Avalonia's UI dispatcher is process-wide); temp
      folders, ports and file names are unique per test; no comparison with the wall clock that breaks at midnight.
    - The repository is found through `tests/Shared/TestRepository.cs` (the `RepositoryRoot` assembly attribute of
      `tests/Directory.Build.props`), never by walking up from the build output.
- Core plugin pages never repeat the page title in a card heading: the description goes into
  the host page header via `ui:PageHeader.Subtitle` (2 px top margin, theme); the status never sits top right in the page
  header but directly left of the Save button it reports on (or next to the value it describes, e.g. the CA validity on the
  PKI page); the card starts with
  the form. User-facing texts use plain language (no protocol jargon such as stratum, DISCOVER/OFFER).
- Dialogs with a single operation (one form: back up, import, generate, static lease, export) have no card: the form sits
  in `Border.dialogBody` (theme, padded like `ui:MessageWindow`) between title bar and footer, and the window sizes to its
  content (`SizeToContent="Height"`, fixed width). Cards only in dialogs with several sections or a list.
- Button and menu labels never end with "..." / "…", also when they open a dialog or a file picker ("Back up", "Import CA",
  "Choose file"). Actions inside a card are filled buttons (`Button.secondary` with `ui:IconLabel`, primary for the main
  one); the flat `ui:ToolbarButton` is only for toolbars, also the header row of a card that holds a table (PKI's
  Policies and certificate cards, like the device toolbar: the main one `IsPrimary`; decision 2026-10-25).
- One accent button per view (exception, the purple device choice button: "Selected devices" of the event rule and
  trigger dialogs and of VAPIX Commander's target devices, "Selected cameras" of Edge Recording Export): while an inline editor is open, its button (Retry, Create tag) is the primary one and the
  footer's main button turns secondary. A disabled primary keeps its role: the accent at about 35 % with dimmed text.
- **Destructive actions are red**: `Button.danger` (red fill, white text) instead of primary for
  buttons that delete or remove (Remove users, Delete certificates), `Button.toolbar.danger` for flat toolbar and icon
  buttons (Delete all, Delete tag, credential Remove, user Delete), `Button.link.danger` for row links (DHCP Delete and
  Release, PKI Remove). `ui:MessageWindow` makes its confirm button red when the confirm text starts with Delete, Remove
  or Release (`MessageWindow.IsDestructive`, `IsDanger` overrides). Destructive entries sit apart from everyday ones
  (separator, last in a menu).
- `ui:DialogFooter` with no visible action puts its lone Close / Cancel on the right.
- Grids whose status text explains a problem (add page, address tables, firmware, certificate install, system report,
  hardening detail) have the class `wrapRows`: rows grow and `ui:StatusChip` shows up to two lines (`MaxLines`).
  Numeric columns (days, offsets, scores, durations) are right-aligned: `CellStyleClasses="number"` plus a header
  `TextBlock.numberHeader`. Empty lists say in one line what to do first.
- **HARD RULE, no style differences.** Same kind of element, same look, everywhere: one
  style per element type in `src/Oadm.Theme/OadmTheme.axaml`, no local overrides of font size,
  weight, color, padding or alignment in views. No special cases such as a bold selected rail
  item or a greyed toggle unless the spec says so. When a mismatch is found, fix the shared
  style or control, not the single view.
- Tests never assert exact positions, sizes, pixel offsets or column widths of controls (overkill); headless screenshots are for looking, assertions check behaviour, content and rules.
- UI is verified only with Avalonia headless tests rendering offscreen (screenshots via
  `OADM_SCREENSHOT_DIR`). Never automate the real desktop: no simulated clicks or drags, no
  capturing real windows on a developer machine.
- Tests accompany every non-trivial class. VAPIX parsing tested from recorded fixtures; RTP
  depacketizers from recorded RTP (`tests/Oadm.Core.Tests/Fixtures/LiveView`, re-record with
  `RtpRecorderTests` and `OADM_RECORD_RTP_DIR`).
  Discovery and task engine tested with fakes, no network in unit tests.
- **EF migrations are never deleted or squashed**: a backup of any older OADM is restored by running them (see "Backup
  and restore"); a schema change is always a new migration.
- Commits: conventional commits (`feat:`, `fix:`, `chore:`), small and focused. No `Co-Authored-By` or `Claude-Session`
  lines in commit messages.
- This file and `docs/spec/` are the single source of truth for the spec. When a decision changes, change it there in
  the same change; a new feature gets its spec file and a line in "Feature specs".

# Not built yet

SSDP and WS-Discovery, scheduled and repeated tasks (retry, recurrence), warranty and replacement data
from Axis online services, localization, installer signing (checksums instead), OS keyring for the master key,
least-privilege service accounts.

# Resources

- ADM user manual: https://help.axis.com/en-us/axis-device-manager
- VAPIX library: https://developer.axis.com/vapix/
