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
| Logging | Serilog (console + rolling file) behind Microsoft.Extensions.Logging; log files roll daily or at 20 MB (server) / 10 MB (client), only the last 14 days and at most 30 files are kept |
| Tests | xUnit, NSubstitute for mocks |
| Device API | VAPIX only (HTTP/HTTPS, digest auth). No ONVIF |
| Live video | RTSP (server, own client) relayed as encoded H.265/H.264 over gRPC, decoded in the client with FFmpeg (LGPL, bundled) |

Cross-platform is a day-one requirement: Windows, Linux, macOS. No Windows-only APIs
(no DPAPI, no registry, no WMI). Paths via `Path.Combine`, data folder via
`Environment.SpecialFolder.LocalApplicationData/Oadm` unless `--Oadm:DataDir=<folder>` or env
`OADM_DATA_DIR` sets it (the installed services always do, see Packaging). Every release must build on all three
(CI matrix: `ci.yml` and `package.yml` run only for release tags `v*.*.*`, user decision 2026-10-08) and the server must run as a plain console process on all three.

# Packaging

Server and client publish as one self-contained single-file exe per platform
(`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `PublishReadyToRun`), see
`manage publish`. Every plugin project is published once into `artifacts/publish/plugins/<plugin id>/`
(`<OadmPluginId>`, server and client part together) and copied next to both exes in
`plugins/<plugin id>/`, so a new plugin project is packaged without further changes.
`--version` (default `0.1.0-dev`, a leading `v` is removed) goes to every assembly (`-p:Version`).
After the publish `manage publish` runs `tools/Oadm.Notices` (generator, never shipped): it reads the published
`*.deps.json` files (server and client from `obj/Release/<tfm>/<rid>/`, every plugin folder) and writes
`THIRD-PARTY-NOTICES.txt` next to each exe: the hand-written `THIRD-PARTY-NOTICES.md` (FFmpeg, Inter, .NET), every NuGet
package and runtime pack with license, copyright, project and where it is used (server, client, plugin x), the full
license texts from `packaging/notices/licenses/` (MIT, Apache-2.0, BSD-2/3-Clause, OFL-1.1, LGPL-2.1; Apache-2.0,
LGPL-2.1 and OFL-1.1 always) and the license and notice files the packages ship (root `LICENSE*`/`NOTICE*`/
`THIRD-PARTY-NOTICES*`, the nuspec license file, `legal/**`), deduplicated by content; a license without a text is a
warning. `LICENSE.txt`, `LGPL-2.1.txt` and `TERMS.md` are written next to each exe too.
**Terms of use** (`/TERMS.md`, user decision 2026-10-08: plain-language disclaimer, "as is", no liability for device
changes, not affiliated with Axis; text fixed by the user) come before the license everywhere the license is shown:
MSI license page (`packaging/windows/License.rtf` = TERMS.md then LICENSE, built by `make-ui-assets.py`, accepted as
before), .pkg license page (productbuild `<license>` from `--resources`, TERMS.md then LICENSE), .deb
`/usr/share/doc/<package>/TERMS.md` (no interactive acceptance), the top of the client's About page
(embedded resource `Oadm.Client.TERMS.md`), and every publish folder / installed app folder.

Installers (`manage package <windows|linux|macos> [--rid] [--version]` into `artifacts/packages/`; layout
table and notes in `packaging/README.md`). Every installer offers **"Server and client"** (default) or **"Client
only"** and ships `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `LGPL-2.1.txt` with each app. CI is one workflow,
`.github/workflows/release.yml`, only on release tags `v*.*.*` (e.g. `v0.0.1`): job `tests` (Windows, Linux, macOS,
`dotnet test` with the unit filter and `--blame-hang-timeout 5m`, 30 min timeout) -> job `installers` (needs tests;
builds the three installers; installs, checks and removes the MSI and the .deb packages as "Server and client" and as
"Client only"; checks the .pkg's choices and payload without installing) -> job `release` (needs both; the GitHub
release of the tag with the installers and `SHA256SUMS.txt`):
- **Windows MSI** (WiX Toolset 6 via the `WixToolset.Sdk` MSBuild SDK, `packaging/windows/`, not in
  Oadm.sln; WiX 7 not used, it requires the OSMF EULA), x64 only (no ARM, user decision 2026-10-08), per machine: `Program Files\OADM\Server`
  and `\Client`, Windows service "OADM Server" (`OadmServer`, automatic, LocalSystem, restart on failure,
  `--Oadm:DataDir="%ProgramData%\OADM"`), Start menu shortcut, one firewall rule TCP 5080 for `Oadm.Server.exe`, profiles
  Domain and Private (`Profile="[OADM_FW_PROFILE]"` = 3), fixed UpgradeCode + MajorUpgrade (same version reinstall
  allowed). Features `Client` (always) and `Server` (service, firewall rule, data folder); own UI `WixUI_Oadm`
  (welcome, license, page "Choose what to install" with the radio buttons "Server and client" / "Client only" =
  AddLocal / Remove of `Server`, ready, progress, finish; Change in Apps and Features opens the same page, upgrades keep
  the choice through MigrateFeatureStates); silent `ADDLOCAL=Client` = client only. `%ProgramData%\OADM` is
  created for SYSTEM and Administrators only and never removed. Files are harvested from the publish folders.
- **Linux .deb** (`dpkg-deb`, `packaging/linux/build-deb.sh`, amd64 only, built on Linux or in a container), three
  packages: `oadm-server` (`/opt/oadm/server`, systemd unit `oadm-server.service` (root, `Type=notify`,
  `Restart=on-failure`, `OADM_DATA_DIR=/var/lib/oadm`, StateDirectory 0700, optional `/etc/default/oadm-server`),
  maintainer scripts: postinst enables and starts it like dh_installsystemd and works without systemd; prerm stops it;
  data kept even on purge), `oadm-client` (`/opt/oadm/client`, `/usr/bin/oadm-client`, `oadm.desktop` + hicolor icon,
  X11 libraries as Depends) and the metapackage `oadm` (depends on both, same version) = "Server and client";
  `oadm-client` alone = "Client only". Both take over the files of the former single package `oadm`
  (`Replaces`/`Breaks: oadm (<< 0.0.3~)`). Each has `/usr/share/doc/<package>/{copyright,THIRD-PARTY-NOTICES.txt,
  LGPL-2.1.txt}`. Pre-release versions are written `1.2.0~rc.1`. No .rpm.
- **macOS .pkg** (`pkgbuild` + `productbuild`, `packaging/macos/build-pkg.sh`, x64 (Intel) only, built on macOS
  only): `/Applications/OADM.app` (Info.plist, icns, license texts and plugins in `Contents/Resources`), server in
  `/Library/Application Support/OADM/server`, LaunchDaemon `com.oadm.server` (root, `OADM_DATA_DIR=/Library/Application
  Support/OADM`, `OADM_SERVICE=launchd`, folder 0700), `uninstall-oadm.sh` next to the server and in the app's
  Resources. Choices (`customize="allow"`): `com.oadm.choice.client` "OADM client" (always) and
  `com.oadm.choice.server` "OADM server" (LaunchDaemon + server; uncheck for "Client only", or
  `installer -applyChoiceChangesXML`). Unsigned (executables ad hoc signed); Developer ID signing and notarization are a
  later step.
- The server runs as root / LocalSystem (NTP and DHCP plugins need UDP 123 and 67). As a service the host
  integrates with the SCM and systemd (`Oadm.Server.Hosting.ServiceHosting`: `AddWindowsService`, `AddSystemd`,
  content root = app folder; no-ops on a console). `ServiceHosting.IsRunningAsService()` (Windows service, systemd, or
  macOS with `OADM_SERVICE=launchd`; `OadmServerHostOptions.ServiceMode` overrides it, tests) switches on the service
  hardening of "Production hardening / 5": folder checks (`FolderGuard`), netsh firewall rules for plugins, no
  development plugin folder. Services set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a folder
  only the service account can write (`%ProgramData%\OADM\runtime`, `/var/cache/oadm`,
  `/Library/Application Support/OADM/runtime`), never a shared temp folder.
- Version from the tag (`v1.2.0` -> `1.2.0`), else `0.1.0-dev`; MSI and .pkg use the numeric part.
- Icons: the app icon is `icon/oadm-app-icon-<size>.png` (16-1024); `packaging/icons/make-icons.py`
  builds `oadm.ico` (client exe, MSI, shortcuts), `oadm.icns` (macOS), `oadm.png` / `oadm-256.png` (Linux, window icon of
  every client window) from it.
Supply chain: every GitHub Action is pinned by its full commit hash (the version as a comment), `.github/dependabot.yml`
opens a weekly pull request with updated hashes, checkout runs with `persist-credentials: false`, workflow permissions
are `contents: read` except the release job (`contents: write`).

Developer commands: one entry point per shell at the repo root, `./manage.sh` (bash) and
`.\manage.ps1` (PowerShell 5.1/7), verb + target (`build`, `run`, `test`, `publish`, `package`, `clean`,
`info`), `manage <verb> help` at every level. Both read their help texts from
`scripts/manage-help.txt`; any new verb, target or option goes into both scripts and that file.
Native AOT and trimming are deliberately NOT used for the host apps: AOT cannot load managed
plugin assemblies at runtime and trimming removes framework APIs plugins rely on. Small
helper tools without plugin loading may use Native AOT.
Code must never use `Assembly.Location` (empty in single-file); use `AppContext.BaseDirectory`.

The client bundles the FFmpeg decoder libraries (avcodec, avutil, swscale, swresample; FFmpeg 9,
LGPL-2.1 build from the `DevEnvy.FFmpeg.Binaries.LGPLv2.Runtime.<rid>` package, bindings
`FFmpeg.AutoGen`). They are native shared libraries embedded in the single-file exe and extracted
by the .NET host at startup (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`); the publish folder contains only
the exe. They add about 57 MB to the win-x64 exe. `OADM_FFMPEG_DIR` loads a user-supplied build
instead (LGPL replaceability), `Oadm.Client --check-decoder` verifies the decoder without a
window. Licenses and source offer: `THIRD-PARTY-NOTICES.md` (hand-written part of the generated
`THIRD-PARTY-NOTICES.txt`, see above). Never add GPL or nonfree FFmpeg builds.

# Solution Layout

```
Oadm.sln
src/
  Oadm.Contracts/      protobuf files + generated gRPC stubs, shared enums
  Oadm.Sdk/            plugin SDK: interfaces, attributes, context objects. No Avalonia, no EF
  Oadm.Sdk.Client/     client-side plugin SDK: dialog/page/toolbar interfaces, ITaskDialogContext,
                       IToolbarContext, shared controls (Controls/: IconLabel, SearchBox, OadmIcon,
                       DialogTitleBar, CardHeader, DialogFooter, StatusChip, FileRow, ProgressRow,
                       ToolbarButton, ToolbarSeparator, PasswordBox, MessageWindow, FormField),
                       form validation (Validation/: ValidatingViewModel, FormValidator), Network/InterfaceSelection,
                       Collections/RangeObservableCollection (bulk list of the host grids and plugin pages)
  Oadm.Core/           domain model, VAPIX client, discovery, task engine, persistence (EF Core)
  Oadm.Server/         host: gRPC services, plugin loader, polling, Serilog setup
  Oadm.Client/         Avalonia app: views, view models, gRPC client, plugin loader
plugins/                (layout and SDK guide: plugins/README.md)
  Oadm.Plugins.Restart/   first Task plugin (server only)
  Oadm.Plugins.SnapshotReport(.Client)/   first Core plugin: rail page + PDF maintenance report
  Oadm.Plugins.VapixCommander(.Client)/   core plugin: VAPIX command library, raw requests, rollouts
  Oadm.Plugins.NtpServer(.Client)/        core plugin: NTP server (RFC 5905 server mode) + "Use OADM as NTP server"
  Oadm.Plugins.DhcpServer(.Client)/       core plugin: DHCP server (RFC 2131) with static leases and lease list
  Oadm.Plugins.MetadataMonitor(.Client)/  core plugin: live event stream of one camera (RTSP metadata, port of AXIS Metadata Monitor)
  Oadm.Plugins.Pki(.Client)/              core plugin: PKI (one CA for device certificates, trusted root store)
  Oadm.Plugins.<Name>/          server part: Oadm.Plugins.<Name>.Server.dll + plugin.json
  Oadm.Plugins.<Name>.Client/   optional Avalonia part: Oadm.Plugins.<Name>.Client.dll
                                (both copy their output to artifacts/plugins/<plugin id>/)
tests/
  Oadm.Core.Tests/
  Oadm.Server.Tests/
  Oadm.Client.Tests/
tools/                 Oadm.DiscoveryProbe (manual discovery tests), Oadm.Notices (THIRD-PARTY-NOTICES.txt generator)
packaging/             installers (windows/, linux/, macos/), icons/, notices/licenses/ (license text templates)
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
  It subscribes to `DeviceService.Watch` and `TaskService.Watch` streams (with snapshot end marker; the
  task snapshot is limited to `TaskStore.MaxTasks` = 10,000 plus the active tasks) and keeps an
  in-memory mirror for the grids. Stream changes reach the UI thread through a coalescing
  `ChangeBatcher` (one dispatcher post per burst) and are applied to the stores as batches.
- Server change feeds (devices, tasks) deliver every change in order while a watcher keeps up; beyond
  1,024 waiting changes they coalesce per entity (`KeyedCoalescingChannel`: the latest snapshot wins,
  an Added stays Added), so a slow client never loses a final state and never blocks the server.
- gRPC responses are gzip-compressed (live view frames excepted); server `MaxReceiveMessageSize` 16 MB,
  client 32 MB.

## gRPC services (Oadm.Contracts)

- Every call needs `authorization: Bearer <token>` in the metadata (plus `oadm-client-machine`), except
  `AuthService.Status`, `Login`, `CreateFirstAdmin`; UNAUTHENTICATED without a valid token, PERMISSION_DENIED for a
  missing role (`Oadm.Server.Auth.AccessPolicy`, see "Production hardening" 1).
- `AuthService` (`auth.proto`): `Status` (server name, version, `needs_first_admin`, `setup_code_required` when the
  caller is not loopback), `Login(user, password, remember)` -> {token, UserInfo} (UNAUTHENTICATED one message for
  wrong password / unknown / disabled user, RESOURCE_EXHAUSTED while locked), `CreateFirstAdmin(user, password,
  setup_code, remember)` (FAILED_PRECONDITION users exist, PERMISSION_DENIED setup code, INVALID_ARGUMENT), `Logout`
  (revokes the call's token), `Me`. `UserService` (Admin): `List`, `Add`, `Update` (optional role / disabled, new
  password; ends the user's sessions), `Delete`; FAILED_PRECONDITION for the own account or the last enabled
  administrator. `AuditService` (Admin): `List(limit, offset)` newest first + `total_count` (limit 0 = 10,000).
- `DeviceService`: `List` (legacy: one message with every device, 23 MB for 5,000 devices with their
  API lists; clients do not use it), `Watch(WatchDevicesRequest)` (stream of DeviceChanged events: one
  ADDED per device, then with `snapshot_end_marker = 1` one `SNAPSHOT_END` (kind 4, no device), then live
  changes; the request is wire compatible with the former Empty), `Remove` and `SetCredentials` (one
  transaction for all ids), `Refresh`, `GetWebUiUrl`.
- `DiscoveryService`: `StartZeroConf`, `StartRangeScan(from, to)`, `ProbeAddress(address)` (one
  entered IP or host name, optional port/scheme; INVALID_ARGUMENT for an unusable or unresolvable
  address), `WatchDiscovered` (stream; every device with its automatic login result
  `auth_state = 14` (PENDING, AUTHENTICATED, PASSWORD_NOT_SET, LOGIN_FAILED, UNREACHABLE,
  ALREADY_ADDED), `auth_user_name = 15`, `credential_id = 16` ("list:<id>",
  "entered"), `auth_detail = 17`, `passphrase_policy = 18`, `entered_address = 19`; every scan
  (zero-conf after `Discovery.ZeroConfSeconds`, range scans, address probes, or `StopScan`) ends with
  scan_finished and the stream ends once every login of the session finished; watching a finished
  session again replays its devices with their current login result, then streams the logins still
  running), `StopScan` (ends the scan early, the session and its devices stay; unknown ids ignored),
  `Stop` (forgets the session; the add page calls it on close).
- `AddDevicesService`: `RetryAuth(session_id, discovered_id, user_name, password,
  save_to_credential_list, related_session_ids = 6)` (returns the updated DiscoveredDevice, also pushed
  to the stream; on success the credential is tried on the failed devices of the session and of the
  related sessions, see "Add Devices Page"; NOT_FOUND, FAILED_PRECONDITION factory default / already
  added, INVALID_ARGUMENT),
  `Commit(session_id, discovered_ids, initial_passwords map = 6, initial_root_password)` (reply:
  `device_ids`, `results = 3` with discovered_id, device_id, status), `Prepare` (legacy, unused).
  See "Add Devices Page".
- `TaskService`: `ListTaskPlugins(ListTaskPluginsRequest)` (context-menu entries incl. those contributed by Core
  plugins; `TaskPluginInfo.display_name` normalized by the server, `group = 8` never empty; runnable sets
  cached by `TaskPluginRunnableCache` per device-table version, at most 10 s old; with `compact = 1` a
  plugin carries the shorter of `runnable_device_ids` or `runnable_on_all_except = 9` +
  `not_runnable_device_ids = 10`: 74 KB instead of 1.8 MB for 5,000 devices x 10 plugins), `Run(pluginId, deviceIds, payloadJson)` (one task per device, reply `task_ids`;
  `task_id` is deprecated = first id; all tasks of a Run written in one transaction), `List(ListTasksRequest)`
  (newest first; `limit`/`offset` paging, `TaskList.total_count = 2`; limit 0 = all, legacy),
  `Watch(WatchTasksRequest)` (stream: snapshot of every active task plus the newest `snapshot_limit`
  (0 = all), `SNAPSHOT_END` (kind 4, no task) when `snapshot_end_marker`, then live changes), `Cancel`, `Delete`,
  `DeleteAll` (cancels and waits for active tasks, then one bulk delete), `GetLog(taskId)` (per-task log, oldest first, live while running); `TaskInfo` carries
  `repeated TaskStep steps = 13` (index, name, `TaskStepState`, detail, progress, started, finished) and
  `current_step_index = 14` (-1 none),
  `Query(pluginId, deviceId, method, payloadJson)` (read-only `ITaskPluginQuery` for task
  dialogs, 30 s timeout; NOT_FOUND, UNIMPLEMENTED, FAILED_PRECONDITION for an incompatible device,
  changed certificate or rejected credentials, UNAVAILABLE, DEADLINE_EXCEEDED, INVALID_ARGUMENT;
  the status detail is the user message).
- `FileService`: `Upload` (client stream: header {name, size} then 256 KB data chunks; returns
  id, name, size, SHA-256; INVALID_ARGUMENT, RESOURCE_EXHAUSTED over `Uploads.MaxMegabytes`, over the 10 GB of
  all uploads together or when less than 1 GB of disk would stay free, see "Production hardening" 4),
  `Delete(fileId)`. Uploads live in `<datafolder>/uploads/<id>.bin` + `<id>.json`, are deleted
  after `Uploads.RetentionHours` (checked every 15 min) and reach tasks through `IUploadedFiles`.
- `PluginService`: `ListCorePlugins` (navigation pages), per-plugin generic
  `Invoke(pluginId, method, payloadJson)` for Core plugin UI pages (NOT_FOUND unknown plugin or
  object, FAILED_PRECONDITION not running, INVALID_ARGUMENT for an `ArgumentException` of the
  plugin, INTERNAL otherwise; the status detail is the message), `Watch(plugin_id)` (stream of `PluginEvent`
  {plugin_id, topic, payload_json} the plugin publishes through `ICorePluginContext.Events` from the call on; NOT_FOUND
  unknown plugin; `Oadm.Core.Plugins.PluginEventHub` fans out with 256 events buffered per watcher, oldest dropped).
  Users: "Snapshot report", "VAPIX Commander", "NTP server", "DHCP server", "PKI", "Metadata Monitor".
- `SettingsService`: `Get`, `Set` (`ServerSettings.zero_conf_seconds = 8`, 0 keeps the stored value; read-only
  `server_version = 21` in both replies for the About page),
  `ListCredentials`, `AddCredential(user_name, password)`
  (INVALID_ARGUMENT, RESOURCE_EXHAUSTED over 20 entries; an identical pair returns the existing
  entry; a new entry is tried on the failed devices of every open add session), `RemoveCredential(id)` (NOT_FOUND), `RevealCredential(id)` (reply `RevealedCredential { password = 1 }`:
  the stored password of one credential list entry for the Credentials page eye and copy buttons; NOT_FOUND;
  logged as "Credential list password of <user> revealed", never the password). Credential entries carry id,
  user name and created time, never a password.
- `LiveViewService`: `Watch(device_id, max_width, max_height, fps, accepted_codecs, camera)`
  (stream of encoded access units), `ListSources(device_id)` (view areas / sensors / channels).
  See "Live view".

# Data Model (EF Core, SQLite)

SQLite runs with the WAL journal (`DatabaseInitializer`) and `synchronous=NORMAL` on every connection
(`SqlitePragmaInterceptor`): a commit does not wait for an fsync, the database stays consistent after a
crash; with FULL a poll round of 5,000 devices took 22 s instead of 6 s.

- `Device`: Id (Guid), Serial (= MAC, unique, upper hex, no separators), Address,
  UseHostName (bool), HostName, Model (ProdNbr), FirmwareVersion, DhcpEnabled, HttpsEnabled,
  Dot1xEnabled, UpnpFriendlyName, ServerName, Status (enum below), Scheme (http/https),
  ProductType (raw basicdeviceinfo ProdType, e.g. "Dome Camera"), Category (enum below),
  HasVideo (derived from Category, not stored),
  CertFingerprintSha256 (nullable), CertNotAfterUtc (nullable), CertTrust (enum below),
  CertSubject, CertIssuer, CertNameMatches (nullable bool, address in SAN; stored, not shown
  yet), LastSeenUtc, WarrantyExpiry (nullable, later), ReplacementModel (nullable, later), Tags,
  Apis (JSON column `[{id, version, name, status}]` from `apidiscovery.cgi getApiList`, written on
  every full refresh incl. the first one after add; proto `repeated DeviceApi apis = 28`),
  CredentialUserName (not stored: user name of the DeviceCredential, filled by repository reads
  for plugins; never the password).
- `DeviceCredential`: DeviceId, UserName, EncryptedPassword (AES-GCM, see Security).
- `CredentialListEntry` (table CredentialListEntries, migration `CredentialList`): Id (Guid),
  UserName (max 64), EncryptedPassword (AES-GCM with the entry id as associated data), CreatedUtc.
  The technician's credential list (Credentials page), at most 20 entries, tried in the order added.
- `Task`: Id, BatchId (shared by the tasks of one Run; proto `batch_id`), PluginId, Name, Status
  (Queued, Running, Done, Failed, Cancelled, DoneWithWarnings), Owner (client machine/user name),
  CreatedUtc, StartedUtc, FinishedUtc, Progress (0-100), PayloadJson (column kept but always NULL:
  payloads may carry secrets and are never persisted; the migration clears old values),
  ScheduledUtc (nullable, unused in Goal 1). Indexes: CreatedUtc (paging, newest first), BatchId, Status
  (migration `TaskStatusIndex`: active tasks for startup recovery and every Watch snapshot).
- `TaskDeviceResult`: TaskId, DeviceId, Status, Message (last progress message, warning or
  error), Progress. Exactly one row per task (proto `TaskInfo.device_id` = its DeviceId).
- `TaskStep` (table TaskSteps, migration `TaskSteps`): key (TaskId, Index), Name (max 200), State
  (Pending, Running, Done, Warning, Skipped, Failed; stored as string), Detail (max 1000: result, progress
  detail, skip reason or error), Progress (0-100), StartedUtc, FinishedUtc (nullable). Cascade-deleted with
  the task; rows are rewritten by index with every task snapshot (a dynamic step may shift later ones).
  At most 200 steps per task (`TaskStepList.MaxSteps`). The current step (`TaskRecord.CurrentStepIndex`,
  not stored) is the running one, else the last that started (the failed one), else the last ended.
- `TaskLogEntry` (table TaskLogEntries): Id (autoincrement), TaskId (cascade delete), DeviceId
  (nullable = task level), TimeUtc, Level (Info, Warning, Error), Message (max 2000 chars).
  At most 1000 entries per task; the 1000th says that later entries were dropped.
- `Setting`: Key, ValueJson.

Device status enum: `Ok`, `Unreachable`, `CredentialsRequired` (401/403), `PasswordNotSet`
(factory default), `CertificateChanged`, `Unknown`.

Device category enum (`Oadm.Sdk.Devices.DeviceCategory`, also used by Core): `Camera`,
`Encoder`, `Speaker`, `Audio`, `Intercom`, `Radar`, `IoModule`, `DoorController`, `Other`,
`Unknown`. Mapped from ProdType in exactly one place, `Oadm.Core.Devices.DeviceCategoryMapper`:
case-insensitive keyword rules, first match wins (intercom/door station, door controller,
encoder/video server, camera, radar, speaker/horn, audio/microphone/amplifier, I/O / relay
module). Empty ProdType is `Unknown`; an unmatched one is `Other` and logged once per string.
Verified: AXIS P3265-V on AXIS OS 12.11 reports ProdType `Dome Camera` (also anonymously via
`getAllUnrestrictedProperties`). `HasVideo` is true for Camera, Encoder and Intercom.
Filled by the anonymous probe on the add page (discovered list shows the icon), on add and on
every basicdeviceinfo poll.

Certificate trust enum: `Trusted` (chain builds to a root in the server OS trust store, or to a
trust anchor of a core plugin such as the PKI CA: `ITrustAnchors`, `Oadm.Core.Vapix.TrustAnchorRegistry`),
`SelfSigned` (subject equals issuer and the only chain error is the untrusted root),
`Untrusted` (any other chain failure, e.g. private CA not in the store), `Expired` (past
NotAfter), `Unknown` (not checked yet or HTTP only). Only the chain is evaluated; the host name
is ignored because devices are usually reached by IP address.

# Device Discovery

## Zero-conf: mDNS / Bonjour only (Goal 1)

Browse `_axis-video._tcp.local`. Verified on AXIS OS 12.11: the TXT record has exactly one
key `macaddress=<SERIAL>`, the instance name is `<Bonjour.FriendlyName> - <SERIAL>`, SRV
points to port 80 at `axis-<serial lowercase>.local`, A/AAAA gives the address. Ignore
169.254.x.x link-local addresses when a routable one is announced.
Must work on all three OS and on multiple NICs (bind one socket per interface). A scan of the add
page runs at most `Discovery.ZeroConfSeconds` (default 30, 5..300; `DiscoveryService.StartZeroConf(duration)`,
timer from the server's TimeProvider) or until `StopScan`: browsing stops, the probes already started
finish, watchers get scan_finished, the devices stay in the session until `Stop`. Without a duration
(server start, periodic re-find) it runs until stopped. SSDP and WS-Discovery
are explicitly out of scope for Goal 1.

## Following moved devices

OADM keeps a device managed when its address changes (`Oadm.Core.Devices.DeviceAddressService`):
- **By a task** (Network plugins after setting a static address): `ITaskExecutionContext.UpdateDeviceAddressAsync`.
  The server reads basicdeviceinfo at the new address with the stored credentials, scheme and pinned
  certificate; a different or missing serial throws `DeviceIdentityException` and the record stays. Otherwise
  the record's Address changes (credentials and pin kept, Unreachable becomes Unknown), the change is published
  and a full refresh queued. Logged ("Device X moved from A to B").
- **By the periodic re-find** (`Oadm.Server.Devices.DeviceRelocationHostedService`, `DeviceRelocationOptions`):
  every 5 minutes (first run 30 s after start), only while at least one device is Unreachable and addressed by
  IP, a 15 s zero-conf session looks for those serials; a device announced at another address is verified
  there the same way and moved (`TryRelocateAsync`). This covers DHCP changes, where the new address is unknown.
- Devices addressed by host name (`Devices.UseHostName`) keep their host name; their record never moves.

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
  HTTP and Basic or Digest over HTTPS. Basic is never sent over plain HTTP, with one exception: the add
  page's automatic login to a device that passed the anonymous Axis check (`AllowBasicOverHttp`, see
  "Production hardening" 2).
- Factory-default check without credentials: `systemready.cgi` returns `needsetup` (yes =
  no admin user yet) and `passphrasepolicy` (none, length, complex).
- Prefer HTTPS, fall back to HTTP if 443 is closed. Store which scheme worked.
- Certificates: trust on first use. Store SHA-256 fingerprint on add. A later mismatch sets
  status `CertificateChanged` and blocks tasks until the user accepts the new certificate.
- Certificate info: the TLS callback (`CertificatePinning`) also describes every presented
  certificate with a separate `X509Chain` build (system trust store, `RevocationMode.NoCheck`,
  no downloads, validity dates ignored) into `VapixClient.ObservedCertificate`; pinning and
  TOFU decisions are unaffected. Expiry is applied at refresh time (`TrustAt(now)`). Captured
  on add and on every full refresh; cleared for HTTP-only devices. Cross-platform APIs only.
- Endpoints used in Goal 1:
  - `basicdeviceinfo.cgi` -> SerialNumber, ProdNbr, ProdShortName, Version, HardwareID
  - `param.cgi?action=list&group=Network.BootProto,Network.UPnP.FriendlyName,Network.Interface.I0.dot1x.Enabled,HTTPS.Enabled`
    -> DHCP, UPnP name, 802.1X, HTTPS (`HTTPS.Enabled=yes|no` still exists on 12.x)
  - `pwdgrp.cgi` action add, user root, grp root, sgrp admin:operator:viewer:ptz
    -> first password on a factory-default device. Works over HTTP without credentials, but
    we send it over HTTPS when available and always with arguments in the POST body, never
    in the URL. Since AXIS OS 11.5 the first user does not have to be named root.
  - `restart.cgi` -> Restart task plugin
  - `param.cgi?action=list&group=Properties.Image,Image` -> live view codecs
    (`Properties.Image.Format`, e.g. `jpeg,mjpeg,h264,h265`), resolutions and video sources
  - RTSP `rtsp://<host>/axis-media/media.amp?videocodec=h264|h265&camera=<n>&resolution=WxH&fps=N&videokeyframeinterval=N&audio=0`
    on port 554 -> live view (see "Live view")
- All VAPIX access lives in `Oadm.Core.Vapix.VapixClient`, one method per endpoint,
  unit-tested against recorded responses in `tests/.../Fixtures`.

# Add Devices Page (fast add, no wizard)

User decision: maximum technician satisfaction. Adding devices is one page, no steps; the server
logs in to every device it finds with the credentials the technician already has.

Toolbar (toolbar plugins, see "Toolbar plugins"): **Scan** (primary), **Scan IP range**, **Add
manually** open the same dialog window (`Discovery/AddDevicesWindow`, shared dialog controls: title
bar, one card, footer) in three modes:
- **Scan**: zero-conf (mDNS) discovery starts immediately; devices appear live. The scan ends after
  `Discovery.ZeroConfSeconds` (Settings page, default 30 s, 5..300).
- **Scan IP range**: From / To inputs on top; Enter or the Scan button starts (several ranges add to
  the same list).
- Scan progress row (`ui:ProgressRow`, indeterminate for zero-conf, percent for ranges) with a
  **Stop** button (icon stop) while a zero-conf or range scan runs (`StopScan` for every running
  session; the devices found stay) and **Scan again** (icon refresh) after it finished or was stopped
  (zero-conf: a new session; range: the same range again; found devices stay, deduplicated by serial; a
  "Checking" of the new search never hides the result or selection of an earlier one). The text then
  says "Scan finished, N devices found" or "Scan stopped, N devices found" ("1 device found"), plus ", M already added" when
  devices OADM already manages answered: those are not listed at all (user decision 2026-10-08) and not in the summary.
  Add manually has neither button.
- **Add manually**: Address input (IP or host name, optional port and scheme,
  `https://camera.example.com:8443`); Enter or Find probes that address (`ProbeAddress`); every
  address adds a row; "No Axis device answered at X." when nothing answers. The entered address
  (host[:port]) becomes the device address on add, regardless of `Devices.UseHostName`.

List: checkbox (40 px), category icon (36 px), Address (140), MAC address (150), Model (160), Login
(status, star: takes the rest so status and detail sit right after Model), Action (auto, min 160, at
the right edge); checked headless at 1040 px window width. Above it
"Select all authenticated", a summary ("10 found · 3 ready to add · 1 need a login · 2 need a
password · 3 selected"), search box and the scan progress row. Login status per device
(`ui:StatusChip`): Checking... (accent), Authenticated (user) (ok), Password not set (warning),
Login failed (error, reason as tooltip), Unreachable (error), Added (ok, row greyed; already managed devices are not
listed, see above).
Only addable devices can be checked: authenticated ones, and factory-default ones
once a first password is entered. **Add** (footer, "Add 3 devices") commits the checked devices
in one click and the page always closes (user decision: no "Keep open" option).
Every password field (login editor, first password + confirm) is `ui:PasswordBox` with the eye button.

- **Login failed**: a click on the row or the "Log in" link opens the inline editor below the list
  (user name, password, "Save to credential list" checked by default, Retry, Cancel). Retry calls
  `RetryAuth` right away (with every other session of the page as `related_session_ids`) and updates
  the row; success checks the row and closes the editor, a wrong password shows "The user name or
  password is wrong.". After a successful Retry the server tries the same credential on every other
  device of the page whose login failed; the page watches the sessions whose stream already ended
  again to receive those results.
- **Factory default**: "Set password" opens the inline password editor (new + confirm, user root,
  hint with the systemready passphrase policy: none = 1-64 printable ASCII, length = at least 15,
  complex = at least 12 with upper, lower, digit and special character; checked client side, the
  device checks again), "Use for all factory-default devices". The password stays in client memory
  until Add; the server sets it as today (pwdgrp.cgi, HTTPS when offered, POST body only) and
  stores it as the device credential. The row shows "Password ready · Change".

Server: `DiscoveryAuthenticator` (Oadm.Server/AddDevices). For every device event of a watched
session it starts the automatic login once per device state: managed serial = ALREADY_ADDED, probe
said factory default = PASSWORD_NOT_SET (plus the passphrase policy read anonymously), unreachable =
UNREACHABLE, otherwise it tries the known credentials one at a time, at most 10 per device (no
lockout risk): the credentials typed on the page (RetryAuth), then the credential list in the order
added. Passwords of managed devices are never tried on new devices. Before the first credential goes to
a scheme the device must pass the **Axis check** on it: anonymous `basicdeviceinfo
getAllUnrestrictedProperties` answering a 12-hex `SerialNumber` equal to the discovered serial and a
`ProdNbr` (once per scheme and login run). Logins go to HTTPS first, then HTTP ("Add manually" with an
entered scheme: only that one); the handler answers Digest when offered, else Basic, also over plain HTTP
for such a verified device. A device that fails the check gets no credential and shows UNREACHABLE "The
device did not identify itself as an Axis device. No password was sent." (logged as a warning). One try
= `basicdeviceinfo getAllProperties` with the credential (plus `param.cgi` network parameters when the
device allows anonymous access); 401 = next, a scheme that does not answer = the next scheme, no scheme
answering = UNREACHABLE, another serial at the address = UNREACHABLE. A device that logged in over HTTP
with Basic only is stored with scheme http and then needs Digest like every managed device (Basic stays
HTTPS only outside the add page's login). At most 8 devices log
in at the same time. Results live per (session, serial) for 30 min after the last use together with
the credential that worked (server memory); `Commit` stores that credential for the new device
(explicit `credentials` in the request still win, legacy). Passwords never leave the server once
stored; RetryAuth credentials travel only client -> server and are kept in server memory (or the
credential list when asked). Adding devices is not a task; the first full refresh is queued in the
background as before.

Follow-up logins: per (session, serial) the authenticator keeps the device as last observed, the
number of rejected automatic attempts and which credentials (SHA-256 of user + password) the device
rejected. When a new credential becomes known it is tried on every LOGIN_FAILED device that has
attempts left and has not rejected it: (a) a successful `RetryAuth` adds it ("entered", or the list
entry when saved) to the session and its `related_session_ids` and starts those logins before the reply
returns (the devices show PENDING); (b) a credential added to the credential list (Credentials page or
"Save to credential list") reloads the candidates of every session. The follow-up uses the normal login
loop: one login at a time per device, at most 8 devices at once, at most 10 rejected credentials per
device in total (typed credentials of `RetryAuth` are remembered as rejected but not counted), entered
credentials first, then the list; credentials added while it runs are
tried in the same run. Unreachable attempts count neither as attempt nor as rejected.

# Visual Style

Reference: MultiDrive (Avalonia showcase), `docs/style-reference-multidrive.png`, https://multidrive.io/.
Dark only, calm and spacious, no gradients inside the app.

- Window: starts maximized. Custom title bar (ExtendClientAreaToDecorationsHint), app name
  top-left, window buttons top-right, background near-black `#141414`. Caption button hover
  is a neutral grey slightly lighter than the title bar (never the system blue/accent); the
  close button hovers red like Windows. No connection indicator in the title bar; a banner
  appears only while the server is unreachable.
- Left navigation rail: expanded by default (user decision 2026-10-08; collapsed: icons only, ~56 px, tooltips with the label),
  expand/collapse toggle at the bottom of the rail; expanded ~220 px with icon + label.
  Selected item as rounded pill `#2A2A2A`. Entries top: Devices, then one per Core plugin
  page. Pinned bottom, top to bottom: Users and Credentials (Admin only: hidden for operators, follow the session
  role live), Logs, Settings, About (icons `users`, `key`, `logs`, `settings`, `info`), then the logged-in user with
  Log out and the expand toggle. Keys = `HostPages` (`users`, `credentials`, `logs`, `settings`, `about`); opening an
  admin page as operator does nothing, a hidden page that was open falls back to Devices. No Tasks page. Every page
  header (core plugin pages, Users, Credentials, About, Settings) is the shared `Controls/PageTitleBar` (title, grey
  side subtitle, trailing content); host pages never repeat the page title in a card heading.
- Content in rounded cards: background `#181818` (only slightly lighter than the window),
  corner radius 16, padding 20-24, 16 px gap. The device grid sits in one card, the tasks
  pane in a second card below it, separated by a draggable splitter so the user can resize
  the tasks pane; the split is persisted per client.
- Accent violet `#6C5CE7` (hover `#7D6EF0`), used for selection, progress bars, primary
  buttons and status pills. Secondary accent teal `#7FC8D0`.
- Text: primary `#F2F2F2`, secondary `#9A9A9A` (labels like "Source", column headers).
  Font Inter (Avalonia.Fonts.Inter), whole-pixel sizes only (fractional sizes blur): 12 px body, 10 px small, 18 px card titles, 22 px page titles, semibold titles; every window renders text with subpixel anti-aliasing, strong hinting and pixel-aligned baselines (App.ApplyCrispText). Page subtitles sit right of the page title, small, grey, baseline aligned.
- Status is never a chip: everywhere a status or result is shown (device certificate columns,
  tasks, task steps, logs, add page, plugin dialogs) use `ui:StatusChip` = a small icon colored
  by the status (check ok green, warning amber, error red, running violet and spinning, neutral
  grey) with plain text in the normal text color to its right, plus optional detail text
  (e.g. the error message). Device grid: the first-column device icon is colored by the device
  status and the Status column is plain text. Tasks pane: the Status column shows only the
  colored icon, with the error or warning message right of it when there is one; finished tasks
  show no progress bar. Count badges stay a small solid violet pill.
- Window chrome: title bar and caption buttons are 32 px high (caption buttons 46x32), exactly
  like the Windows default. Main window minimum size 1280x720.
- Text fields, number fields and select fields: text vertically centered (measured; select
  fields need the Inter correction, text boxes do not).
- Icons: one outline icon set (Fluent System Icons / Lucide geometry), monochrome
  `#CFCFCF`, 16 px. Toolbar uses icon + text buttons, not ADM's icon-only bar.
- All colors and sizes as resources in `Themes/OadmTheme.axaml`; no hard-coded colors in views.

# Main Window: Manage Devices

Reference for content: ADM screenshot `docs/adm-manage-devices.png`. Same information
density, styled as described in Visual Style.

Layout, top to bottom:
1. Title "Devices". Left navigation rail as described in Visual Style.
2. Toolbar: toolbar plugins (Scan, Scan IP range, Add manually | Remove | task plugin actions that
   declare `ShowInToolbar` | plugin entries), Columns button and search box right-aligned (host parts).
3. Status line: "N devices, M selected".
4. Device grid (virtualized): sortable, column chooser, column order and width persisted per
   client, horizontal scroll, multi-select, right-click context menu: the core actions (Open web
   interface, Remove), a separator, then one **submenu per task group** (`TaskPluginInfo.group`, sorted
   by name, with a group icon: Applications app, Maintenance settings, Network network, Security key,
   Users users, Video video, others plugin; a group is a submenu even with one entry, user decision)
   holding its Task plugins whose `CanRun` is true for the whole selection, sorted by name, with their
   icons. Entries never end with "..." (the host appends none and strips "..." / "…" defensively,
   `TaskPluginNames.Normalize`). Menus and submenus are at least `Oadm.MenuMinWidth` (240) wide (theme).
   The toolbar task buttons are unchanged (no groups).
5. Resizable, collapsible bottom pane **Tasks** (no tabs), one row per task (= per device).
   Columns: Name, Device (130 px), Status (widest, 5*, min 280 px: icon plus message), Current step, Start time, Owner, Progress (bar). **Current step** is
   "Step 3/6 · Upload firmware" plus " · 45 %" while the running step reports progress (tooltip: the text
   and the step detail); a failed task shows its failed step, a successful one just "Completed"; empty for plugins without steps. **Device** is the task's
   device by its device grid address (IP or host name, resolved through the client device store
   and updated live); a device removed since shows as "removed device 1a2b3c4d" (id shortened).
   Tooltip: "10.0.0.200: Failed - Connection refused". Sortable by the text. Status chips:
   Queued/Cancelled neutral, Running violet, Done green, **Done with warnings** amber (warn
   chip), Failed red. **Details** opens the task details window, live while it is open: a Device card
   (device, MAC address, model, status chip, message), a **Steps** card ("3 of 9 steps finished"; per
   step #, state chip (Pending/Skipped neutral, Running violet, Done green, Warning amber, Failed red),
   name, detail, duration, progress bar + percent only for the running step; rows updated in place) and a
   Log card (level chip, time, device address, message; `TaskService.GetLog`, re-read when the current
   step or the task state changes, new entries appended), built from the shared grid and chip styles.
   Buttons: details, cancel, delete, **delete all** (with confirmation; running tasks are
   cancelled first); all work per task.

**Logs page** (rail, bottom): live client log with level filter and search. Server log
streaming comes later.

## Live view

Clicking the device icon (first grid column) of a video device opens the live video in a panel
on the right of the device card, inside the main window, so nobody has to log in to the camera
web UI. Reference screenshot: headless `client-liveview.png`.

- Panel: reusable `Controls/LiveViewPanel` in a card next to the device card (about 60 : 40,
  resizable with the vertical `paneSplitter`; the device card keeps at least 900 px so its toolbar
  stays on one line, the panel at least 320 px). Slides in from the right (250 ms, cubic ease-out).
  Header: `IconLabel` with video icon and model, subtitle address, serial and (with several
  sources) the source name, source switch, close button. Below: state chip (Connecting, Live,
  Reconnecting, Error) and detail text (codec, resolution, fps, or the reason). Picture
  `Stretch=Uniform` on a dark rounded surface.
- Open/close: the icon of the shown device or the close button or Escape closes the panel and
  stops the stream; another device's icon switches. Removing the shown device closes it. The icon
  cell has a hover state and the tooltip "Live view"; devices without video are disabled
  (`LiveViewSupport.IsSupported`, a model heuristic until the device category from the server is
  available: C audio, A1/A9 door and I/O controllers, D3 sensors and T accessories have no video).
- Sources: `ListSources` returns every `Image.I<n>` that is not disabled as camera `n+1` with its
  `Image.I<n>.Name` (view areas on single-sensor cameras, sensors and quad view on multisensor
  cameras, inputs on encoders). With more than one source the header shows a segmented switch
  ("1 2 ...", tooltip = name); switching restarts the stream with `camera=<n>`; the choice is
  remembered per device for the client session. 10.0.0.48 reports one sensor
  (`ImageSource.NbrOfSources=1`), 8 view areas, of which "View Area 1" (full frame) and
  "View Area 2" (cropped) are enabled.
- Transport: the server opens RTSP/1.0 over TCP to port 554 with RTP interleaved on the same
  connection, Digest authentication with the stored credentials (Basic is refused on plain RTSP;
  credentials never reach the client), `Blocksize: 64000`, a keyframe every second
  (`videokeyframeinterval=fps`). Own RTSP client and depacketizers (H.264 RFC 6184, H.265 RFC 7798)
  in `Oadm.Core/LiveView`. Video itself is not encrypted on the camera link (RTSP over HTTPS is a
  later option). Devices with status `CertificateChanged` are refused.
- Negotiation: H.265 > H.264 among the codecs the camera lists in `Properties.Image.Format` and
  the client can decode; when opening a codec fails the next is tried. No MJPEG. Resolution: the
  largest of the source's resolutions with the sensor aspect that fits the requested box (default
  1920x1080), always 25 fps (user decision: full HD at 25 fps decodes in software on any current PC or
  laptop).
- Sharing: one upstream camera connection per (device, camera, codec, resolution, fps) shared by
  all viewers, opened on the first and closed when the last viewer leaves. A joining viewer gets
  the cached GOP first (picture within one frame time). Slow viewers never buffer: a full queue
  (60 frames) is dropped and the viewer resumes at the next keyframe. Packet loss drops access
  units until the next keyframe.
- gRPC: each `LiveViewFrame` carries one Annex B access unit, the keyframe flag, parameter sets
  on keyframes, RTP timestamp, resolution and camera. Client channel max message size 32 MB.
- Client: FFmpeg decode on a background thread (low delay, slice threads), swscale to BGRA into a
  new `WriteableBitmap`, swapped on the UI thread, old bitmaps disposed. Reconnect with backoff
  1, 2, 4, 8, 10 s; permanent errors (unknown device, credentials, no common codec, certificate
  changed) stop with the server's message. If FFmpeg cannot be loaded the panel shows
  "Video decoder not available on this platform" (no fallback).
- Fake mode replays recorded 10.0.0.48 video (H.265 or H.264) and reports two view areas for the
  P3265-V and four sensors plus quad view for the P3727-PLE.
- Measured on 10.0.0.48 at 640x360, 10 fps: first frame 0.3-0.6 s after the request through the
  server; about 160-250 kbit/s for both H.264 and H.265 (scene dependent, keyframe every second).

Device grid columns, default order:

| Column | Source |
|---|---|
| (icon) | category icon (camera, encoder, speaker, audio, intercom, radar, I/O module, door controller, generic), tooltip "Camera (Dome Camera)" |
| MAC address | SerialNumber from basicdeviceinfo |
| Status | computed, see enum |
| Address | IP or host name, hyperlink opens device web UI in default browser |
| Model | ProdNbr |
| Firmware | Version |
| DHCP | Network.BootProto == dhcp -> Yes/No |
| HTTPS | HTTPS enabled -> Enabled/Disabled |
| Certificate expires | CertNotAfterUtc as days left: "245 days", "1 day", "Today", "Expired 3 days ago". Chip: ok > 30 days, warn <= 30 days, error expired. Empty for HTTP-only |
| Certificate | CertTrust: Trusted (ok), Self-signed (warn), Untrusted / Expired (error). Empty for HTTP-only. Both certificate cells have a tooltip with subject, issuer and valid-until date |
| IEEE 802.1X | dot1x.Enabled -> Enabled/Disabled |

Polling: server refreshes status (basicdeviceinfo) for every device every **60 s** by
default (`Polling.IntervalSeconds`). Full refresh (basicdeviceinfo + network parameters +
certificate info + server name) on add, after a task finishes on that device,
every **10 minutes** per device (`Polling.FullRefreshMinutes`, checked every 30 s),
and immediately when a status poll sees a device go from any non-Ok status back to Ok. All
full refreshes go through one deduplicating queue with bounded parallelism. Scale: a poll round starts
every interval and spreads its device starts evenly over 90 % of it (`PollRoundAsync`, at most
`MaxParallelism` = 16 at once; 5,000 devices at 60 s = about 90 polls per second, not a burst); each
device row is read right before its poll. After a server start the first scheduled full refresh is
staggered: the n-th of N devices is due n/N of an interval later. A poll that changes nothing but
LastSeenUtc is written but not published to watchers (no 5,000 identical updates per minute to every
client).

# Tasks

- **A task always targets exactly one device.** Running a plugin on N selected devices creates
  N tasks (one Run, one shared BatchId, the payload shared in memory), so one failing device
  never marks the others as failed. The task state is the device state.
- States: Queued, Running, Done, Failed, Cancelled, DoneWithWarnings (the plugin called
  `ReportWarning`; a failure wins over a warning). Persisted, visible in the tasks pane, history
  kept until the user deletes it or the retention removes it (finished tasks older than
  `Tasks.RetentionDays` = 90 or beyond the newest `Tasks.MaxHistory` = 50,000; hourly, see Settings).
- **Steps: a task always tells the user exactly what it is doing.** Every device request and every
  wait of a task plugin is its own short, imperative, named step ("Read users", "Upload firmware",
  "Wait for the device to come back"); five requests = five steps. Plugins plan their steps up front
  (`PlanSteps`, shown Pending) and may add steps on the way; steps that do not apply are Skipped with a
  reason ("Keep unchanged"). Engine rules: beginning a step completes the running one; when the plugin
  returns, a running step ends Done and planned steps never reached end Skipped ("Not run."); when it
  throws, the running step (or the one its `using` just ended) ends Failed with the exception message and
  pending ones Skipped ("Not run: an earlier step failed." / "... the task was cancelled."; a cancelled
  running step is Failed "Cancelled."). The running step is also the device message ("Upload firmware -
  12 of 80 MB"). When the task succeeds (Done or Done with warnings) the engine appends a final step
  **Completed** (Done, `TaskStepList.CompletedStepName`, also for plugins without own steps), so the
  tasks pane reads just "Completed"; Failed and Cancelled tasks get none (the failed step stays
  current). Overall progress = steps with equal weights, the running one with its own progress,
  unless the plugin calls `ReportProgress` (then that value wins). Step changes go to the change feed
  immediately and are persisted with the throttled writes (max 1/s per device) plus every state
  transition. Recovery after a server restart marks a Running step Failed ("Server stopped while the
  task was running.") and Pending ones Skipped.
- **Task names: the task list says exactly what a task does** ("Add user joe", "Set static IP
  10.0.0.60", "Upgrade firmware to 12.11.77 (factory default)"), never the plugin's menu name: the engine
  names each task with `ITaskPlugin.GetTaskName(payloadJson)` once per Run (trimmed, trailing "..."
  stripped, at most `TaskPluginNames.MaxTaskNameLength` = 48 characters, longer names shortened with
  "…" and a warning logged; empty or throwing = `DisplayName`). Names never contain secrets.
- Task engine parallelism: at most `Tasks.MaxParallelPerPlugin` running tasks (= devices) per plugin, a
  **server setting** (default 16, 1..256, Settings page "Parallel tasks per plugin"). A plugin's
  `ITaskPlugin.MaxParallelDevices` can only lower it: limit = min(setting, MaxParallelDevices) (firmware
  sets 4: large uploads share the server's link). Further tasks wait in Queued (FIFO per plugin,
  `Core/Tasks/PluginSlots`) and a cancel while queued ends them as Cancelled ("Cancelled before start.").
  The engine reads the setting live (`TaskParallelismSetting` -> `TaskEngineOptions.MaxParallelTasksPerPluginSource`)
  each time a queued task could start: a change applies without a server restart to tasks that start
  afterwards (a raised limit starts queued tasks at once via `TaskEngine.RescheduleQueued`; a lowered one
  never interrupts running tasks, new ones start when fewer run). The former configuration value
  `Oadm:MaxParallelTasksPerPlugin` is no longer read. Example: a Restart on 5,000 devices at about 90 s
  each takes about 8 hours at 16, about 1 hour at 128. Cancellation via
  `CancellationToken`, exceptions become `Failed` with message (also logged as an Error entry),
  never crash the server. A Run on N devices publishes N Added changes and writes all N tasks in
  one store transaction (`ITaskStore.AddRangeAsync`) before any of them starts; the engine overhead
  is measured at about 9 ms per task with SQLite (5,000 trivial tasks in about 45 s).
- Persistence: every state transition writes the snapshot plus new log entries; a progress
  report with a message, a warning or a log entry writes at most once per second per device
  (the message survives as the device result). Payloads are never persisted or logged.
- Task log: `ctx.Log(level, message)`, warnings, failures, precondition errors and credential
  changes; live from memory while the task runs (`GetLog`), from TaskLogEntries afterwards.
- Scheduling, retry and recurrence are out of scope for Goal 1.

# Plugin System

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

```csharp
public interface IPlugin
{
    string Id { get; }                 // "oadm.restart", "oadm.ntp"
    string DisplayName { get; }            // task plugins: max 32 chars (TaskPluginNames.MaxDisplayNameLength), no trailing "..."
    string? IconKey { get; }
}

public interface IDeviceInfo          // read-only device view for plugins
{
    Guid Id { get; }
    string Serial { get; }
    string Address { get; }
    string? HostName { get; }
    string? Model { get; }
    string? FirmwareVersion { get; }
    DeviceStatus Status { get; }
    DeviceCategory Category { get; }   // Camera, Speaker, Radar, IoModule, ...
    bool HasVideo { get; }             // filter in CanRun, e.g. snapshot only for video devices
    IReadOnlyList<DeviceApi> Apis { get; }   // apidiscovery list of the last full refresh
    string? CredentialUserName => null;      // user OADM stores for the device (server side), never the password
    DateTime? CertNotAfterUtc => null;       // HTTPS certificate end of validity (server side)
    string? CertTrustName => null;           // "Trusted", "SelfSigned", "Untrusted", "Expired"; null unknown/HTTP
}

public interface ITaskPlugin : IPlugin
{
    string Group => TaskGroups.General; // context menu submenu; TaskGroups.Applications, General, Maintenance,
                                       // Network, Security, Users, Video, or any new name
    bool ShowInToolbar { get; }
    bool RequiresDialog { get; }       // client opens the matching ITaskPluginDialog first
    bool CanRun(IDeviceInfo device);
    int? MaxParallelDevices => null;   // concurrent tasks of this plugin, only lowers Tasks.MaxParallelPerPlugin; null = the setting (16)
    bool ShowInMenus => true;          // false: not in context menu/toolbar (ListTaskPlugins skips it); started by its core plugin
    string GetTaskName(string? payloadJson) => DisplayName;  // task list name: exactly what this task does, max 48, no secrets
    Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);
}

public interface ITaskPluginQuery     // optional on task plugins: read-only reads for the dialog
{
    Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct);
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
    Guid TaskId { get; }
    IVapixClient Vapix { get; }        // pre-authenticated for the current device
    ILogger Logger { get; }
    ICorePlugin? Owner { get; }        // set when the task was contributed by a Core plugin
    IUploadedFiles Files { get; }      // uploads referenced by id in the payload
    void ReportProgress(int percent, string? message = null);   // optional with steps; wins over the derived progress
    void PlanSteps(params string[] names) { }                   // DIM: pending steps shown up front
    ITaskStep BeginStep(string name);                           // DIM default: a detached step (third-party contexts)
    void ReportWarning(string message);                         // device ends DoneWithWarnings
    void Log(TaskLogLevel level, string message);               // per-task log, never secrets
    void MarkCredentialsInvalid();     // device lost OADM's credentials: delete them, refresh
    Task UpdateCredentialsAsync(string userName, string password, CancellationToken ct); // store new ones; Vapix is swapped
    Task<IVapixClient> CreateClientForAsync(string address, CancellationToken ct);       // DIM: device at another address (same credentials, scheme, pin); caller disposes
    Task<bool> UpdateDeviceAddressAsync(string newAddress, CancellationToken ct);         // DIM: server verifies the serial, moves the record, Vapix is swapped; false = host name kept
    Task UpdateDeviceTlsAsync(string scheme, string? expectedFingerprintSha256, CancellationToken ct); // DIM: after a web server change: "https" + the
                                       // new certificate's SHA-256 (null = pin what is presented) or "http"; server connects that way, verifies serial
                                       // and certificate, stores scheme, pin and certificate details (no CertificateChanged), Vapix is swapped
}
// DIM defaults throw NotSupportedException. UpdateDeviceAddressAsync and UpdateDeviceTlsAsync throw DeviceIdentityException (Oadm.Sdk.Plugins)
// when the device at the new address has another serial or does not answer; the record stays unchanged.
// ITaskQueryContext has IDeviceRepository? Devices (DIM null): all managed devices, read-only.

public interface ITaskStep : IDisposable   // Dispose without an end = Done; escaping exception = Failed
{
    string Name { get; }
    TaskStepState State { get; }       // Pending, Running, Done, Warning, Skipped, Failed
    void ReportProgress(int percent, string? detail = null);    // "37 of 82 MB"
    void Complete(string? detail = null);                       // Done, detail = result ("AXIS OS 12.11.77")
    void Warn(string message);         // Warning + ctx.ReportWarning (task ends Done with warnings)
    void Skip(string reason);          // did not apply
    void Fail(string message);         // records the failure; throw to fail the task
}
// Helpers (TaskStepExtensions): ctx.StepAsync(name, async step => ...) (Done / Failed + rethrow),
// ctx.SkipStep(name, reason). Pattern: using var step = ctx.BeginStep("Send restart"); ...
// TaskStepList (Oadm.Sdk.Plugins) is the state machine behind it, used by the engine and by test
// fakes; tests/Shared/StepRun.cs runs a plugin with the engine's end rules for step assertions.

public interface ICorePluginContext
{
    IDeviceRepository Devices { get; }
    IVapixClientFactory Vapix { get; }
    ITaskRunner Tasks { get; }         // core plugins may start tasks themselves; Cancel(taskId) (DIM false)
    ISettingsStore Settings { get; }   // namespaced per plugin
    ILogger Logger { get; }
    string? PluginDirectory => null;   // folder the plugin was loaded from (data files); never Assembly.Location
    ISecretProtector? Secrets => null; // Protect/Unprotect(value, purpose): AES-256-GCM with the master key
    IPluginEvents? Events => null;     // Publish(topic, payloadJson): live events to the plugin's page (PluginService.Watch)
    ITrustAnchors? TrustAnchors => null; // Set(der[]): CAs the server trusts when rating device certificates (per plugin)
    IDeviceEventStreams? EventStreams => null; // OpenAsync(deviceId): the device's RTSP event stream with the stored
                                       // credentials (never handed out): XML documents, LostDocuments, dispose = TEARDOWN
    IFirewallRules? Firewall => null;  // Open/CloseAsync(FirewallRule): inbound allow rule for the server exe, Domain +
                                       // Private; only the Windows service offers it (netsh), else null. Helper:
                                       // FirewallRuleKeeper.SyncAsync(enabled) (Oadm.Sdk.Network)
}
```

Task plugin names and groups: `DisplayName` is at most `TaskPluginNames.MaxDisplayNameLength` (32)
characters and never ends with "..." (dialog tasks too); it is the menu and toolbar name only, the tasks
pane shows `GetTaskName` (see Tasks). The server registry (`RegisteredTaskPlugin.DisplayName`
/ `.Group`) strips trailing "..." / "…" and shortens longer names to 31 characters + "…", logging a
warning for either; empty groups become `General`, groups are shortened the same way. The task name in
the tasks pane, `TaskPluginInfo` and the client menu and toolbar use the normalized name. Bundled plugins:
Restart, Upgrade firmware and Date and time (Maintenance), Applications (ACAP) (Applications), Users (Users), Network
settings and Assign IP address (Network), the eight PKI tasks (Security, contributed by `oadm.pki`); `TaskPluginNamesTests` checks every plugin deployed to
`artifacts/plugins`.

## Client SDK (Oadm.Sdk.Client)

```csharp
public interface ITaskPluginDialog
{
    string PluginId { get; }
    Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner); // payload JSON, null = cancel
}

public interface ITaskDialogContext   // server access of a dialog, bound to its plugin id
{
    Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct); // TaskService.Query
    Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct); // FileService.Upload, progress 0..1
}

public interface ICorePluginPage
{
    string PluginId { get; }
    Control CreateView(ICorePluginClientContext ctx);  // page shown in the navigation rail
    bool HasOwnCards => false;         // true: the view lays out its own cards, no host card around it
    bool ShowTasksPane => false;       // true: the shared tasks pane (TasksPaneLayout) sits below the page,
                                       // so rollouts are watched without switching pages (VAPIX Commander)
}

public interface ICorePluginClientContext  // UI thread; all but InvokeAsync have defaults
{
    Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct); // PluginService.Invoke
    IAsyncEnumerable<PluginEvent> WatchEventsAsync(CancellationToken ct); // PluginService.Watch; default empty (poll)
    IReadOnlyList<IDeviceInfo> Devices { get; }          // + DevicesChanged
    IReadOnlyList<IDeviceInfo> SelectedDevices { get; }  // selection of the Devices page
    string OwnerName { get; }                            // "user@machine" for tasks the page starts
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task OpenAsync(string hostPage);                     // HostPages.Devices etc.
    Window? Owner { get; }
}

public interface IToolbarPlugin          // a part of the Devices page toolbar
{
    string Id { get; }                   // "oadm.toolbar.scan"; built-in ids are reserved
    int Order { get; }                   // within the group
    ToolbarGroup Group { get; }          // Add, Manage, Tasks, Plugins (left to right, separator between)
    Control CreateControl(IToolbarContext ctx);   // any Avalonia control, created once
}

public interface IToolbarContext         // UI thread
{
    IReadOnlyList<IDeviceInfo> SelectedDevices { get; }  // + SelectionChanged
    IReadOnlyList<IDeviceInfo> Devices { get; }          // + DevicesChanged
    IReadOnlyList<ToolbarTaskPlugin> TaskPlugins { get; } // + TaskPluginsChanged
    bool CanRunTask(string pluginId);
    Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, CancellationToken ct); // like the context menu
    Task OpenAsync(string hostPage);     // HostPages.AddScan, AddIpRange, AddManually, Devices, Logs, Settings
    Task RemoveDevicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct);
    Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct);
    Window? Owner { get; }
}
```

### Toolbar plugins

The Devices page toolbar is made of toolbar plugins. Built in (compiled into the client,
`Devices/Toolbar/BuiltInToolbarPlugins.cs`, registered in the container like plugin parts): Scan
(primary), Scan IP range, Add manually (group Add), Remove (Manage; confirmation, the context menu
uses the same flow) and one generic plugin with a button per task plugin that declares
`ShowInToolbar` (Tasks; enabled when it can run on the whole selection). External ones come from
`*.Client.dll` like dialogs and pages. `DeviceToolbar` orders them by group, order, id, creates each
control once (failures logged, entry left out), keeps a `ToolbarSeparator` only between groups that
show something and moves the same controls into a new page view. Buttons are `ui:ToolbarButton`
(icon key + text, `IsPrimary`). The Columns button and the search box are host parts. Sample:
`tests/TestPlugins/Oadm.TestPlugins.Sample.Client` (deployed to `artifacts/test-plugins/oadm.sample`).
API guide: `plugins/README.md`.

Dialogs and pages are real Avalonia views with view models, styled by the host theme. They use
the shared controls from `Oadm.Sdk.Client.Controls` (`IconLabel`, `SearchBox`, `PasswordBox`, `OadmIcon`,
`DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip`, `FileRow`, `ProgressRow`, `FormField`,
`ToolbarButton`, `ToolbarSeparator`, `MessageWindow` (every message and confirmation popup:
`MessageWindow.ConfirmAsync` / `ShowMessageAsync`, used by the host dialog service and plugin dialogs);
usage in `plugins/README.md`) and
the theme resources (styles, colors, `Icon.*` geometries) of the host application; the icon keys
are listed in `plugins/README.md`. Plugins never talk to devices from the client; payloads go to
the server. gRPC errors reach the dialog as `RpcException` (Status.Detail is the message).

`VapixRequestOptions.Timeout` (`HttpRequestOptionsKey<TimeSpan>` "Oadm.RequestTimeout") on a
request passed to `IVapixClient.SendAsync` overrides the default 15 s timeout for that request
(firmware and ACAP uploads use minutes; plugins use the SDK constant, never their own key); request
bodies are streamed, never buffered.

## Loading and packaging

- Server scans `<app>/plugins/*/`, `<datafolder>/plugins/*/` and, in a repository checkout,
  `<repo>/artifacts/plugins/*/` (`PluginPaths.Development`) on startup, loads each
  `*.Server.dll` in its own collectible `AssemblyLoadContext`, resolves `ITaskPlugin` and
  `ICorePlugin` implementations via reflection, then registers the Core plugins' `TaskPlugins`
  too. Client does the same for `*.Client.dll` with `ITaskPluginDialog`, `ICorePluginPage` and
  `IToolbarPlugin` over the same three roots (`ClientPluginLoader.DefaultRoots`).
- Plugin folder: `plugin.json` (id, version, minSdkVersion), `<Name>.Server.dll`,
  optional `<Name>.Client.dll`, private dependencies. SDK assemblies are shared from the
  host and never copied into the plugin folder.
- Plugin failures (load or execution) are logged and isolated; a broken plugin never
  prevents server or client from starting.

## HARD RULE: device safety for task plugins

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

## First plugin: Restart

`plugins/Oadm.Plugins.Restart`: `ShowInToolbar = true`, `RequiresDialog = false`, calls
`restart.cgi`, then polls basicdeviceinfo until the device answers again (timeout 3 min).
Steps: Check device (basicdeviceinfo; unreachable fails "Nothing was changed"), Send restart, Wait for
the device to go offline, Wait for the device to come back, Verify device (serial number; a different
one is a Warning). No client assembly needed.

Steps of the other task plugins (details in each plugin README):
- Every successful task ends with the engine's step Completed (not listed below).
- Users: Check compatibility, Read password policy, Identify OADM account, Read users, Validate change,
  Add/Update/Remove user <name> (one per write; Remove of several users: one "Remove user <name>" each,
  all validated before the first one), Verify users. Names: "Add user joe", "Change password joe",
  "Change role joe", "Change user joe", "Remove user joe", "Remove users joe, ann". Dialog: Remove picks
  the users in the Existing users list (multi-select, no user name field; the OADM account and the last
  administrator are greyed with a tooltip and cannot be selected), no general lock-out warning text.
- Network: Check compatibility, Read current settings, Read IPv6 address mode, Validate settings, Check
  address is free, Set host name, Set DNS, Set IPv6 (+ Enable IPv6), Set IPv4 (order as written), Wait for the
  settings to apply, Check reachability, Wait for the device at the new address, Verify device identity, Update
  OADM device address; unchanged sections Skipped "Keep unchanged". Names: "Set static IP 10.0.0.60" / "Set
  static IP addresses", "Switch to DHCP", "Set DNS servers", "Set host name <name>", "Change IPv6 settings"
  when only that section changes, else "Change network settings".
- Assign IP address: Check compatibility, Read current settings, Validate settings, Check address is free, Set
  DNS, Set IPv4, Wait for the settings to apply, Check reachability, Wait for the device at the new address,
  Verify device identity, Update OADM device address. Names: "Assign IP 10.0.0.60" / "Assign IP addresses",
  "Assign IP via DHCP".
- Check address is free (both): before the first write every new static address (IPv4, IPv6) is probed with
  `IAddressProbe` (ICMP echo via `System.Net.NetworkInformation.Ping`, 2 tries x 1 s, plus a TCP connect to
  80/443; the device's own current addresses are not probed). An answer fails the step "10.0.0.60 is already in
  use (answers ping). Nothing was changed." and nothing is sent; Skipped for DHCP. The dialogs' query
  `checkAddresses` uses the same probe.
- Firmware: Check compatibility, Read device info, Validate file, Read firmware status, Upload firmware
  (byte progress), Install firmware (until offline), Wait for device to come back, Verify version, Read
  commit state, Commit firmware (retries add "Wait before retrying the commit" and "(attempt n)" steps).
  File checks never look at the content (real images start with gzip/tar-like bytes, e.g. AXIS OS 10.12
  `M3206-LVE_10_12_338.bin`): only `.bin`, 1 MB..2 GB and product/version from the download name when it
  follows the pattern; the device verifies product and signature itself. Names: "Upgrade firmware to
  12.11.77", "Downgrade firmware to 10.12.236", "Install firmware 12.11.77" (mixed), "Install firmware"
  (version unknown), + " (factory default)".
- Date and time: Check compatibility, Read current time settings, Read NTP settings, Validate settings, Set time zone,
  Set NTP configuration / Turn off NTP, Set date and time, Verify time settings, Verify NTP settings (like ADM the time
  zone and the time mode are always planned; values the device already has are Skipped "Already ...").
- ACAP install: Check compatibility, Read package, Read device info, Read embedded development version,
  Read unsigned application setting, Read installed applications, Check compatibility of package, Upload
  package (byte progress), Verify installation (+ Start application, Verify application state); remove,
  start, stop: Check compatibility, Read installed applications, <action> application, Verify ....
  Names: "Install <app> <version>", "Upgrade <app> to <version>", "Remove|Start|Stop <app>".
- Restart: name "Restart device". VAPIX Commander rollout: the command name or "<first> +N more".

## Network settings plugin

`plugins/Oadm.Plugins.Network` (+ `.Client`), id `oadm.network`, context menu (group Network) "Network settings",
dialog with IPv4 / IPv6 / DNS / Host name sections (each "Keep unchanged" by default). One device: IP address
fields; several devices: no IP range field, the new IPv4 address and (static IPv6 only) the new IPv6 address are
set per row in the shared address table (columns "New IP address", "New IPv6 address"; IPv4 suggested from the
first device's current address and subnet), subnet mask, gateway and prefix length stay shared fields. Errors are
shown once: below their field (`INotifyDataErrorInfo`) or in the row's Status column, never as a list; Apply is
disabled with a tooltip saying why. No "Current: ..." info lines (the fields are prefilled). Apply on a risky
change (address, subnet, gateway, DHCP switch, IPv6 change over IPv6) asks in the host's shared confirmation
window `ui:MessageWindow` ("The devices may become unreachable", Cancel / Apply); no inline acknowledgement. Uses network-settings 1.x (`getNetworkInfo`, `setIPv4AddressConfiguration`,
`setResolverConfiguration`, `setHostnameConfiguration`, `setIPv6AddressConfiguration` >= 1.6) and
param.cgi `Network.*` for the IPv6 address mode and for devices without network-settings. Writes the
address family OADM connects with last. After a new static address (IPv4, or IPv6 when OADM connects over
IPv6) OADM follows the device: it polls the new address (90 s), verifies the serial number and moves the
device record (`UpdateDeviceAddressAsync`); not answering, another serial or a refused move end as Warning
with the record unchanged; host names are kept. DHCP keeps the record (step "Update OADM device address"
Skipped "DHCP: address assigned by the network, the device will be found again by the next scan"), the
periodic re-find moves it. Decision table and verified device behavior: plugin `README.md`.

## Assign IP address plugin

Same package, id `oadm.network.assign-ip`, context menu (group Network) and toolbar **Assign IP address**, a clone of
ADM's "Assign IP address to selected devices" (research and sources in the Network plugin `README.md`):
- Page 1: "Obtain IP addresses automatically (DHCP)" (Finish here) or "Assign the following IP address
  range" with **IP range**, **Subnet mask**, **Default router** (prefilled from the first device), optional
  DNS servers (domain name and search domains of each device kept). One device: "Assign the following IP
  address", field "IP address".
- IP range syntax as ADM/ACS (`IpRangeExpression`): `192.168.0.*`, `10.*.1.*`, `192.168.0.10-192.168.0.20`,
  `192.168.0.10-20`, `10.10-30.1.101`, `10.10-30.1.*`, comma lists; a single address alone is a start address.
- Page 2 "New IP addresses": the shared `AddressAssignmentGrid` (MAC address, Model, Current IP address, New IP
  address editable in the cell, Status chip), devices in the grid order the host passes. Suggestions skip
  network/broadcast addresses, the router, other managed devices' addresses and addresses in use (query
  `checkAddresses`: managed devices plus ping and a TCP connect to 80/443 from the server, rows "In use (answers
  ping)"; also a **Check addresses** button). "Not enough addresses: ..." below the table when the range is too
  small; conflicts (duplicate, outside the subnet, used by a managed device, in use) are red chips only in the
  Status column and block Finish. Field errors (IP range, subnet mask, default router, DNS) below their field; no
  error list, no current-values info line. Finish asks in the shared `ui:MessageWindow` with the reachability
  warning (Cancel / Finish); no inline acknowledgement.
- Payload: `NetworkPayload` with `ipv4` (+ `dns` with `keepDomains`) only; one task per device with the
  Network plugin's steps for DNS and IPv4 and the follow-the-device steps.
- "Network settings" shares range parsing, suggestion, conflicts, validation, warnings and the table.

## Snapshot report plugin (core plugin)

`plugins/Oadm.Plugins.SnapshotReport` (+ `.Client`), id `oadm.snapshot-report`, rail page **Snapshot
report** (icon `snapshot`). Purpose: the yearly maintenance check. The technician sees current
snapshots of every video source of the managed video devices, checks them fast and exports a PDF report.
Read-only for devices (param.cgi reads and image.cgi snapshots).

- Sources: the live view discovery, exposed to plugins as `IVapixClient.GetVideoSourcesAsync()` (SDK
  `VideoSource(Camera, Name, Sensor, Resolutions)`; Core implements it with `GetImageCapabilitiesAsync`).
  Only `IDeviceInfo.HasVideo` devices. One source = one tile titled with the address; several = one tile
  per source "<address> - <label>": the view area name ("View Area 2", "Quad view"), or "Sensor n"
  (encoders "Channel n") for empty or generic "Camera n" names. Devices in IPv4 order. A device whose
  sources cannot be read (or whose status is CredentialsRequired / PasswordNotSet / CertificateChanged,
  checked without a request) is one error tile. Sources are cached 5 minutes for the snapshots.
- Snapshot: `GET /axis-cgi/jpg/image.cgi?camera=N&resolution=WxH` (JPEG API) with the stored
  credentials, server side. Resolution: the largest of the source that fits the box with the sensor
  aspect (`VideoResolutions.Choose`, shared with the live view): grid 1280x720, report 1920x1080
  (`SnapshotReportPluginInfo`). Verified on 10.0.0.48 (AXIS OS 12.11): 1280x720 about 125 KB in
  0.1-0.3 s, camera=1/2 are the two view areas, a missing camera answers HTTP 400 with an HTML
  "400 Bad Request" page. At most 4 device requests at a time (all callers), 10 s timeout each.
  Errors are short texts: "Timeout after 10 s", "Unauthorized - HTTP 401", "Forbidden - HTTP 403",
  "Bad Request - HTTP 400", "Unreachable - <socket error>", "The device has no video source 3", or the
  VAPIX text the device sent ("Error: ...").
- `InvokeAsync` methods (`SnapshotReportMethods`, JSON camelCase, models in `Shared/`): `listSources`
  ({deviceIds}, empty = all) -> tiles with device facts; `snapshot` ({deviceId, camera, maxWidth,
  maxHeight}) -> {jpegBase64, width, height, capturedUtc, error}; `generateReport` ({site, technician,
  date, items[{deviceId, camera}]}) starts a background job (fresh report-size snapshots, then the
  PDF) and returns its status; `reportStatus` ({jobId}) -> state running/done/failed, done/total,
  message, size, pages, failed; `readReport` ({jobId, offset}) -> 2 MB base64 chunks (a report is larger
  than one gRPC message); `deleteReport`. Jobs live in server memory, 30 minutes after last use.
- Page: toolbar Refresh all, "Select all" check box, **Export PDF** (primary); picture size slider
  (200-720 px) and `ui:SearchBox` on the right; status line "14 pictures from 9 cameras · 13 selected
  · 3 failed"; `ui:ProgressRow` while loading ("Loading snapshots 5 of 14", 4 at a time). Tiles
  (`Border.tile`, accent outline when selected) in a wrap panel: picture (`Button.picture` on
  `Border.liveViewSurface`, Stretch Uniform; click = large preview window with facts and "Take new
  snapshot"), check box + title + refresh icon button, facts line (model · firmware · MAC), then the
  capture time and size, or a `ui:StatusChip` (Loading accent, error red with the message). A failed
  snapshot keeps the older picture. All tiles start selected.
- Export dialog: Site / customer (required), Technician, Date (yyyy-MM-dd, today), file name preview;
  site and technician (and the last folder) remembered per client in
  `LocalApplicationData/Oadm/plugins/oadm.snapshot-report/export.json`. Export asks for the file first
  (save dialog), then shows the job progress and downloads the PDF; failed snapshots keep the dialog
  open with a warning.
- PDF (PDFsharp + MigraDoc 6, MIT; QuestPDF rejected for its license; Roboto embedded): A4 portrait.
  Cover: "Maintenance report", site, technician, date, creation time and OADM version, summary (cameras,
  video sources, online, offline or not OK, snapshots taken / failed), firmware versions with counts,
  certificates expired or expiring within 30 days. Then two snapshots per page: heading "<title> ·
  <model>", the JPEG as sent by the device (embedded unchanged, DCTDecode) in a 17 x 8.5 cm box, facts
  table (model, MAC, address/host name, firmware, status, certificate, snapshot time and size, source);
  a failed snapshot is a grey box "No snapshot: <error>". Footer "<site> · <date> · Maintenance report"
  and "Page n of m". About 250 KB per full-HD snapshot.
- Fake mode (`FakeOadmApi.SnapshotReport.cs`): the core plugin is listed, tiles come from the fake live
  view sources (P3265-V two view areas, P3727-PLE four sensors + quad view, error tiles for the devices
  with a bad status), pictures are generated JPEGs, the PDF is a one-page placeholder.
- Tests: `tests/Oadm.Plugins.SnapshotReport.Tests` (source expansion, request and error mapping, PDF parsed
  back with PDFsharp: pages, cover text via ToUnicode, DCT images; view models; headless screenshots
  `snapshot-report-page.png`, `-preview.png`, `-export.png`; hardware test through the in-process server
  writing `snapshot-report-10.0.0.48.pdf` to `OADM_SCREENSHOT_DIR`).

## VAPIX Commander (core plugin)

`plugins/Oadm.Plugins.VapixCommander` (+ `.Client`), id `oadm.vapix-commander`, rail page **VAPIX
Commander** (icon `command`). A technician picks one or more VAPIX commands, fills their fields and rolls
them out to any number of devices, or sends one now to one device (Postman-like). Command format v1 and
JSON schema: `plugins/Oadm.Plugins.VapixCommander/Schema/command-format.md` + `command.schema.json` (the contract; category
`Custom` for saved commands, optional `response.errorPattern`).

- Library: every `Library/*.json` next to the plugin assembly (`ICorePluginContext.PluginDirectory`,
  fallback `AppContext.BaseDirectory/Library`), loaded once at start with strict JSON (unknown
  properties are errors) and `CommandValidator`; broken files and invalid or duplicate commands are
  skipped, logged and listed on the page ("N library entries could not be loaded"). The bundled library
  (161 commands, separate commit) is checked by `tests/Oadm.Plugins.VapixCommander.Library.Tests` and
  `BundledLibraryTests` (every command loads and validates with this engine).
- Saved commands: server side in plugin setting `savedCommands` (one JSON document, at most 500),
  shared by all clients, editable (renaming replaces), deletable, export/import as a library file
  (`formatVersion` 1, category of the commands or `Custom`; import keeps valid ids, same id replaces,
  invalid ones are reported). Password field values are encrypted with the server master key through
  the SDK `ISecretProtector` (`ICorePluginContext.Secrets`, AES-256-GCM, purpose
  `vapix-commander:<id>:<field>`), never listed, never exported; without a protector they are dropped.
  A saved command lists `storedSecretFields`; leaving such a field empty uses the stored value.
- Engine (server, payload in memory only, password values never logged, masked as `***` in request
  lines and bodies shown to the user): `FieldValues` checks values per type (integer, number, boolean,
  enum, string/password with pattern and min/max length; numbers min/max) before anything is sent;
  `CommandRenderer` fills `{{field}}` in path (URL-escaped), query (escaped, "," and ":" kept), headers
  (no Authorization/Cookie/Host), body (json: a value that is exactly `{{field}}` is typed; form;
  text; xml: XML-escaped; SOAP content type for envelopes) and extract paths; booleans are yes/no for
  `param-cgi`, true/false elsewhere, unless `trueValue`/`falseValue`; timeout via
  `VapixRequestOptions.Timeout` (default 15 s, max 600 s). `CommandExecutor` sends with the factory's
  cached client (never disposed by the plugin), decodes the body as UTF-8 itself (Axis sends
  `charset=utf8`), shows binary answers as "image/jpeg, 123.4 KB", pretty-prints JSON/XML (cut at
  256 KB) and interprets per kind (`ResponseInterpreter`, table in command-format.md). Errors: device
  text (`# Error: ...` line, JSON `message (code N)`, REST error/problem+json, SOAP fault, Axis
  `GeneralError`, first text line, HTML title) or transport text ("Timeout after 15 s", "Connection
  refused", "Host unreachable", "TLS/certificate error: ...", "Unauthorized - HTTP 401 (check
  credentials)", "Not Found - HTTP 404 (API not available on this firmware)", "Bad Request - HTTP 400:
  <device text>", "Server error - HTTP 500: <device text>"). HTTP 204 / empty 2xx is success.
- Rollout: page method `rollout` validates every command and value first (nothing starts on an error),
  writes need `confirmed`, then `ITaskRunner.RunAsync` with the contributed, hidden task plugin
  `oadm.vapix-commander.run` (`ShowInMenus = false`; one task per device, owner = client user). Steps:
  **Check compatibility** (fresh `GetApiListAsync` when a command writes or nothing is cached, else
  the cached API list; detail "All 3 commands supported (fresh API list)"), then one step per command
  named like the command ("(2)" for repeats) with the extracted result ("Product: P3265-V") or the
  error as detail. An incompatible command fails its step "Missing API x. Nothing was sent."; a failed
  command does not stop the others on that device; the task ends Failed with "<command>: <error>"
  (+N more failed) as the device message (tasks pane, next to the error icon), every request is logged
  (masked). **Stop on first error** (`RolloutRegistry`, server memory): the first failure aborts the
  rollout; the failing device skips its remaining commands ("Not run: stopped on the first error."),
  tasks not started yet are cancelled (`ITaskRunner.Cancel`, "Cancelled before start."), running
  devices finish their current command, skip the rest ("Not run: the rollout stopped after an error on
  another device.") and end Cancelled.
- Page methods (`CommanderMethods`, camelCase JSON, records in `CommanderContract.cs`): `listLibrary`,
  `listSaved`, `save`, `delete`, `export`, `import`, `tryRequest` (deviceId, command ref
  library/saved/inline, values, confirmed; read-only commands run at once, writes need `confirmed` and
  re-check a fresh API list; refuses CertificateChanged devices and cached-incompatible ones; returns
  status, reason, duration, headers, pretty body, request line and the interpreted result),
  `checkCompatibility` (deviceIds x command refs against the cached `device.Apis`: Compatible, Missing
  API, Version too old, API list not read yet, Needs a video device), `rollout`. User-level problems come
  back as `error` in the reply, not as gRPC errors.
- Page (`HasOwnCards`: three cards, shared controls only, compact icon buttons = `Button.toolbar.iconOnly`
  + `ui:OadmIcon` with a tooltip): **Library** card (Import icon right of the title, SearchBox, TreeView
  Built-in / Saved by category without any badges (no counts, no write / dangerous: the tooltip, the Kind
  column of the rollout set, the header of the selected command ("Write · dangerous · <description>") and
  the run confirmation say it). A single click on a command adds it to the rollout set, a click on a group
  only expands / collapses it (a double click is one action); Enter does the same for the selected node.
  Saved commands show Export and Delete icons on hover of their row (Delete key on a selected saved
  command); no "Add to rollout" and no "Export all" buttons); middle card with a
  segmented switch **Rollout set** (DataGrid #, Command, Category, Kind, Source and an icon column with
  per-row Move up, Move down, Save with values (saved copy in Custom with the entered values, opens the
  name editor for that row) and Remove, disabled when not possible (first row up, last row down); a
  "Remove all" icon in that column's header; no button row. Keyboard on the grid: Delete removes the
  selected row, Ctrl+Up / Ctrl+Down move it. Selecting a row shows its field form below, checked like
  the server while typing; `ui:PasswordBox` for passwords) / **Raw request** (method, path, query and
  header tables with Make field (the header table is hidden for GET unless the command has headers), body type + body, timeout, response kind, fields from `{{placeholders}}`
  typed by hand, "Add to rollout", "Save as command" with name, category, description, requires
  prefilled from the path (param.cgi -> param-cgi 1.0, `/config/rest/<api>/v<n>` -> `<api> n.0`, known
  CGIs), changes-the-device (prefilled from method/action) and dangerous); "Send to <device>" + **Send**
  shows the result card (status chip, "HTTP 200 OK · 84 ms · address", interpreted result, request
  line, body and headers in `ui:CodeView`s). The body language comes from the response content type
  (else from the text: param.cgi key=value); JSON and XML are always shown pretty-printed (2 spaces,
  also when the device sends them minified) with a **Pretty / Raw** switch (default Pretty; Raw = the
  exact device text from `TryOutcome.RawBody`, which the server sends only when it differs from the
  pretty body), content type and "Cut at 256 KB." next to it. **Target devices** card (Devices page
  selection preselected; buttons Devices page selection, All (matching the search), Compatible, None;
  SearchBox; compatibility summary "4,812 compatible · 188 not compatible · 12 not checked"; per device
  a check box and one status chip: "Compatible" or the first problem "<command>: Missing API x (+N
  more)", tooltip = every command). The run controls are docked at the bottom of this card (no separate
  Run bar card): status chip of the last action (+ "Show tasks" opens the Devices page), summary "3
  commands × 2 devices · 1 write", **Stop on first error** (default on), **Run on N devices**: a
  confirmation lists every command with its kind, incompatible devices and the stop mode when a command
  writes; dangerous commands need a second explicit confirmation.
- Scale (a site can have thousands of devices): the target list is virtualized (`ItemsControl` with
  `VirtualizingStackPanel`, only visible rows exist); `Targets` / `VisibleTargets` are replaced as whole
  lists (one reset, no per-item collection events), sorted by IPv4 address numerically; the selection is
  counted incrementally and All / None / Devices page selection / Compatible set the flags in one O(n)
  pass with a single notification; compatibility is computed on the client from each device's cached
  API list (`Compatibility.Check`, the server re-checks a fresh list before writes), no request per
  device. 5000 devices: filter, select all, devices page selection, compatibility and select compatible
  each stay under 200 ms (test).
- Tests: `tests/Oadm.Plugins.VapixCommander.Tests` (rendering of all field types, typed JSON, booleans
  per kind, validation, every response kind incl. error texts and transport mapping with a fake device,
  stop on first error through the real task engine and core plugin host, saved CRUD / encryption /
  export / import, compatibility, page view models against the in-process plugin (library click, per-row
  rollout actions and their disabled states, 5000-device timing), bundled library,
  headless screenshots `plugin-vapix-commander-rollout.png` (library + rollout set with four rows),
  `-try-error.png`, `-raw.png`, `-try-json.png`, `-try-json-raw.png`, `-try-xml.png`, `-try-paramcgi.png`,
  `-many-devices.png` (5000 devices, asserts that only a few rows are realized)); `tests/Oadm.Client.Tests/CodeViewTests`
  (detection, pretty-printing, tokens, fallbacks, size limits, rendering and copy);
  `tests/Oadm.Server.Tests/VapixCommanderServerTests` (PluginService routing, hidden task) and the
  read-only hardware test `VapixCommanderHardwareTests` (param.cgi list Brand and basicdeviceinfo, Try
  and a rollout on 10.0.0.48 through the in-process server). Not in fake mode (`--fake`) yet.

## NTP server plugin (core plugin)

`plugins/Oadm.Plugins.NtpServer` (+ `.Client`), id `oadm.ntp-server`, rail page **NTP server** (icon `clock`). Spec and
decisions: `docs/specs/ntp-server.md`. Own RFC 5905 server-mode implementation (SNTPv4 compatible), no library.
- Time source (user decision): without an upstream OADM is a fully valid NTP server on its own clock, like chrony's
  `local stratum 10`: LI 0, stratum 10, reference id `LOCL`, reference timestamp = now, root delay 0, root dispersion
  10 ms; never LI 3 / stratum 16, no OS clock-sync detection. With an upstream (one host name or IP, optional
  `host:port`): stratum = upstream + 1, reference id = upstream IPv4 address (IPv6: first 4 bytes of its MD5), root delay
  and dispersion accumulated (+15 ppm per second since the last sync). The OS clock is served, never set.
- Upstream (`UpstreamMonitor`, own loop, fully decoupled: requests are always answered at once from the current
  `TimeSourceState`, an immutable record the responder reads without locks): one query in flight, hard timeout 2 s per
  query, DNS with its own 3 s timeout (also when the resolver ignores the token) cached 5 min and kept stale on DNS
  failure; a fresh socket per query; answers must come from the queried address and port and carry our transmit
  timestamp as originate (16 random low bits), else ignored; sanity checks: mode 4, stratum 1..15, LI != 3, non-zero
  timestamps, round trip < timeout; Kiss-o'-Death (stratum 0) rejected. Good answer: poll 64 s, doubling to 1024 s after 4
  good answers in a row. Failure: retry after 2, 4, 8 ... s up to the poll interval; after 3 failures in a row the
  upstream is not reachable: local mode (stratum 10) + warning, back automatically on the next good answer; while 1-2
  failures the last good upstream state is still served. KoD RATE doubles the poll interval, DENY/RSTR wait 1024 s.
- Protocol: UDP 123 (`NtpServerOptions.Port`, tests use 0 = random port on 127.0.0.1), IPv4 and IPv6 in separate sockets.
  Mode 3 requests of version 3 and 4 with a transmit timestamp get one 48-byte mode 4 answer (VN and poll echoed,
  precision from the Stopwatch resolution, originate = request transmit, receive taken when the datagram is read,
  transmit set right before sending); everything else (modes 1/2/5/6/7, other versions, < 48 bytes) is dropped silently.
  NTP era handling (2036) in `NtpTimestamp`. The receive loop reuses one buffer, one answer buffer and one
  `SocketAddress`; the client key is read from the sockaddr bytes (no allocation per packet). Windows: SIO_UDP_CONNRESET off.
- Abuse protection (`NtpRateLimiter`): token bucket per client IP, burst 8, refill 1 per 2 s; over it: drop, one
  Kiss-o'-Death RATE per client per minute, one "Rate limited" log entry per client per minute. Global cap 2,000
  requests/s (dropped, status "Too many requests, dropping" for 10 s). Client table LRU, at most 10,000 entries, idle
  entries expire after 10 min. No allow list, no NTS in v1.
- Settings (plugin setting `config`: enabled, interfaceId, interfaceName, upstream) stored server side and restored on
  server start. Interfaces from the SDK `IServerNetworkInterfaces` (every up, non-loopback interface with an address;
  "All interfaces" binds 0.0.0.0 and [::]; an interface binds all its addresses except IPv6 link-local). A failed bind
  (port in use, interface gone) is retried every 30 s while enabled.
- Status line (one at a time, `ui:StatusChip`, detail as tooltip): `Running on 10.0.0.17:123` / `Running on all
  interfaces, port 123` (ok), `Stopped` (neutral), `Port 123 is in use by another program` (error; Windows +
  " (Windows Time service)" when `sc query W32Time` says RUNNING, tooltip: net stop w32time; Windows has no privileged
  ports, so AccessDenied there also means in use), `Insufficient permission to use port 123` (error; Linux tooltip: run
  as root or `sudo setcap 'cap_net_bind_service=+ep' <server exe>`; macOS: run with sudo or choose All interfaces),
  `Interface <name> is not available` (error; AddressNotAvailable or the interface is gone), then warnings in this
  order: `Too many requests, dropping`, `Upstream <host> does not answer, using this computer's time`, `Server clock differs
  from upstream by 3.2 s` (> 1 s). Mapping in `NtpStatusTexts.ForBindError` (SocketError x OS).
- Request log: last 40 in memory (`RequestLog` ring buffer of value entries, no table): time, client, offset (client
  transmit minus server receive; "-" beyond one day, e.g. clients that randomize the transmit timestamp), result Answered
  / Rate limited. Pushed live (see `ICorePluginContext.Events`, batched every 500 ms); the page resolves the device column
  ("P3265-V (10.0.0.48)") from the client's device list (O(n) per device change).
- Page methods (`NtpServerMethods`): `getState` -> `NtpState` (config, status, interfaces, requests, upstream, stratum,
  port); `save` ({enabled, interfaceId, upstream}) validates the upstream with one query (same timeouts; the running
  server keeps serving) and replies {saved, upstreamError, upstreamResult, state}; an upstream that does not answer is
  not saved (error under the field). Events: `state` (status/upstream changed), `requests` (new entries).
- Page (one card in the host card, subtitle in the host page header, status chip left of the Save button); Enable NTP server;
  Listen on (select, refreshed when the page opens); Upstream server (optional, checked while typing, field errors under
  the field, "Checking <host>" accent chip while Save runs, "Answered: Stratum 2, offset +3 ms, round trip 12 ms" after
  it, a line "Serving stratum 3 from ..." / "Serving the server clock ..."); Save (primary); "Last requests" DataGrid
  (Time, Client, Device, Offset, Result chip). The view model watches the events while the view is attached; when the
  stream ends or fails (or the host has none: fake mode) it re-reads the state every 2 s.
- Task "Use OADM as NTP server" (contributed, id `oadm.ntp-server.use`, group Maintenance, no dialog, `CanRun`: ntp 1.x
  or param.cgi): the address is the selected interface's address of the device's family, with "All interfaces" the
  local address the server routes to the device with (UDP connect, no packet). Steps: Check compatibility (fails "The
  OADM NTP server is not running ... Nothing was changed." when it is off), Read NTP settings, Set NTP server <addr>
  (Skipped "Already uses <addr>"), Verify NTP settings (Warning on a mismatch). Name "Use OADM NTP server 10.0.0.17"
  ("Use OADM NTP server" with All interfaces). The VAPIX requests are the Date and time plugin's (`TimePlanner`,
  `TimeClient`; its `Model/` and `Vapix/` sources are compiled into the NTP assembly, because a project reference would
  put `Oadm.Plugins.DateTime.Server.dll` into this plugin folder, where the loader would register Date and time twice).
- Per OS: Windows needs nothing but a free port 123 (W32Time holds it on many machines: stop it, then OADM serves; the
  firewall must allow inbound UDP 123); Linux needs root or `cap_net_bind_service` (chronyd/ntpd must not hold 123);
  macOS needs sudo for a single interface (wildcard binds below 1024 are allowed without root since 10.14).
- Tests: `tests/Oadm.Plugins.NtpServer.Tests` (codec round trip, era wrap, invalid input; responses incl. local mode and
  ignored modes; rate limiter burst/refill/LRU/global; status per SocketError and OS; interface listing; upstream
  client against `FakeUpstream`: timeout, slow and late answers, wrong origin, other port, KoD RATE/DENY,
  unsynchronized, DNS failure/hang/cache; monitor fallback and recovery; in-process loopback integration: valid mode 4
  answer a client accepts, rate limiting with one KoD, answers within 100 ms while the upstream hangs, upstream
  validation on Save, persistence across restart, port in use retried, missing interface; the task against the Date and
  time fake camera; page view model; headless screenshots `ntp-server-page.png`, `-errors.png`,
  `-upstream-warning.png`, `-checking.png`). A hardware test where 10.0.0.48 queries OADM is a device write and is not
  written/run without explicit user approval.

## DHCP server plugin (core plugin)

`plugins/Oadm.Plugins.DhcpServer` (+ `.Client`), id `oadm.dhcp-server`, rail page **DHCP server** (icon `network`). Spec and
decisions: `docs/specs/dhcp-server.md`; layout, per-OS setup and the manual test plan: `plugins/Oadm.Plugins.DhcpServer/README.md`.
Own RFC 2131 / 2132 implementation, IPv4 only, one interface, no relay agents (relayed messages are ignored and logged).
- Settings (plugin setting `config`: enabled, interfaceId, interfaceName, rangeStart, rangeEnd) restored on server start;
  the plugin starts disabled. Leases (plugin setting `leases`, a JSON list of static and bound/expired/released leases,
  never offers) are written at most every 2 s when changed and on stop; they survive restarts.
- Derived, never asked: mask from the interface prefix, router = the interface's IPv4 gateway when it is in the subnet,
  DNS = the interface's IPv4 DNS servers, domain = its DNS suffix, lease 24 h (T1 50 %, T2 87.5 %), server id = the
  interface address. Shown as one line: "Clients get mask 255.255.255.0, router 10.0.0.138, DNS 10.0.0.138, lease 24 h"
  ("no router" / "no DNS", ", domain x" when there is one). Server, router and DNS addresses inside the range are skipped.
- Protocol (`DhcpEngine`): DISCOVER -> OFFER (static lease first, then the client's previous lease, the requested
  address, the next free address from a rotating cursor, then the oldest expired/released lease); REQUEST selecting
  (another server id: our offer is withdrawn), init-reboot (NAK for a wrong address or network, silent without a
  record), renewing/rebinding (NAK without a lease); DECLINE and in-use probe hits mark the address as a conflict for 1 h;
  RELEASE keeps the record (the device gets the same address again); INFORM -> ACK without lease time. Replies go to
  ciaddr when set, else broadcast (also NAKs and clients without the broadcast flag: no portable ARP injection).
  Parameter request list honored for 1, 3, 6, 15 (51/53/54/58/59 always); host name (12) echoed and stored.
- Before a new address is offered the Network plugin's probe (ping + TCP 80/443, `AddressProbe.cs` compiled in) runs
  with 2.5 s timeout, at most 32 probes at once (busy: no answer, the client retries), up to 3 addresses per request.
  Static leases are never probed (the user decided).
- Abuse protection: per MAC token bucket (burst 10, 1 per 3 s, one log line per device per minute), global 500
  messages/s, table LRU 10,000 MACs with 10 min idle expiry (shared `KeyedRateLimiter`); at most 64 messages in
  flight; one pending offer per MAC (re-offered without a new probe), offers expire after 60 s, pending offers capped at
  half the pool (min 16) so a flood of new MACs cannot take the pool. Receive buffer reused, reply buffers pooled.
- Sockets per OS (`UdpDhcpSocketFactory`): Windows binds the interface address (broadcasts of the interface arrive);
  Linux 0.0.0.0 + `SO_BINDTODEVICE` (root or `cap_net_raw` before kernel 5.7); macOS 0.0.0.0 + `IP_BOUND_IF`; packets
  of other interfaces are dropped via IP_PKTINFO. A loopback binding never sends broadcasts (tests). Injectable
  `IDhcpSocketFactory`; tests use `FakeDhcpNetwork` (in memory) and loopback with random ports, never port 67 on a real
  interface.
- Other DHCP servers (`OtherServerCheck`): a discover with the broadcast flag and a random locally administered MAC from
  UDP 68 (address reuse), offers collected for 3 s, the own address and the own server (it ignores the probe MAC) not
  counted. Runs on Save when enabling (not when already serving on that interface), every 10 min while running and at
  server start. Found on Save: nothing saved, the page asks with the shared confirmation popup "Another DHCP server
  (10.0.0.1) answers on this network. Running two DHCP servers causes address conflicts. Enable anyway?" and saves again
  with the servers confirmed; confirmed servers are stored per interface (`config.acceptedOtherServers`) and never warn
  again: after "Enable anyway" the status says `Running on ...`. Only a server that was not confirmed turns it into a warning.
- Status line (`DhcpStatusTexts`, `ui:StatusChip` left of the Save button, detail as tooltip; plain language, protocol details
  only in the server log): `Running on Ethernet (10.0.0.17/24)`, `Stopped`, `Port 67 is in use by another program`
  (Windows tooltip names the running DHCP Server role or Internet Connection Sharing, via `sc query DHCPServer` /
  `SharedAccess`; AccessDenied on Windows also means in use; Linux: dnsmasq, isc-dhcp-server, NetworkManager + `ss`;
  macOS: bootpd/Internet Sharing + `lsof`), `Insufficient permission to use port 67` (Linux: root or `setcap
  'cap_net_bind_service,cap_net_raw=+ep'`; macOS: sudo), `Interface <name> is not available`, `Range is not inside the
  interface subnet` (the interface address changed; re-checked every 30 s like a failed bind), `Another DHCP server
  answers on this network (<ip>)` (warning), `Address pool exhausted` (warning, 5 min after a request found no address
  or while no address is free).
- Page methods (`DhcpServerMethods`): `getState` (config, status, interfaces with an IPv4 address, per interface the
  derived values, all leases, lease version), `save` (field errors per field; `otherServers` when a confirmation is
  needed), `saveStatic` (add/edit, field errors "Mac", "Address", "Name"), `makeStatic`, `deleteStatic`, `release`
  (forgets a dynamic lease). Events: `state` (status changed), `leases` (changed + removed leases with the lease version,
  batched every 500 ms, split above 2,000 entries; the page ignores versions it already has).
- Page (`DhcpServerView`, subtitle in the host page header via `ui:PageHeader`, status chip left of Save, no card title): Enable
  DHCP server; Listen on (`InterfaceSelection`, IPv4 interfaces only, "Ethernet - 10.0.0.17/24 (Intel I219)"); Start
  address / End address (`ui:FormField`, errors under the fields while typing: "Must be inside the subnet 10.0.0.0/24.",
  "Must be after the start address.", network/broadcast/server address, at most 65,536 addresses); the derived
  line; Save (disabled with the reason as tooltip; status "Checking for other DHCP servers" while saving). Leases:
  summary "6 leases, 2 static", shared `SearchBox`, "+ Static lease"; one virtualized DataGrid (MAC address, IP address
  sorted numerically, Host name / device, Type, Expires "in 23 h" / "Expired" / "Released" / "-", row actions as link
  buttons: Edit, Delete for static; Make static, Release for dynamic; Delete and Release are confirmed). Managed devices
  by MAC = serial number (else by address) show "P3265-V (managed)". Rows update in place; search and structural changes
  rebuild the list with one reset (O(n), 5,000 leases tested).
- Static lease dialog (`StaticLeaseWindow`, `ValidatingViewModel`): MAC address, IP address (inside the subnet, may be
  outside the range, not the server address, not reserved or actively leased by another device), optional name (max 63);
  errors under the fields while typing, the server's answer under the field too.
- Tests: `tests/Oadm.Plugins.DhcpServer.Tests` (codec round trip with all options, overload, concatenation, malformed
  input and random garbage; state machine incl. selecting/other server, init-reboot, renew/rebind, decline, release,
  inform, probe conflicts, offer expiry, pending offer cap, probe concurrency; lease store: allocation, static priority,
  collisions, expiry, reclaim, persistence, 5,000 leases; rate limits; status texts per OS; the whole service on the
  in-memory network: full exchange, unicast renew, live events, restart persistence, page actions, other server +
  confirmation, port in use retried, Windows service named, interface address change, pool exhausted, flood, malformed
  datagrams; real socket on loopback; page view model incl. 5,000 leases; headless screenshots `dhcp-server-page.png`,
  `-page-errors.png`, `-page-other-server.png`, `dhcp-server-other-server-confirm.png`, `dhcp-server-static-lease-dialog.png`).

## PKI plugin (core plugin)

`plugins/Oadm.Plugins.Pki` (+ `.Client`), id `oadm.pki`, rail page **PKI** (icon `shield`). Spec and decisions:
`docs/specs/pki.md`, device API research `tests/Oadm.Plugins.Pki.Tests/Fixtures/`. Part 1: the CA, its page and the server's trust;
part 2: the contributed Security tasks (below), the only writers of the `issued` registry.
- CA store (plugin settings): `ca` (id = SHA-256 of the certificate, source generated/imported, certificate and chain PEM,
  key as PKCS#8 PEM encrypted with `ICorePluginContext.Secrets`, purpose `pki:ca:<id>`), `previousCas` (public parts, at
  most 10, newest first, expired ones dropped), `issued` (`{serialNumber, deviceId, purpose https|dot1x, caId, notAfterUtc,
  issuedUtc, alias}`; per device and purpose the newest counts; `IssuedRegistry` keeps changes in memory and writes at
  most every 2 s and on stop, entries ended more than 30 days ago are dropped), `config`. Without `Secrets` the plugin keeps no CA
  ("CA cannot be stored on this server"); a key that cannot be decrypted (other master key) is the error state "CA key
  cannot be read" with Generate / Import only, nothing is overwritten.
- First start: the default CA "OADM Root CA <machine name>" in the background (status "Creating the certificate
  authority"). Generate (`CaGenerator`, `CertificateRequest` only): RSA 4096, SHA-256 PKCS#1 v1.5, 16 random positive
  serial bytes, NotBefore now - 5 min, BasicConstraints CA (critical), KeyUsage keyCertSign + cRLSign (critical), SKI +
  AKI, 1..30 years (default 10), off the request thread.
- Import (`CaImporter`): PKCS#12 + password (also the Back up file) or PEM certificate(s) + key (in the file or a separate
  PEM: PKCS#8, PKCS#1, SEC1, encrypted PKCS#8), at most 1 MB, password never stored or logged. Checks with field errors
  (`File`, `Password`, `KeyFile`, `KeyPassword`): readable / password right, exactly one certificate with a matching key,
  a CA (keyCertSign when KeyUsage is present), RSA >= 2048 or ECDSA P-256 / P-384, valid now and NotAfter >= now +
  device validity + 1 year ("This CA expires on <date>, before certificates it would issue."). The other certificates on
  the issuer path become the chain.
- Replace (generate or import): without `confirmed` and with an existing CA the reply is `needsConfirmation` with
  `devicesWithCurrentCa` (also for 0 devices, and only after all checks passed, so the page asks after the field
  checks); the page asks "N devices have certificates from the current CA. They keep working, but show 'Issued by a
  previous CA' until they are renewed. Replace the CA?" (only "Replace the CA?" for 0). The old CA moves to
  `previousCas`, the anchors are updated, a `state` event is published.
- Trust anchors: the active CA, its chain and the previous CAs (+ chains) go to `ctx.TrustAnchors`, so device
  certificates they issued are **Trusted** in the device grid without an OS change (lazy, next full refresh).
- Export `.crt` (PEM, with the chain) / `.cer` (DER, CA only), previous CAs as well; Back up = PKCS#12 with key and chain,
  AES-256-CBC + SHA-256, 100,000 iterations, password >= 8 (twice in the dialog), warning popup, logged "CA backup exported".
- OS trust store (`TrustStore/`, `TrustStoreInstallers.ForServer` / `.ForClient`, one class per OS behind
  `IProcessRunner` and `IMachineRootStore`, 30 s per tool, output to the log): Windows LocalMachine Root (client without
  admin: `certutil -addstore Root` with runas), Debian/Ubuntu `/usr/local/share/ca-certificates/oadm-<fp16>.crt` +
  `update-ca-certificates`, RHEL/Fedora `/etc/pki/ca-trust/source/anchors` + `update-ca-trust extract`, SUSE
  `/etc/pki/trust/anchors` + `update-ca-certificates` (client without root: one `pkexec /bin/sh -c` prompt, without pkexec
  the error shows the exact `sudo` command), macOS System keychain `security add-trusted-cert -d -r trustRoot` (client:
  `osascript ... with administrator privileges`), check `find-certificate -Z -a`, remove `delete-certificate -Z <sha1>`.
  Texts: "Permission denied: the server must run as administrator / root", "No supported certificate tool found
  (update-ca-certificates or update-ca-trust)", "<tool> failed: <first line>", "The installation was cancelled.".
  Tests use fakes only; nothing ever touches a real trust store.
- Settings (`config`, `PkiValidation` shared by server and page): `deviceCertValidityDays` 365 (1..3650, shortened to the
  CA's end when issuing, logged), `expiryWarningDays` 30 (1..365), `dot1x.eapolVersion` 1|2, `dot1x.identity`
  mac|hostName|custom, `dot1x.customIdentity` (1..64 printable, placeholders `{serial}` `{hostName}` only),
  `dot1x.radiusCa` oadm|imported + `radiusCaPem` (`importRadiusCa` checks a PEM/DER CA certificate, Save stores it).
- Page methods (`PkiMethods`, camelCase, errors as `{errors: {Field: message}}` or `{error}`): `getState` (status, ca,
  previousCas, config, serverTrustInstalled (checked every call), counts, radiusCa, `simulated` in fake mode), `generate`,
  `import`, `previewReplace`, `exportPublic`, `exportPrevious`, `removePrevious`, `backup`, `installServerTrust`,
  `saveSettings`, `importRadiusCa`; event `state` after every change.
- Page (`HasOwnCards`, subtitle "Issues device certificates for HTTPS and IEEE 802.1X.", header chip "CA valid until
  <date>" ok / "CA expires in N days" warning (within `expiryWarningDays`) / "CA expired", "CA key cannot be read" error):
  cards Certificate authority (name, validity, key, fingerprint selectable, chain of an intermediate, "Trusted root store"
  chips for the server and this computer, toolbar buttons Install in trusted root store, Export public certificate (PEM /
  DER menu), Back up, Generate new CA, Import CA), Device certificates (the two fields; no issued-devices summary line,
  user decision 2026-10-08), IEEE 802.1X (EAPOL version, EAP identity + custom field, RADIUS server CA
  with Import... / View, one Save for both cards), Previous certificate authorities (only when there are any: Name, Valid
  until, Replaced, Export / Remove links, Remove confirmed). Dialogs `GenerateCaWindow`, `ImportCaWindow` (FileRow, key
  FileRow only for a certificate without key), `BackupWindow`: `ValidatingViewModel`, errors under the fields, the
  confirmation popups owned by the dialog. Last export folder per client in
  `LocalApplicationData/Oadm/plugins/oadm.pki/client.json`. Fake mode (`FakeOadmApi.Pki.cs`): in-memory CA + one previous
  CA, server store simulated, this computer never touched.
- Tests: `tests/Oadm.Plugins.Pki.Tests` (generator extensions, importer formats and every check, service with real secret
  protector, event hub and anchor registry: default CA, restart, other master key, no secrets, replace + confirmation,
  previous CAs cap/expiry/remove, export, backup restore, settings, RADIUS CA, status texts; trust store installers per
  OS with the fake runner; page view model incl. dialogs; fake mode; headless screenshots `pki-page.png`,
  `pki-page-imported-intermediate.png`, `pki-generate-dialog.png`, `pki-import-dialog-errors.png`, `pki-backup-dialog.png`);
  `tests/Oadm.Core.Tests/Vapix/TrustAnchorTests.cs` (anchored leaf / intermediate Trusted, self-signed stays, registry,
  lazy re-rating in `CertificatePinning`, host context).

### PKI Security tasks (part 2, contributed by `oadm.pki`)

Context menu group **Security** (icon key), no toolbar, `CanRun` on cached data only: AXIS OS >= 11.11 from the firmware
version (+ network-settings 1.x in `Apis` for 802.1X). Device APIs (`Device/`): REST `cert` v1 (`CertApi`: `config/discover`
must list `cert.v1` released, read fresh before the first write, cached 10 min for queries; else "Needs AXIS OS 11.11 or
later. Nothing was changed."), SOAP web server TLS (`WebServerTls`: `aweb:Get/SetWebServerTlsConfiguration` at
`/vapix/services`, ciphers sent back as read), network_settings.cgi (`NetworkInfoApi`: getNetworkInfo, setWired8021X
Configuration), time.cgi getDateTimeInfo (`DeviceClock`, HTTP Date header as fallback).
- Issuing (`CertificateDeployment.IssueAsync`, `DeviceCertificateIssuer`): key on the device (`create_certificate` RSA-2048 in
  the default keystore) -> `get_csr` -> OADM signs (its own subject `CN=<OADM address>`, SAN IP addresses, host name, FQDN,
  `axis-<serial>.local`; never the CSR's; CSR key RSA >= 2048 or EC; EKU serverAuth / clientAuth; KeyUsage digitalSignature +
  keyEncipherment; AKI = CA SKI; 16 random serial bytes; validity `deviceCertValidityDays` from now - 5 min, capped at the
  CA) -> `PATCH certificates/<alias>`; the entry goes into `issued`. A failure after the key was created deletes the
  unfinished key again. Aliases `OADM HTTPS|802.1X <yyyyMMdd-HHmmss>`, CA `OADM CA <8 hex>`, RADIUS CA `OADM RADIUS CA <8
  hex>`, manual installs `OADM import <ts>`, percent-encoded in URLs. CA certificates are matched by fingerprint (an
  existing alias is reused). "Remove previous OADM certificate" deletes older OADM certificates of the purpose that
  nothing uses (alias prefix + registry serial or the CA as issuer), never others.
- **HTTPS: Enable/Update** (`oadm.pki.https-enable`, no dialog, name "Enable HTTPS"): Check compatibility, Read web server
  settings, Read network settings (Skipped without network-settings), Install CA certificate (Skipped "Already
  installed"), Create key on the device, Get certificate request, Sign certificate, Install certificate, Switch web server
  to the new certificate (policy kept; HTTP only becomes HTTP and HTTPS), Verify HTTPS (`ctx.UpdateDeviceTlsAsync("https",
  fingerprint)`, retried up to 60 s while the web server restarts: OADM pins the new certificate, scheme https), Remove
  previous OADM certificate.
- **HTTPS: Disable** (`oadm.pki.https-disable`, confirmation "Video systems that use HTTPS lose the connection to these
  devices.", name "Disable HTTPS"): Check compatibility (fails when `Network.HTTP.AuthenticationPolicy=basic`: OADM sends no
  Basic over HTTP), Read web server settings, Set HTTP only (policy `Http`, certificates stay; Skipped "Already HTTP only"),
  Verify (`UpdateDeviceTlsAsync("http")`).
- **IEEE 802.1X: Enable/Update** (`oadm.pki.dot1x-enable`, confirmation "Devices on ports that enforce 802.1X become
  unreachable if authentication fails.", name "Enable IEEE 802.1X"): Check compatibility (CA chain must end in a root, the
  RADIUS CA of the page, cert v1, network-settings with `wired.8021X`, identity 1..128), Check device clock (> 5 min off:
  "The device clock is 12 min off. Set the date and time first. Nothing was changed."), Install CA certificates (RADIUS
  server CA + the OADM CA chain), Create key / Get request / Sign / Install certificate, Set 802.1X configuration (enabled,
  EAP-TLS, identity MAC = serial / host name / custom with `{serial}` `{hostName}`, EAPoLv<n>, certClient, certsCA = RADIUS
  CA aliases), Verify 802.1X settings (read back, mismatch = Warning), Remove previous OADM certificate.
- **IEEE 802.1X: Disable** (`oadm.pki.dot1x-disable`, name "Disable IEEE 802.1X"): Check compatibility, Set 802.1X off
  (Skipped "Already off"), Verify; certificates stay.
- **Renew certificates now** (`oadm.pki.renew`, name "Renew certificates"): Check compatibility, Read certificates, Read web
  server settings, Read network settings, then per purpose whose current certificate is OADM's the enable flow with
  prefixed steps ("HTTPS: Create key on the device", "IEEE 802.1X: ..."; 802.1X adds Check device clock and runs only
  while 802.1X is on); otherwise one Skipped step "Renew HTTPS certificate" / "Renew IEEE 802.1X certificate" with the
  reason ("No OADM HTTPS certificate on this device").
- **View installed certificates** (`oadm.pki.view`, dialog only, never a task) and **Delete certificates**
  (`oadm.pki.delete`): `CertificatesWindow` (virtualized DataGrid grouped Client / Server / CA / devices that could not be
  read; MAC address, Address, Name, Issued by, Issued to, Valid to, In use (HTTPS, 802.1X), Source OADM / Other;
  SearchBox; Refresh), read through the query `listCertificates` (read-only, at most 4 devices at a time, progress row,
  rows appear progressively, list replaced as a whole at most every 300 ms). Delete mode: check boxes; in use and Axis
  factory (802.1AR) certificates greyed with a tooltip; Select all / none (O(n)); confirmation popup; payload = aliases per
  device. Task steps: Check compatibility, Read certificates (validates every choice before the first delete: exists, not
  in use, not factory), Delete certificate <alias> (one each), Verify. Names "Delete certificate <alias>" / "Delete N
  certificates" (distinct aliases).
- **Install certificates manually** (`oadm.pki.install`): `InstallCertificatesWindow`: Use for (HTTPS / IEEE 802.1X / CA
  certificates only), .pfx / .p12 files (one password for all, `CertificateFiles`: wrong password under the field), table
  File, Certificate, Device, Status chip; a file must match exactly one selected device by MAC (serial in any notation, also
  `axis-<serial>`), IP or host name / FQDN in CN or SAN, one file per device; problems block Install. Install confirms,
  uploads (`UploadAsync`) and sends `{purpose, password, files[{deviceId, fileId, fileName}]}` (CA only: deviceId empty =
  every device). Steps: Check compatibility, Read certificate file, then HTTPS: Read web server settings, Install
  certificate (`install_from_pkcs12`), Switch web server, Verify HTTPS; 802.1X: Check device clock, Install CA
  certificates, Install certificate, Set 802.1X configuration, Verify 802.1X settings; CA only: Install CA certificates.
  A device without a file ends Done with warnings, nothing sent. Name "Install certificate <file>" / "Install N
  certificates".
- Not done: the device grid's "Trusted (OADM CA)" / "Issued by a previous CA" texts and `expiryWarningDays` in the
  Certificate expires column (OADM certificates already rate **Trusted** through the trust anchors). Task names do not
  distinguish Enable from Update or one renewed purpose (the name is fixed per run, before the device is read).
- Tests (`tests/Oadm.Plugins.Pki.Tests`): parsers and request bodies against the recorded 10.0.0.48 answers (linked from
  `tests/Oadm.Plugins.Pki.Tests/Fixtures`), every task against `FakeCamera` (stateful REST cert v1 with real keys and CSRs, SOAP web
  server, network_settings.cgi, time.cgi) incl. compatibility failures with no writes, clock, chain, cleanup, renew,
  delete refusals, manual install; registry batching; dialog view models (5,000 devices: unit check plus a Perf test with
  190,000 certificates); headless screenshots `pki-view-certificates.png`, `pki-delete-certificates.png`,
  `pki-install-manual.png`, `pki-dot1x-confirm.png`; `tests/Oadm.Client.Tests/SecurityMenuTests` (`pki-security-menu.png`);
  `tests/Oadm.Server.Tests/DeviceTlsTests` (UpdateDeviceTlsAsync with the fake network presenting real certificates).
  `HardwareWriteTests` is an opt-in skeleton (`OADM_PKI_HARDWARE_WRITE=1`, `Category=HardwareWrite`), not written yet.

## Metadata Monitor (core plugin)

`plugins/Oadm.Plugins.MetadataMonitor` (+ `.Client`), id `oadm.metadata-monitor`, rail page **Metadata Monitor** (icon
`activity`). A port of the AXIS Metadata Monitor tool: pick one camera, Start, and watch its event stream live, the way a
technician checks which events a device sends (I/O ports, virtual inputs, storage, temperature, tampering, ACAP events).
Read-only for devices. Decided with the user on 2026-10-08:

| Topic | Decision |
|---|---|
| Who connects | The **server** opens the RTSP metadata stream with the stored credentials (like the live view); passwords never reach the client. |
| Content | **Events only**: exactly `rtsp://<device>/axis-media/media.amp?video=0&audio=0&event=on` (no analytics scene data, no free address field). |
| Cameras | **One camera at a time**; switching the camera stops the running stream. |
| Saving | **No saving**: no export, no capture files. A single message can be copied from the detail view. |

- **Transport (server, `Oadm.Core/LiveView` RTSP client reused):** RTSP/1.0 over TCP to port 554, RTP interleaved, Digest
  with the stored credentials (Basic never on plain RTSP), DESCRIBE / SETUP of the one `application` media
  (`vnd.onvif.metadata`) / PLAY, keep-alive (GET_PARAMETER or OPTIONS before the session timeout), TEARDOWN on Stop.
  RTP payload: XML fragments of one `tt:MetadataStream` document per message, the RTP marker bit ends a document;
  fragments are joined by sequence number, a gap drops the document (counted, "1 message lost"). At most 1 MB per
  document. Devices with status CertificateChanged / CredentialsRequired / PasswordNotSet are refused with the device
  status text. One stream per page session; the server ends it when the page's watch ends (client closed or switched
  page) or after Stop. Reconnect with backoff 1, 2, 4, 8, 10 s on a broken connection, status "Reconnecting".
- **Parsing (server):** every `wsnt:NotificationMessage` in a document becomes one message: topic (`wsnt:Topic`, namespace
  prefixes resolved, `tns1:` / `tnsaxis:` dropped for the tree text: `Device/IO/VirtualInput`), UTC time and
  `PropertyOperation` (Initialized, Changed, Deleted) of `tt:Message`, `Source` / `Key` / `Data` SimpleItems (name, value),
  the raw XML of the notification (pretty-printed for the detail view), and the capture time (server receive time).
  Category = "Event". Malformed XML becomes one message with Category "Invalid" and the raw text.
- **Info text** (like the AXIS tool's Info column): `[INIT] port = 33; active = 0;` = `[INIT]` / `[CHANGED]` / `[DELETED]`
  from the property operation, then every Source, Key and Data item as `name = value;` in document order.
- **Page methods / events (`MetadataMethods`):** `start` ({deviceId}) -> {streamId} or `error` ("The device has no event
  stream", "Unauthorized - HTTP 401", "Unreachable - ..."), `stop`, `keepAlive` (every 5 s while running). Messages are pushed through `ICorePluginContext.Events`
  (topic `messages`, batched every 250 ms, at most 500 per batch; above that the oldest of the batch are dropped and counted)
  and a `state` event (Connecting, Live, Reconnecting, Stopped, Error + text, message count, lost count).
- **Page (client, `HasOwnCards` false: one card):** `ui:PageHeader.Subtitle` "Shows the events a camera sends, live.".
  One toolbar row (fits the 1280 px minimum window with the rail expanded; user decision): camera select (managed video
  and I/O devices, sorted by IPv4, type to jump, "P3265-V (10.0.0.48)"), `ui:SearchBox` filter (live, case-insensitive
  over topic, info and raw XML; "Filter events"), **Clear** (empties the list, the stream keeps running), **Autoscroll**
  toggle (on: the newest row stays visible; scrolling up turns it off, scrolling to the end turns it on again) and the
  count ("134 messages", "5 of 15 messages"); on the right the status chip (Connecting accent, Live ok "Live", plus
  " · 3 messages lost" when documents were lost, Reconnecting warning, Error red with the text) and one primary
  **Start** / **Stop** button.
  List (virtualized DataGrid, `RangeObservableCollection`, batches appended in one step): Seq# (per stream, from 1),
  Timestamp (UTC, the message's UtcTime, `yyyy-MM-dd HH:mm:ss.fff`), Category, Event topic, Capture time (UTC), Property
  operation, Info (star width). No raw Data column (it is in the detail view). The client keeps the newest **10,000**
  messages (oldest removed in one step).
  Detail (below the list, splitter, height persisted per client): the selected message's XML in `ui:CodeView` (Xml,
  always pretty-printed and highlighted, no Raw switch: user decision) with a copy button (copies the pretty XML).
- **Scale:** a camera can send hundreds of Initialized messages at Start (every virtual input); batching, one collection
  change per batch and the virtualized grid keep the UI responsive; filter is O(n) over at most 10,000 rows. Test with
  10,000 messages and a burst of 2,000 in one batch.
- **Tests:** RTP metadata depacketizer from recorded packets of 10.0.0.48 (`OADM_RECORD_RTP_DIR`, read-only stream), XML
  parsing incl. namespaces, multiple notifications per document, malformed XML, Info text; server stream lifecycle with a
  fake RTSP source (start, stop, reconnect, refused statuses, watch ended); page view model (filter, clear, autoscroll,
  10,000 cap, selection survives batches); headless screenshots `metadata-monitor-page.png` (live list + detail),
  `-error.png`; read-only hardware test against 10.0.0.48 (Start, at least one Initialized message, Stop).
- **Implementation:** SDK `IDeviceEventStreams` on `ICorePluginContext.EventStreams` (DIM null; `IDeviceEventSource`,
  `DeviceMetadataDocument`, `DeviceStreamException` with Unreachable / Unauthorized / NotSupported / Protocol), implemented
  by `Oadm.Core.LiveView.DeviceEventStreams` (device address + `CredentialStore`) on `RtspMetadataSource` (Core
  `LiveView/Rtsp`: the live view's `RtspClient`, `SdpMetadataTrack` for the `m=application ... vnd.onvif.metadata` media,
  OPTIONS keep-alive on a timer every half session timeout (at most 10 s), a silence watchdog (no packet and no keep-alive
  answer for 2 x keep-alive + 5 s = connection lost; event streams can be quiet for minutes, `RtspClient.SinceLastReceive`))
  and `Rtp/MetadataDepacketizer` (a gap drops the document it hits; a packet after a gap is kept only when it starts a new
  XML document). 10.0.0.48 (AXIS OS 12.11) sends one document per RTP packet, one notification per document, an empty
  `tt:MetadataStream` first and 128 Initialized messages at PLAY (64 virtual inputs, PTZ, storage, temperature, ...),
  the first about 2.6 s after Start through the server. Plugin: `MetadataMonitorPlugin` (sessions per page,
  `MetadataMonitorOptions`), `Streaming/MonitorSession` (read loop, reconnect, batches split below the 1 M character event
  limit, raw XML per message cut at 256 K characters), `Parsing/MetadataParser` (XDocument without DTDs, prefixes resolved
  by namespace: `tns1:` / `tnsaxis:` dropped, other prefixes kept). Device refusals use the snapshot report's status
  texts ("Credentials required - the device rejects the stored credentials", ...). A page's stream also ends without its
  keep-alive: the page calls `keepAlive` ({streamId}) every 5 s while a stream runs, the server stops streams without one
  for 20 s (client closed or gone; switching pages stops the stream at once). Page layout: two rows (camera search,
  camera select, then status chip + one primary **Start** / **Stop** toggle on the right; below: filter, Clear,
  Autoscroll, message count on the right) because one row does not fit the 1280 px minimum window. Autoscroll follows the
  grid's vertical scroll bar (wheel and bar: at the end on, above off); the selection is kept through the grid's Reset of
  each batch. Detail height in `LocalApplicationData/Oadm/plugins/oadm.metadata-monitor/client.json`; copy = the shown
  text (pretty or raw) through the TopLevel clipboard. Fake mode (`FakeOadmApi.MetadataMonitor.cs`): generated burst
  (64 virtual inputs, digital input, storage, temperature) then a change every 1.5 s, statuses refused like the server.
  Test project `tests/Oadm.Plugins.MetadataMonitor.Tests` (parser, lifecycle with `FakeEventStreams`, view model, fake
  mode, headless screenshots, Perf `ScaleTests`, hardware `MetadataHardwareTests` through the in-process server and gRPC
  Watch); Core: `MetadataDepacketizerTests`, `RtspMetadataSourceTests` (scripted RTSP server), `DeviceEventStreamsTests`,
  `MetadataRecorderTests` (Hardware; re-records `Fixtures/LiveView/events.sdp` + `.rtp` with `OADM_RECORD_RTP_DIR`).

## Date and time plugin

`plugins/Oadm.Plugins.DateTime` (+ `.Client`), id `oadm.datetime`, context menu (group Maintenance, icon `clock`)
**Date and time**: a clone of the ADM / AXIS Camera Station "Set date and time" dialog (the ADM manual has no date and
time chapter; wording from the ACS 5 manual, sources in the plugin `README.md`), for any number of devices.
- Dialog "Set date and time" behaves **exactly like ADM** (user decision): no "Keep unchanged" anywhere, OK always
  writes the time zone and the selected time mode to every selected device. **Device time** card for the first selected
  device (read-only query `getTimeSettings`: device time and offset, time zone, time mode with sync state, server time and
  difference); **Time zone** card: the 313 IANA zones of AXIS OS 12.11 (bundled list, offsets from the OS time zone
  database, read from this year's offset change instants so all three OS agree (`ZoneYear`); a zone the OS does not
  know, e.g. Antarctica/Troll on Windows Server 2025, is listed last without offset and set by IANA id only) in a DataGrid (UTC offset, City, Time zone, DST) with `ui:SearchBox`, "Automatically adjust for daylight
  saving time changes"; **Time mode** card: Synchronize with server computer time (NTP off, the OADM server's UTC sent
  once per device at execution time, the devices get the server's time zone, the zone list is disabled), Synchronize
  with NTP server (Obtain from DHCP / Use servers, up to 5, one per line; Use NTS with NTS KE servers on ntp 1.5+), Set
  manually (date + time in the device's zone, NTP off). Defaults when the dialog opens: time zone = the first device's
  current IANA zone (the OADM server's zone when the device has none, e.g. a POSIX zone), time mode = the first device's
  mode (NTP if enabled, otherwise Set manually). OK (disabled with the reason as tooltip until a zone is chosen and every
  field is valid; field errors below their inputs, the zone error directly below the list); device notes are O(n)
  summaries of the cached API lists ("500 of the selected devices have no Time API ..."). Devices without the Time API
  fail setting the clock with "Nothing was changed" (no legacy date.cgi).
- APIs: time-service 1.x (`getDateTimeInfo`, `setTimeZone`, `setPosixTimeZone` for DST off, `setDateTime`), ntp 1.x
  (`getNTPInfo`, `setNTPClientConfiguration`; NTS from 1.5), param.cgi `Time.*` for older firmware (time zone as POSIX,
  one NTP server; no date and time without the Time API; date.cgi is not used). Decision table and what 10.0.0.48
  reports: plugin `README.md`. Payload (`DateTimePayload`): `timeZone` (required except server time), `mode` (Ntp,
  ServerTime, Manual; required), `ntp`, `manualDateTime`, `daylightSaving`, `timeZoneUnchanged` (name hint). Values the
  device already has are skipped; every write is verified by reading again (Warning on a mismatch or a clock more than
  3 s off).
- Task names (`GetTaskName(payloadJson)` on the plugin class): "Change date and time", or the most specific name when
  only the time mode differs from the device (one device whose zone and DST are kept, `timeZoneUnchanged`): "Set NTP
  servers 10.0.0.17, pool.ntp.org", "Set NTP servers from DHCP", "Set NTS KE servers ...", "Set date and time
  2026-10-07 18:00"; "Sync with server time" for server time mode.
- Tests: `tests/Oadm.Plugins.DateTime.Tests` (request bodies per API version, recorded 10.0.0.48 fixtures, validation,
  time zones and POSIX conversion, step sequences against a stateful fake device, view model with 5000 devices, headless
  screenshots `datetime-dialog-single.png`, `-multi-errors.png`, `-server-time.png`, `-nts-error.png`).

## Applications (ACAP) plugin

`plugins/Oadm.Plugins.Acap` (+ `.Client`), id `oadm.acap`, context menu (group Applications) "Applications (ACAP)",
dialog. Lists installed applications (query `listApplications`), start/stop/remove (remove asks for
confirmation) and install/upgrade of an uploaded `.eap` on all selected devices. Uses the classic
Application API (`application 1.x`: list/upload/control/config.cgi). Before uploading it reads the
package manifest and refuses (nothing changed) on architecture mismatch, AXIS OS outside the
package's range or below its manifest schema minimum, root apps on AXIS OS 12+, and downgrades
without the explicit option. Decision table and research in `plugins/Oadm.Plugins.Acap/README.md`.

# Settings

Server-side in `Setting`. Goal 1 keys: `Polling.IntervalSeconds` (60, 5..86400),
`Polling.FullRefreshMinutes` (10, 1..1440), `Scan.Parallelism` (32), `Scan.TimeoutMs` (1500),
`Discovery.ZeroConfSeconds` (30, 5..300: a zero-conf scan of the add page ends after this time; Settings
page "Zero-conf scan duration (s)"), `Server.Name` (hostname),
`Server.ListenUrl`, `Uploads.MaxMegabytes` (2048, 1..65536), `Uploads.RetentionHours` (24,
1..8760; both server-only, not on the settings page yet), `Tasks.RetentionDays` (90, 0..3650, 0 = no age
limit) and `Tasks.MaxHistory` (50000, 0 or 100..1000000, 0 = no limit): task history retention, applied
one minute after start and then hourly by `TaskRetentionHostedService` (finished tasks only, one bulk
delete, one Removed change per task; server-only, not on the settings page yet),
`Tasks.MaxParallelPerPlugin` (16, 1..256: how many tasks (= devices) of one task plugin run at the same
time, read live by the task engine, plugin `MaxParallelDevices` can only lower it; proto
`optional int32 max_parallel_tasks_per_plugin = 9` so a partial `Set` keeps it; Settings page "Parallel
tasks per plugin" with the hint "How many devices a task runs on at the same time, e.g. restarts or
firmware updates."), `Devices.UseHostName` (bool, false: add devices by host name when one is
known, otherwise by IP address; proto `optional bool use_host_name = 7` so a partial `Set`
keeps it). Settings page in the client exposes them (one card **Server**, the only card of the page; operators see it
read-only); `Devices.UseHostName` is the checkbox
"Use host name when available, otherwise IP address". Rail page **Credentials** (Admin only, subtitle "Passwords OADM
tries when it adds devices.", `Settings/CredentialsViewModel` + `CredentialsView`, one card without title): entries (key icon, user name, password masked as 8 bullets, eye icon button "Show password" /
"Hide password" that loads it with `RevealCredential` and masks (and forgets) it on the second click, copy icon
button "Copy password" (clipboard of the window, loaded on demand, not shown), added time, Remove), add form (user
name, password, "Add credential"); stored encrypted on the server (`CredentialListStore`, table
CredentialListEntries), tried on every discovered device (see "Add Devices Page"). Rail page **About** (everyone,
subtitle "Version and licenses.", `Settings/AboutPageView` around the card `Settings/AboutView`, `AboutViewModel`):
terms of use, client version, server version (`ServerSettings.server_version`), the
sentence on the Apache-2.0 license, "Show licenses" shows `THIRD-PARTY-NOTICES.txt` (next to the exe, the macOS app's
`Contents/Resources`, or `THIRD-PARTY-NOTICES.md` in a checkout) in a read-only `ui:CodeView` (Plain). There is no "This client" card:
the server address is set with `--server` or in the client settings file (user decision 2026-10-08). Client-side
(local JSON in LocalApplicationData): server address, device grid column layout, bottom pane state.

# Security

- Device passwords: AES-256-GCM, key in `<datafolder>/master.key` (0600 on Unix), random
  nonce per record, stored as `nonce|ciphertext|tag`. OS keyring integration is a later goal. A missing or different
  key is replaced at startup (warn and continue, `Security.KeyCheck`, see "Production hardening" 3).
- Device credentials never leave the server; gRPC returns only "has credentials". The credential list
  (same AES-256-GCM, entry id as associated data) lists only ids and user names; the one exception is the
  explicit reveal of a credential list entry (`SettingsService.RevealCredential`, Credentials page eye / copy
  button; user decision 2026-10-08; Admin only and in the audit log, see "Production hardening"). Credentials the
  technician types for a login (RetryAuth) or a first password travel
  only client -> server.
- Clients log in over TLS with users and roles (see "Production hardening"); the server binds to all interfaces so a client on another
  machine can connect.
- Never log passwords or digest headers.

# Production hardening (decided with the user on 2026-10-08)

Source: the overall and security audits of 2026-10-08 (local, not in git). Every point below is a user decision; details
marked *(default)* were filled in and can be changed. This section wins over older text in this file where they differ.

## 1. Access: TLS, users, roles, login (replaces "No authentication in Goal 1")

- **TLS on the gRPC endpoint.** On first start the server creates a self-signed certificate (ECDSA P-256, CN = server
  name, SAN = host name + all interface addresses + localhost, 20 years) and keeps it in the data folder
  (`server-tls.json`: certificate PEM + private key encrypted with the master key). Kestrel serves HTTP/2 over TLS on the
  listen URL (default `https://0.0.0.0:5080`; an `http://` URL is still accepted for tests and logs a warning). The
  client pins the certificate's SHA-256 per server on first connect (trust on first use, the fingerprint is shown and
  confirmed in the login window) and refuses a changed certificate with "The server certificate changed" (Forget server
  re-pins). *(default)* Regenerating the certificate is a server command line option `--Oadm:RegenerateTlsCertificate`.
- **Users and roles.** Table `Users` (Id, UserName unique case-insensitive 1..64, PasswordHash = PBKDF2-SHA256 with
  210,000 iterations and a 16-byte salt, Role Admin | Operator, Disabled, CreatedUtc, LastLoginUtc). Passwords: no
  strength rule, only not empty (user decision). **First administrator**: while no user exists, the login window offers "Create the
  first administrator"; the server accepts this only from a loopback client, or from a remote client that enters the
  one-time setup code the server writes to `<datafolder>/setup-code.txt` (admin-only file) and logs at startup *(default)*.
- **Tokens.** `AuthService.Login(user, password, remember)` returns an opaque random token (32 bytes, base64url); the server
  stores only its SHA-256 with user, client address, expiry (8 h sliding, 30 days with "Remember me") *(default)*. Clients
  send `authorization: Bearer <token>` in gRPC metadata (the contracts stay unchanged, see Architecture). A server
  interceptor answers UNAUTHENTICATED without a valid token (only `AuthService.Login`, `AuthService.Status` (server name,
  version, "no users yet") and first-admin setup are open) and PERMISSION_DENIED for a missing role. `Logout` revokes.
  Failed logins: 5 per user per 5 minutes, then 5 minutes locked, logged *(default)*. Task `Owner` = authenticated user
  name + "@" + the client machine name the client sends (no longer whatever the client claims).
- **Roles.** **Admin**: everything. **Operator**: devices (watch, add, remove, refresh, credentials of a device, web UI
  link), discovery, tasks (run, cancel, delete own and others; not Delete all), live view, uploads, plugin pages that do
  not change server configuration (Snapshot report, VAPIX Commander, Metadata Monitor, PKI read and device
  certificate tasks). **Admin only**: `SettingsService.Set`, credential list (add, remove, reveal), users, `TaskService.DeleteAll`,
  PKI (generate, import, backup, export, install in the server root store, PKI settings), DHCP and NTP save / static
  leases / release. Core plugins declare their method roles through a new SDK member
  `ICorePlugin.RequiredRole(string method)` (DIM default Operator); the host checks it before `InvokeAsync`.
- **Credential reveal**: Admin only, every reveal in the audit log; without a login (no users yet) it is refused.
- **PKI "Install in trusted root store" on the server**: Admin only; the confirmation shows the CA's SHA-256 fingerprint;
  logged in the audit log. The client-side install is unchanged.
- **Login window (client).** Shown at start: Server (default `localhost:5080`, remembered, list of recent servers), User
  name, Password (`ui:PasswordBox`), "Remember me" (keeps the token in the client settings, never the password), Log in.
  First connect to a server: fingerprint confirmation. No users yet: "Create the first administrator" (+ setup code for
  remote servers). The rail shows the logged-in user with Log out at the bottom. Replaces the removed "This client"
  card: the server address is chosen here. `--server` still preselects it. Fake mode has no login (user "admin").
- **Users page** (rail page **Users**, Admin only, subtitle "Who can log in to this server and what they may do."): list (user, role, last login, disabled), add, change role,
  reset password, disable, delete; never the last enabled admin, never yourself.
- **Audit log.** Table `AuditEntries` (TimeUtc, UserName, ClientAddress, Action, Target, Detail; retention 365 days or
  200,000 entries *(default)*). Logged: login ok/failed, logout, user changes, settings changes, credential list
  add/remove/reveal, PKI actions, DHCP/NTP save, task runs (plugin, device count), Delete all, device remove, VAPIX
  Commander send / rollout. Logs page gets an **Audit** tab (Admin only, virtualized, SearchBox).
- **Implementation** (done 2026-10-08):
  - Server: `Oadm.Server.Auth` (`AuthInterceptor` on every call sets `Oadm.Core.Auth.CallerContext` for the call, so
    `TaskEngine.RunAsync` and core plugin task runs take the owner "user@machine" from it; `AccessPolicy` role table;
    `AuthGrpcService`, `UserGrpcService`, `AuditGrpcService`; `ServerTlsCertificate` (`server-tls.json`, key AES-GCM
    with the master key, purpose `oadm:server-tls`; on Windows instead one persisted named CNG key "OADM Server TLS <hash of the data folder>" (machine key store as a service, else the user store; not exportable; opened by name on every start, overwritten on regenerate, so no start leaves a key file behind) and the file holds its name; a key that cannot be decrypted or opened, e.g. after a master key
    replacement, creates a new certificate and logs that clients must use Forget server); `AuthMaintenanceHostedService`
    hourly: audit retention, expired sessions; `InProcessAccess.CreateTokenAsync` for in-process hosts (tests, hardware
    tests)). Core: `Oadm.Core.Auth` (`UserStore`, `PasswordHasher`, `AuthTokenStore` (table AuthTokens: token SHA-256,
    user, client address, remember, expiry; sliding expiry cached in memory, written at most once per minute),
    `LoginThrottle`, `AuthManager` (login, first administrator, setup code), `AuditLog`); migration `UsersAndAudit`
    (Users, AuthTokens, AuditEntries). A stored listen URL equal to the old default `http://0.0.0.0:5080` reads as the
    new https default.
  - Plugin methods: the host checks `ICorePlugin.RequiredRole(method)` in `PluginService.Invoke` (a throwing plugin
    counts as Admin) and audits calls where `ICorePlugin.IsAudited(method)` (DIM: the Admin methods) as action "Plugin
    action", target the plugin name, detail the method. Admin: PKI everything except `getState`, DHCP `save` /
    `saveStatic` / `deleteStatic` / `makeStatic` / `release`, NTP `save`. VAPIX Commander stays Operator; `tryRequest`
    and `rollout` are audited. The PKI page asks before "Install in trusted root store" with the CA's SHA-256
    fingerprint (`ui:MessageWindow`).
  - Client: `Shell/AppShell` shows `LoginWindow` (`LoginViewModel`: server as editable select with the recent servers,
    user name, password, confirm password + setup code in first-administrator mode, Remember me; errors below the fields,
    connection problems below the server field) before the main window, and again after Log out or when a call answers
    UNAUTHENTICATED ("Your session has ended. Log in again."). TLS trust: `Oadm.Contracts.Security.ServerCertificatePinning`
    (pins in `client-settings.json` `PinnedServers` by "host:port"; `OadmChannel` creates the pinned channel and adds the
    token); the fingerprint is confirmed in the shared confirmation window ("Trust this server?", two lines of 16 pairs);
    a changed certificate shows "The server certificate changed ..." with **Forget server**. Remember me keeps
    {user, token} per server in `RememberedLogins` and resumes with `AuthService.Me` at start. An address without scheme
    means https; `http://` stays possible for tests. Rail bottom: user (tooltip "Logged in as ... (role) on ...") and
    Log out. `UserSession.IsAdmin` hides the Users and Credentials rail pages, disables the server settings form (with the
    sentence "Only administrators can change server settings.") and hides the Audit tab for operators; the server checks again.
    Users page `Settings/UsersPageView` around the card `Settings/UsersCardView` (loaded on every connect; DataGrid; Make administrator / operator, Reset password (inline editor), Disable
    / Enable, Delete with confirmations; the own row offers only Reset password). Audit tab `Logging/AuditLogViewModel`
    (newest 10,000, O(n) search, Refresh). Fake mode signs in as "admin" (Administrator) without a login window.
  - Tests: `tests/Oadm.Server.Tests/AuthTests` (unauthenticated / operator / admin per service, role table against the
    contracts, plugin method roles + audit, first administrator loopback and setup code, login / logout / owner,
    throttling, session expiry, user rules, audit entries and retention), `TlsPinningTests` (real Kestrel with TLS:
    TOFU, restart keeps the certificate, changed certificate, regenerate); `MethodRoleTests` in the DHCP, NTP, PKI and
    VAPIX Commander test projects; `tests/Oadm.Client.Tests/LoginTests` (login, wrong password, first administrator,
    fingerprint trust / refusal / changed / Forget server, remembered session, pin store, Users page, Audit tab with
    5,000 entries; headless screenshots `login-window.png`, `login-first-admin.png`, `login-fingerprint-confirm.png`,
    `client-users-page.png`, `logs-audit-tab.png`). `TestServerHost` calls as the administrator "admin"
    (`InvokerFor(token)` / `InvokerForUserAsync(name, role)` for others, `createAdmin: false` for an empty user table,
    PBKDF2 with 1,000 iterations for speed).

## 2. Automatic login on the add page

- **Axis check first** (user decision): before any credential is sent, the device must answer the anonymous VAPIX
  `basicdeviceinfo.cgi` `getAllUnrestrictedProperties` with a valid Axis answer (12-hex serial number, ProdNbr). Only
  then OADM logs in: **HTTPS first** (Digest, then Basic), then HTTP (Digest, then Basic). User decision: such a verified
  Axis device may get Basic over plain HTTP when it offers nothing else. Known residual risk (recorded on the user's
  request): a device that imitates the anonymous answer passes the check.
- **Candidates: credential list only** (+ credentials typed in the session). Passwords of managed devices are never
  tried on new devices. At most 10 rejected credentials per device (unchanged).
- Implemented in `DiscoveryAuthenticator` (`Judge`, `LoginSchemes`) and `VapixConnectionOptions.AllowBasicOverHttp`
  (set only there); details in "Add Devices Page". Tests: `AxisCheckTests` (every failing answer: 404, no serial, no
  ProdNbr, other serial, 401: no credential request at all, also for RetryAuth; HTTPS first with the check before the
  first credential; HTTP only with Basic allowed), `FastAddTests.PasswordsOfManagedDevicesAreNeverTriedOnNewDevices`,
  read-only hardware test `FastAddHardwareTests.TheRealCameraPassesTheAnonymousAxisCheck` (10.0.0.48 passes on both
  schemes).

## 3. Data safety

- `master.key` missing or not matching (a key check value is stored in `Setting` `Security.KeyCheck` = HMAC of a fixed
  label): **warn and continue** (user decision). The server creates a new key, logs an error naming how many device
  credentials and credential list entries became unreadable, deletes those unreadable rows, sets the affected devices to
  CredentialsRequired, and the client shows a banner "The server's key was replaced: N devices need their credentials
  again" until dismissed. The PKI shows its existing "CA key cannot be read" state.
- Implemented in `Oadm.Core.Security.MasterKeyCheck`, run by `DatabaseInitializer` after the migrations (before
  anything is served). Key check = base64 HMAC-SHA256 of "OADM master key check v1" with the key
  (`CredentialProtector.ComputeKeyCheck`), stored as JSON string. Replaced when: master.key is missing, has the wrong
  size, or does not match the stored value; without a stored value (data of older versions) when encrypted rows exist
  and none of them decrypts. An existing file is never deleted: it is renamed to `master.key.replaced-<UTC time>` before
  the new key is written (`MasterKeyFile.Replace`); the running `CredentialProtector` switches to the new key. One
  transaction deletes the unreadable `DeviceCredentials` and `CredentialListEntries` rows, sets those devices to
  CredentialsRequired, stores the new check value and the notice `Security.KeyReplaced` ({id, replacedUtc, devices,
  credentialListEntries}); the error log names the counts and that plugin secrets of the old key (saved command
  passwords, CA keys, the TLS key) cannot be read either. Server -> client: `ServerSettings.key_replaced = 20`
  (`KeyReplacedNotice` id, message, devices, credential_list_entries, replaced) in every `SettingsService.Get`/`Set`
  reply. The client reads it on every connect and shows it as a warning banner (`Border.banner.warning`, the shared
  banner style, text + "Dismiss" link) below the connection banner; dismissed ids are kept in the client settings
  (`DismissedKeyNotices`), so a later replacement shows again. Banner text: "The server's key was replaced: 3 devices
  need their credentials again." (+ ", 1 credential list entry was removed."). Tests: `MasterKeyCheckTests` (same key,
  missing / different / wrong-size key: counts, rows removed, statuses, backup file, new key checked on the next
  start; legacy data without check value), `MasterKeyFileTests`, `KeyReplacedNoticeTests` (gRPC),
  `CrashHandlingTests.The_key_replaced_banner_shows_until_dismissed_on_this_client`.

## 4. Robustness

- **Client crashes**: plugin dialogs, pages and toolbar plugins run inside try/catch; an error shows in `ui:MessageWindow`
  ("The <plugin> dialog failed: <message>") and the client keeps running. Global handlers
  (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Dispatcher.UIThread.UnhandledException`)
  log every error with the log file path; only truly fatal errors end the app, after logging.
  Implemented: `TaskPluginRunner` (dialog: "The <name> dialog failed: <message>", nothing runs),
  `MainWindowViewModel.SyncCorePluginPages` (page: the rail entry stays, the page shows "The <name> page failed:
  <message>" and the message window says the same; the other pages load), `DeviceToolbar` (entry left out, "The
  toolbar entry <id> failed: <message>"). `Infrastructure/CrashHandling` holds the three handlers, installed by Program
  only (the process handlers before Avalonia starts, the UI handler in `AfterSetup`; tests see their exceptions): every
  error is logged with today's client log file (`<datafolder>/logs/client-yyyyMMdd.log`); a UI thread error is marked
  handled and shown once at a time in `MessageWindow` ("Something went wrong", the message, "OADM keeps running. Details
  are in the client log: <path>"); fatal errors (out of memory, access violation, invalid program, bad image) are logged
  as Critical and end the app; unobserved task errors are logged and observed. Tests: `CrashHandlingTests`.
- **Limits**: HTTP answers from devices at most 16 MB (firmware and ACAP uploads exempt from the request side only), XML
  documents at most 1 MB with DTD processing prohibited, RTSP / video access units at most 8 MB, uploads: 10 GB of disk
  for all uploads together (user decision) and at least 1 GB free disk, oldest uploads removed first, RESOURCE_EXHAUSTED
  otherwise.
  Implemented: `VapixClient.MaxResponseBytes` (HttpClient `MaxResponseContentBufferSize`; over it
  `VapixResponseTooLargeException` "The device answer is larger than 16 MB and was not read."); every XML from a device
  goes through the SDK `Oadm.Sdk.Vapix.DeviceXml.Parse` / `ParseElement` (1 MB of characters, DTD prohibited, no
  resolver; callers' settings cannot lift the limits; ACAP, PKI, Metadata Monitor, VAPIX Commander; `CodeView` keeps
  its own 512 K plain-text limit with DTD prohibited); `NalDepacketizer.MaxAccessUnitBytes` (8 MB: a larger access
  unit is discarded at once and dropped like a damaged one, the stream resumes at the next keyframe,
  `OversizedAccessUnits`); `UploadStore.QuotaBytes` / `MinFreeDiskBytes`: a new upload reserves its declared size
  (uploads in progress count), removes the oldest finished uploads (by file time) until the quota and the free disk
  space fit, else `UploadRejectedException { TooLarge }` = RESOURCE_EXHAUSTED; a single upload may still be up to
  `Uploads.MaxMegabytes`, so a large one removes older ones. Free space unknown (network share): only the quota applies.
- **VAPIX Commander**: every request must go to the device's own address: paths with `\`, `//` or an authority are
  rejected; `VapixClient.SendAsync` refuses absolute URIs to another host.
  Implemented: `CommandValidator.IsDevicePath` (starts with one "/", no "\", no "//" anywhere, no "@" before the query,
  no "..", no spaces or control characters; field error `CommandValidator.PathProblem` "The path must start with / and
  stay on the device: no "..", "//", "\" or host name."), checked again by `CommandRenderer` after the field values
  are filled in. `VapixClient.SendAsync` throws `ArgumentException` before sending when the resolved URI has another
  scheme, host or port, user info, or a relative URI contains "\" (relative "/path" parsed as an implicit file URI on
  Unix counts as relative, like HttpClient). Tests: `DeviceLimitsTests`, `DepacketizerTests`, `UploadStoreTests`,
  `RendererTests` (paths `/\/host`, `//host/x`, absolute URL, and the client refusing other hosts).

## 5. Installation and release

- **Firewall (Windows)**: the MSI opens only TCP 5080, profiles Domain and Private. The NTP and DHCP plugins add their UDP
  rule (123 / 67, Domain and Private) when enabled and remove it when disabled (`netsh advfirewall`, the server runs as
  SYSTEM); Linux and macOS unchanged. *Implemented:* SDK `ICorePluginContext.Firewall` (`IFirewallRules`, null unless
  the server runs as the Windows service) and `FirewallRuleKeeper` (first sync always applies, so a rule left by a crash
  is removed at start; later only changes; errors logged, never fatal, retried on the next change). Rules
  `FirewallRule.ForService("NTP", Udp, 123)` = "OADM Server (NTP, UDP 123)" and "OADM Server (DHCP, UDP 67)", synced in
  the services' ApplyAsync and closed on stop. Server: `NetshFirewallRules` (`delete rule name=...` then `add rule
  name=... dir=in action=allow protocol=UDP localport=123 program=<exe> profile=domain,private enable=yes`; delete
  ignores "No rules match"), commands through `ICommandRunner` (fake in tests).
- **Folders** (user decision: service account stays SYSTEM / root): the server checks at start that its data folder,
  runtime extraction folder and every plugin folder it loads are writable only by admins / root (Windows ACL, Unix owner
  root and no group/world write); a folder that is not is fixed when possible (Windows: ACL reset by SYSTEM) or skipped
  with an error (plugins) / refused (data folder). The development plugin folder (`artifacts/plugins`) is only used when
  the server does not run as an installed service. *Implemented:* `Oadm.Server.Hosting.FolderGuard` over
  `IFolderPermissions` (fake in tests): `WindowsFolderPermissions` (System.Security.AccessControl; owner and every
  entry below must be SYSTEM / Administrators / TrustedInstaller, no allow entry with write, append, delete, change
  permissions, take ownership or generic write/all for anyone else, CREATOR OWNER allowed; fix: owner Administrators,
  protected ACL SYSTEM + Administrators full control (+ Users read and execute for plugin folders), children reset to
  inherit) and `UnixFolderPermissions` (`find <dir> ! -type l ( ! -user 0 -o -perm -0020 -o -perm -0002 )`; fix
  `chown -R 0:0`, `chmod -R go-w`). Step 0 of `OadmServerHost.StartAsync`: data folder and
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` (unless inside it), an unfixable one throws; `PluginLoader.FolderCheck` skips an
  unfixable plugin folder with a load error. Only in service mode.
- **Installer choice**: each installer offers "Server and client" (default) or "Client only": MSI features (Server
  optional), `.deb` split into `oadm-server` and `oadm-client` (+ metapackage `oadm` depending on both), `.pkg`
  choices. *Implemented*, details in "Packaging".
- **Licenses**: at publish a complete notices file is generated from every NuGet package (license expression or file
  from the package) and the bundled assets (FFmpeg LGPL-2.1 full text, Inter OFL, .NET runtime notices); `LICENSE`,
  `THIRD-PARTY-NOTICES.txt` and the LGPL text ship in all three installers. The client gets an **About**
  page (rail): terms of use, version, server version, license texts. *Implemented* (`tools/Oadm.Notices`, see
  "Packaging"; About page, see "Settings").
- **One release workflow** (`release.yml`, on tags `v*.*.*` only, replaces ci.yml and package.yml): tests on Windows,
  Linux, macOS (timeout 30 min, hang detection) -> the three installers -> the GitHub release, only when everything
  passed. v0.0.1 stays a normal release (user decision). *Implemented*, plus `SHA256SUMS.txt` in the release.
- **Not now** (user decisions): installer signing (SHA-256 checksums are published with each release instead),
  least-privilege service accounts, database backup before migrations.
- **Hardware write tests**: done by the user by hand on a spare camera.

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
  `src/Oadm.Client/Controls`. Existing shared ones: `IconLabel` (every icon + text row: navigation
  rail, dialogs), `ToolbarButton` (every toolbar button) and `ToolbarSeparator`, `SearchBox`
  (every search field), `OadmIcon`, and for
  dialogs `DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip` (status chip),
  `FileRow` (chosen file + "Choose file"), `ProgressRow` (progress bar + status text),
  `MessageWindow` (every message box and confirmation popup, host and plugins; dialogs never show
  inline "I understand" risk sections), `PasswordBox` (every password field: TextBox with bullet mask and eye button "Show password" /
  "Hide password", icons `Icon.eye` / `Icon.eyeOff`; never a `TextBox` with `PasswordChar`), `CodeView`
  (every read-only code / response display: JSON and XML pretty-printed with 2 spaces and highlighted,
  param.cgi `key=value` highlighted with `# Error` lines red, `Language` Auto/Json/Xml/KeyValue/Plain with
  Auto from the content type then the text, `IsFormatted` false = exact text; unparsable text plain, above
  512 K characters plain; monospace `Oadm.FontFamilyMono`, no wrapping, selectable and copyable; colors
  `Oadm.Code.*Brush`; logic in `CodeText` without UI; editable bodies stay `TextBox.code`), `FormField` (every labeled form row: label,
  input, error or hint below the input; classes `wide` and `inline`). Before
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
  `ShowAll`, `Reset`, `SetServerError`); errors show once the field was edited or a submit was tried, never on an
  untouched form; submit buttons stay disabled while any error exists and their tooltip says why. Usage:
  `plugins/README.md` "Validation".
- **HARD RULE, scale to thousands of devices.** A site can have 1000+ cameras. Every list of
  devices, discovered devices, tasks, sources or tiles is virtualized (DataGrid, virtualizing
  ListBox/ItemsRepeater; never an ItemsControl/StackPanel creating one control per item), search,
  filter, sort and select-all are O(n) without per-item UI work, server calls are batched (one call
  for many devices, bounded parallelism on the server), per-device work is lazy for visible rows
  or summarized ("4,812 compatible, 188 missing API"), and images/snapshots load only for visible
  tiles. Every feature with a device list has a test with at least 5000 fake devices that keeps
  filtering/selection/summary fast. Target size: **5,000 devices and 50,000 tasks in the history**
  (audit, numbers and fixes: `docs/scale-audit.md`). Concretely:
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
  - Server: per-device work has bounded parallelism and is spread over time (polling); writes for many
    devices or tasks are one transaction (`RemoveManyAsync`, `SetManyAsync`, `AddRangeAsync`,
    `DeleteManyAsync`); lists that can grow without bound are paged or limited (task history, Watch
    snapshot) and have a retention; derived per-device results are cached per device-table version.
  - gRPC: never one message whose size grows with the device or task count when a stream or a page
    can carry it; sets of ids are sent in their shorter form (compact runnable sets).
  - Tests: `[Trait("Category", "Perf")]` for scale tests that take longer than about a second; they run
    with `manage test perf` (not in `manage test unit`), each with a generous but meaningful time
    budget, and write their measured times to the test output.
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
  one); the flat `ui:ToolbarButton` is only for toolbars.
- **HARD RULE, no style differences.** Same kind of element, same look, everywhere: one
  style per element type in `Themes/OadmTheme.axaml`, no local overrides of font size,
  weight, color, padding or alignment in views. No special cases such as a bold selected rail
  item or a greyed toggle unless the spec says so. When a mismatch is found, fix the shared
  style or control, not the single view.
- UI is verified only with Avalonia headless tests rendering offscreen (screenshots via
  `OADM_SCREENSHOT_DIR`). Never automate the real desktop: no simulated clicks or drags, no
  capturing real windows on a developer machine.
- Tests accompany every non-trivial class. VAPIX parsing tested from recorded fixtures; RTP
  depacketizers from recorded RTP (`tests/Oadm.Core.Tests/Fixtures/LiveView`, re-record with
  `RtpRecorderTests` and `OADM_RECORD_RTP_DIR`).
  Discovery and task engine tested with fakes, no network in unit tests.
- Commits: conventional commits (`feat:`, `fix:`, `chore:`), small and focused.
- This file is the single source of truth for the spec. When a decision changes, change it
  here in the same PR.

# Goal 1 - Definition of Done

1. `dotnet run --project src/Oadm.Server` starts on Windows, Linux, macOS and creates its
   SQLite database and master key.
2. `dotnet run --project src/Oadm.Client` connects and shows the empty Manage devices window.
3. Add devices via zero-conf (mDNS), IP range and manual address on the fast add page, with
   automatic login from the credential list, setting a password on a factory-default device and
   logging in to others inline.
4. Added devices appear in the grid with all columns the device provides; status refreshes on
   the configured interval.
5. Right-click or toolbar **Restart** runs the Restart task plugin loaded from the plugins
   folder, with progress visible in the Tasks tab.
6. Remove device, refresh, search and column chooser work.
7. Tests green on CI for all three OS.

Milestones in order: (1) solution skeleton + contracts + CI, (2) VAPIX client + fixtures,
(3) persistence + crypto, (4) discovery, (5) gRPC services + task engine, (6) client shell and
device grid, (7) add page, (8) plugin loader + Restart plugin, (9) polish and docs.

# Open Points to Verify on Real Hardware

Resolved on AXIS P3265-V, AXIS OS 12.11.77 (see VAPIX section): HTTPS parameter, first
password over HTTP, mDNS TXT keys. Still open:
- Success response of `pwdgrp.cgi` on a factory-default device (needs a reset camera).
- `restart.cgi` end to end through the Restart plugin.

# Later Goals (not now)

Client authentication and users, SSDP/WS-Discovery, scheduling/retry, Core plugins (NTP,
DHCP, IDP) with their UI pages, backup/restore, warranty
and replacement data from Axis online services, installers/packaging, localization.

# Resources

- ADM user manual: https://help.axis.com/en-us/axis-device-manager
- VAPIX library: https://developer.axis.com/vapix/
- Reference screenshot: docs/adm-manage-devices.png
