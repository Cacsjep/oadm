# OADM - Open AXIS Device Management

Open source, cross-platform alternative to AXIS Device Manager (ADM). Users coming from ADM
must feel at home: same workflows, same information, but our own modern dark Fluent look.

- Name: **OADM**, root namespace `Oadm`, folder `odam`.
- License: Apache-2.0 (`LICENSE` at repo root).
- Main branch: `main`.
- Language of UI and code: English.

# Tech Stack

| Area | Choice |
|---|---|
| Runtime | .NET 10 LTS, C# 14 |
| Server | Generic Host worker service + Kestrel, gRPC (Grpc.AspNetCore), EF Core + SQLite |
| Client | Avalonia 12, Fluent theme, dark only, CommunityToolkit.Mvvm |
| Client <-> Server | gRPC with protobuf contracts. Server-streaming for live device and task updates |
| Logging | Serilog (console + rolling file) behind Microsoft.Extensions.Logging |
| Tests | xUnit, NSubstitute for mocks |
| Device API | VAPIX only (HTTP/HTTPS, digest auth). No ONVIF |

Cross-platform is a day-one requirement: Windows, Linux, macOS. No Windows-only APIs
(no DPAPI, no registry, no WMI). Paths via `Path.Combine`, data folder via
`Environment.SpecialFolder.LocalApplicationData/Oadm`. Every PR must build on all three
(CI matrix) and the server must run as a plain console process on all three.

# Packaging

Server and client publish as one self-contained single-file exe per platform
(`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `PublishReadyToRun`), see
`scripts/publish-*`. Plugins ship next to the exe in `plugins/<name>/`.
Native AOT and trimming are deliberately NOT used for the host apps: AOT cannot load managed
plugin assemblies at runtime and trimming removes framework APIs plugins rely on. Small
helper tools without plugin loading may use Native AOT.
Code must never use `Assembly.Location` (empty in single-file); use `AppContext.BaseDirectory`.

# Solution Layout

```
Oadm.sln
src/
  Oadm.Contracts/      protobuf files + generated gRPC stubs, shared enums
  Oadm.Sdk/            plugin SDK: interfaces, attributes, context objects. No Avalonia, no EF
  Oadm.Sdk.Client/     client-side plugin SDK: Avalonia dialog/page base classes, UI context
  Oadm.Core/           domain model, VAPIX client, discovery, task engine, persistence (EF Core)
  Oadm.Server/         host: gRPC services, plugin loader, polling, Serilog setup
  Oadm.Client/         Avalonia app: views, view models, gRPC client, plugin loader
plugins/
  Oadm.Plugins.Restart/   first Task plugin
tests/
  Oadm.Core.Tests/
  Oadm.Server.Tests/
  Oadm.Client.Tests/
docs/                  ADM reference screenshots, protocol notes
```

# Architecture

Two processes, like ADM:

- **Oadm.Server** owns the truth: device table, credentials, task table, discovery, polling,
  plugin execution. Listens on `http://0.0.0.0:5080` (gRPC, h2c) by default, configurable.
  No authentication in Goal 1. The API is designed so a token/auth interceptor can be added
  later without changing the service contracts (auth travels in gRPC metadata, never in
  messages).
- **Oadm.Client** is a thin UI: no device logic, no VAPIX calls. Everything goes through gRPC.
  It subscribes to `DeviceService.Watch` and `TaskService.Watch` streams and keeps an
  in-memory mirror for the grids.

## gRPC services (Oadm.Contracts)

- `DeviceService`: `List`, `Watch` (stream of DeviceChanged events), `Remove`, `Refresh`,
  `SetCredentials`, `GetWebUiUrl`.
- `DiscoveryService`: `StartZeroConf`, `StartRangeScan(from, to)`, `WatchDiscovered` (stream),
  `Stop`.
- `AddDevicesService`: `Prepare(discoveredIds)`, `SetPasswords(...)`, `SetCredentials(...)`,
  `Commit(...)` mirroring the wizard steps below.
- `TaskService`: `ListTaskPlugins` (context-menu entries incl. those contributed by Core
  plugins), `Run(pluginId, deviceIds, payloadJson)`, `List`, `Watch` (stream), `Cancel`,
  `Delete`.
- `PluginService`: `ListCorePlugins` (navigation pages), per-plugin generic
  `Invoke(pluginId, method, payloadJson)` for Core plugin UI pages (later goal).
- `SettingsService`: `Get`, `Set`.

# Data Model (EF Core, SQLite)

- `Device`: Id (Guid), Serial (= MAC, unique, upper hex, no separators), Address,
  UseHostName (bool), HostName, Model (ProdNbr), FirmwareVersion, DhcpEnabled, HttpsEnabled,
  Dot1xEnabled, UpnpFriendlyName, ServerName, Status (enum below), Scheme (http/https),
  CertFingerprintSha256 (nullable), LastSeenUtc, WarrantyExpiry (nullable, later),
  ReplacementModel (nullable, later), Tags.
- `DeviceCredential`: DeviceId, UserName, EncryptedPassword (AES-GCM, see Security).
- `Task`: Id, PluginId, Name, Status (Queued, Running, Done, Failed, Cancelled), Owner
  (client machine/user name), CreatedUtc, StartedUtc, FinishedUtc, Progress (0-100),
  PayloadJson, ScheduledUtc (nullable, unused in Goal 1).
- `TaskDeviceResult`: TaskId, DeviceId, Status, Message, Progress.
- `Setting`: Key, ValueJson.

Device status enum: `Ok`, `Unreachable`, `CredentialsRequired` (401/403), `PasswordNotSet`
(factory default), `CertificateChanged`, `Unknown`.

# Device Discovery

## Zero-conf: mDNS / Bonjour only (Goal 1)

Browse `_axis-video._tcp.local`. Verified on AXIS OS 12.11: the TXT record has exactly one
key `macaddress=<SERIAL>`, the instance name is `<Bonjour.FriendlyName> - <SERIAL>`, SRV
points to port 80 at `axis-<serial lowercase>.local`, A/AAAA gives the address. Ignore
169.254.x.x link-local addresses when a routable one is announced.
Must work on all three OS and on multiple NICs (bind one socket per interface). Runs
continuously while the add wizard is open and once at server start. SSDP and WS-Discovery
are explicitly out of scope for Goal 1.

## IP range scan

For every address in the range (parallelism from settings, default 32): try HTTPS 443 then
HTTP 80, `POST /axis-cgi/basicdeviceinfo.cgi` with
`{"apiVersion":"1.0","method":"getAllProperties"}`.
- `401` with `WWW-Authenticate` realm `AXIS_<serial>` -> Axis device, serial from realm.
- `200` (anonymous allowed) -> parse properties directly.
- Connection refused / timeout (default 1500 ms) -> not a device.
No ICMP, no ARP. Result goes into the same discovered list as mDNS, deduplicated by serial.

# Device Communication (VAPIX)

- Auth: AXIS OS 12 uses Digest on port 80 and Basic on port 443 (seen in
  `/config/rest/virtualhost/v1` and the 401 headers). The client offers Digest only over
  HTTP and Basic or Digest over HTTPS. Basic is never sent over plain HTTP.
- Factory-default check without credentials: `systemready.cgi` returns `needsetup` (yes =
  no admin user yet) and `passphrasepolicy` (none, length, complex).
- Prefer HTTPS, fall back to HTTP if 443 is closed. Store which scheme worked.
- Certificates: trust on first use. Store SHA-256 fingerprint on add. A later mismatch sets
  status `CertificateChanged` and blocks tasks until the user accepts the new certificate.
- Endpoints used in Goal 1:
  - `basicdeviceinfo.cgi` -> SerialNumber, ProdNbr, ProdShortName, Version, HardwareID
  - `param.cgi?action=list&group=Network.BootProto,Network.UPnP.FriendlyName,Network.Interface.I0.dot1x.Enabled,HTTPS.Enabled`
    -> DHCP, UPnP name, 802.1X, HTTPS (`HTTPS.Enabled=yes|no` still exists on 12.x)
  - `pwdgrp.cgi` action add, user root, grp root, sgrp admin:operator:viewer:ptz
    -> first password on a factory-default device. Works over HTTP without credentials, but
    we send it over HTTPS when available and always with arguments in the POST body, never
    in the URL. Since AXIS OS 11.5 the first user does not have to be named root.
  - `restart.cgi` -> Restart task plugin
- All VAPIX access lives in `Oadm.Core.Vapix.VapixClient`, one method per endpoint,
  unit-tested against recorded responses in `tests/.../Fixtures`.

# Add Devices Wizard (clone of ADM)

Toolbar buttons: **Add devices** (zero-conf) and **Add devices from IP range**.

1. **Select devices**: grid of discovered devices with columns Address, Serial (MAC), Model,
   Status. Multi-select, select all, search box. Devices already managed are greyed out.
2. **Host name**: checkbox "Use host name when available, otherwise IP address" (default off).
3. **Set password**: list of selected devices that are factory default (`PasswordNotSet`).
   Fields: new password, confirm. Button **Skip** leaves them without a password.
   Password rules shown inline: 1-64 printable ASCII chars, plus the stricter device
   passphrase policy from systemready when present (length: min 15 chars, complex: min 12
   with mixed character types).
4. **Credentials**: for devices that already have a password: user name, password, checkbox
   "Use these credentials for all selected devices", per-device override possible.
   Wrong credentials do not block adding; the device gets status `CredentialsRequired`.
5. **Review**: table of what will happen per device. **Finish** commits: devices stored,
   credentials encrypted, first full refresh queued as task "Add devices".

Everything in the wizard is driven by the server (`AddDevicesService`); the client only
renders the steps.

# Visual Style

Reference: MultiDrive (Avalonia showcase), `docs/style-reference-multidrive.png`, https://multidrive.io/.
Dark only, calm and spacious, no gradients inside the app.

- Window: starts maximized. Custom title bar (ExtendClientAreaToDecorationsHint), app name
  top-left, window buttons top-right, background near-black `#141414`. Caption button hover
  is a neutral grey slightly lighter than the title bar (never the system blue/accent); the
  close button hovers red like Windows. No connection indicator in the title bar; a banner
  appears only while the server is unreachable.
- Left navigation rail: collapsed by default (icons only, ~56 px, tooltips with the label),
  expand/collapse toggle at the bottom of the rail; expanded ~220 px with icon + label.
  Selected item as rounded pill `#2A2A2A`. Entries top: Devices, then one per Core plugin
  page. Pinned bottom: Logs, Settings, then the expand toggle. No Tasks page, no About page.
- Content in rounded cards: background `#181818` (only slightly lighter than the window),
  corner radius 16, padding 20-24, 16 px gap. The device grid sits in one card, the tasks
  pane in a second card below it, separated by a draggable splitter so the user can resize
  the tasks pane; the split is persisted per client.
- Accent violet `#6C5CE7` (hover `#7D6EF0`), used for selection, progress bars, primary
  buttons and status pills. Secondary accent teal `#7FC8D0`.
- Text: primary `#F2F2F2`, secondary `#9A9A9A` (labels like "Source", column headers).
  Font Inter (Avalonia.Fonts.Inter), 13 px body, 20 px card titles, semibold titles.
- Status shown as small rounded pills (OK green `#3FB27F`, warning amber `#E0A43A`,
  error red `#E5534B`, neutral grey), not plain text.
- Icons: one outline icon set (Fluent System Icons / Lucide geometry), monochrome
  `#CFCFCF`, 16 px. Toolbar uses icon + text buttons, not ADM's icon-only bar.
- All colors and sizes as resources in `Themes/OadmTheme.axaml`; no hard-coded colors in views.

# Main Window: Manage Devices

Reference for content: ADM screenshot `docs/adm-manage-devices.png`. Same information
density, styled as described in Visual Style.

Layout, top to bottom:
1. Title "Devices". Left navigation rail as described in Visual Style.
2. Toolbar: Add devices, Add from IP range, Remove, Refresh, then Task plugin actions that
   declare `ShowInToolbar`, search box right-aligned.
3. Status line: "N devices, M selected".
4. Device grid (virtualized): sortable, column chooser, column order and width persisted per
   client, horizontal scroll, multi-select, right-click context menu with core actions and
   all Task plugins whose `CanRun` is true for the whole selection.
5. Resizable, collapsible bottom pane **Tasks** (no tabs). Columns: Name, Status, Start
   time, Owner, Progress (bar). Buttons: details, cancel, delete, **delete all** (with
   confirmation; running tasks are cancelled first).

**Logs page** (rail, bottom): live client log with level filter and search. Server log
streaming comes later.

Device grid columns, default order:

| Column | Source |
|---|---|
| (icon) | device type icon with status overlay |
| MAC address | SerialNumber from basicdeviceinfo |
| Status | computed, see enum |
| Address | IP or host name, hyperlink opens device web UI in default browser |
| Model | ProdNbr |
| Firmware | Version |
| DHCP | Network.BootProto == dhcp -> Yes/No |
| HTTPS | HTTPS enabled -> Enabled/Disabled |
| Server | OADM server name (hostname by default) |
| IEEE 802.1X | dot1x.Enabled -> Enabled/Disabled |
| UPnP friendly name | Network.UPnP.FriendlyName |
| Warranty expiry | later goal, shows "Not synchronized" |
| Device replacement | later goal, empty |

Polling: server refreshes status (basicdeviceinfo) for every device every **60 s** by
default, user-configurable in Settings. Full parameter refresh on add, on manual Refresh, and
after a task finishes on that device.

# Tasks

- A task targets one or more devices and starts immediately. States: Queued, Running, Done,
  Failed, Cancelled. Per-device result and progress. Persisted, visible in the Tasks tab,
  history kept until the user deletes it.
- Task engine: bounded parallelism per task (default 8 devices at once), cancellation via
  `CancellationToken`, exceptions become `Failed` with message, never crash the server.
- Scheduling, retry and recurrence are out of scope for Goal 1.

# Plugin System

Two kinds of plugins, one packaging format, one loader.

- **Task plugins**: appear in the device context menu and optionally the toolbar. Run
  directly or open a dialog first. Executed on the server per device.
- **Core plugins** (later goals, interfaces defined now): long-running services with their
  own UI page in the navigation rail, e.g. NTP server, DHCP server, IDP. They get full access
  to the device table and VAPIX via `ICorePluginContext`. **A Core plugin can also contribute
  Task plugins**: `ICorePlugin.TaskPlugins` returns the Task plugins it owns. Example: a PKI
  Core plugin runs an internal CA with its own page, and contributes a "Deploy certificate"
  task that issues a cert for the selected cameras and installs it over VAPIX. Likewise an
  NTP Core plugin contributes "Use this NTP server". The loader registers contributed tasks
  exactly like standalone Task plugins, so the context menu does not care where a task came
  from, and a contributed task can reach its owning Core plugin's state via the context.

## SDK (Oadm.Sdk, no UI dependencies)

```csharp
public interface IPlugin
{
    string Id { get; }                 // "oadm.restart", "oadm.ntp"
    string DisplayName { get; }
    string? IconKey { get; }
}

public interface ITaskPlugin : IPlugin
{
    bool ShowInToolbar { get; }
    bool RequiresDialog { get; }       // client opens the matching ITaskPluginDialog first
    bool CanRun(IDeviceInfo device);
    Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);
}

public interface ICorePlugin : IPlugin
{
    IReadOnlyList<ITaskPlugin> TaskPlugins { get; }   // tasks this core plugin contributes
    Task StartAsync(ICorePluginContext ctx, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct); // UI page backend
}

public interface ITaskExecutionContext
{
    IVapixClient Vapix { get; }        // pre-authenticated for the current device
    ILogger Logger { get; }
    ICorePlugin? Owner { get; }        // set when the task was contributed by a Core plugin
    void ReportProgress(int percent, string? message = null);
}

public interface ICorePluginContext
{
    IDeviceRepository Devices { get; }
    IVapixClientFactory Vapix { get; }
    ITaskRunner Tasks { get; }         // core plugins may start tasks themselves
    ISettingsStore Settings { get; }   // namespaced per plugin
    ILogger Logger { get; }
}
```

## Client SDK (Oadm.Sdk.Client)

```csharp
public interface ITaskPluginDialog
{
    string PluginId { get; }
    Task<string?> ShowAsync(IReadOnlyList<IDeviceInfo> devices, Window owner); // payload JSON, null = cancel
}

public interface ICorePluginPage
{
    string PluginId { get; }
    Control CreateView(ICorePluginClientContext ctx);  // page shown in the navigation rail
}
```

Dialogs and pages are real Avalonia views with view models, styled by the host theme.
Plugins never talk to devices from the client; payloads go to the server.

## Loading and packaging

- Server scans `<datafolder>/plugins/*/` plus the solution `plugins/` output on startup,
  loads each `*.Server.dll` in its own collectible `AssemblyLoadContext`, resolves
  `ITaskPlugin` and `ICorePlugin` implementations via reflection, then registers the Core
  plugins' `TaskPlugins` too. Client does the same for `*.Client.dll` with
  `ITaskPluginDialog` and `ICorePluginPage`.
- Plugin folder: `plugin.json` (id, version, minSdkVersion), `<Name>.Server.dll`,
  optional `<Name>.Client.dll`, private dependencies. SDK assemblies are shared from the
  host and never copied into the plugin folder.
- Plugin failures (load or execution) are logged and isolated; a broken plugin never
  prevents server or client from starting.

## First plugin: Restart

`plugins/Oadm.Plugins.Restart`: `ShowInToolbar = true`, `RequiresDialog = false`, calls
`restart.cgi`, then polls basicdeviceinfo until the device answers again (timeout 3 min),
reporting progress. No client assembly needed.

# Settings

Server-side in `Setting`. Goal 1 keys: `Polling.IntervalSeconds` (60),
`Scan.Parallelism` (32), `Scan.TimeoutMs` (1500), `Server.Name` (hostname),
`Server.ListenUrl`. Settings page in the client exposes them. Client-side (local JSON in
LocalApplicationData): server address, grid column layout, bottom pane state.

# Security

- Device passwords: AES-256-GCM, key in `<datafolder>/master.key` (0600 on Unix), random
  nonce per record, stored as `nonce|ciphertext|tag`. OS keyring integration is a later goal.
- Credentials never leave the server; gRPC returns only "has credentials".
- No client authentication in Goal 1. Server binds to all interfaces so a client on another
  machine can connect; README documents this as LAN-only.
- Never log passwords or digest headers.

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
- Tests accompany every non-trivial class. VAPIX parsing tested from recorded fixtures.
  Discovery and task engine tested with fakes, no network in unit tests.
- Commits: conventional commits (`feat:`, `fix:`, `chore:`), small and focused.
- This file is the single source of truth for the spec. When a decision changes, change it
  here in the same PR.

# Goal 1 - Definition of Done

1. `dotnet run --project src/Oadm.Server` starts on Windows, Linux, macOS and creates its
   SQLite database and master key.
2. `dotnet run --project src/Oadm.Client` connects and shows the empty Manage devices window.
3. Add devices via zero-conf (mDNS) and via IP range through the ADM-cloned wizard, including
   setting a password on a factory-default device and entering credentials for others.
4. Added devices appear in the grid with all columns the device provides; status refreshes on
   the configured interval.
5. Right-click or toolbar **Restart** runs the Restart task plugin loaded from the plugins
   folder, with progress visible in the Tasks tab.
6. Remove device, refresh, search and column chooser work.
7. Tests green on CI for all three OS.

Milestones in order: (1) solution skeleton + contracts + CI, (2) VAPIX client + fixtures,
(3) persistence + crypto, (4) discovery, (5) gRPC services + task engine, (6) client shell and
device grid, (7) add wizard, (8) plugin loader + Restart plugin, (9) polish and docs.

# Open Points to Verify on Real Hardware

Resolved on AXIS P3265-V, AXIS OS 12.11.77 (see VAPIX section): HTTPS parameter, first
password over HTTP, mDNS TXT keys. Still open:
- Success response of `pwdgrp.cgi` on a factory-default device (needs a reset camera).
- `restart.cgi` end to end through the Restart plugin.

# Later Goals (not now)

Client authentication and users, SSDP/WS-Discovery, scheduling/retry, Core plugins (NTP,
DHCP, IDP) with their UI pages, firmware upgrade task, backup/restore, certificates, warranty
and replacement data from Axis online services, installers/packaging, localization.

# Resources

- ADM user manual: https://help.axis.com/en-us/axis-device-manager
- VAPIX library: https://developer.axis.com/vapix/
- Reference screenshot: docs/adm-manage-devices.png
