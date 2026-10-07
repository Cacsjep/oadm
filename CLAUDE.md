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
| Live video | RTSP (server, own client) relayed as encoded H.265/H.264 over gRPC, decoded in the client with FFmpeg (LGPL, bundled) |

Cross-platform is a day-one requirement: Windows, Linux, macOS. No Windows-only APIs
(no DPAPI, no registry, no WMI). Paths via `Path.Combine`, data folder via
`Environment.SpecialFolder.LocalApplicationData/Oadm`. Every PR must build on all three
(CI matrix) and the server must run as a plain console process on all three.

# Packaging

Server and client publish as one self-contained single-file exe per platform
(`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `PublishReadyToRun`), see
`manage publish`. Plugins ship next to the exe in `plugins/<name>/`.

Developer commands: one entry point per shell at the repo root, `./manage.sh` (bash) and
`.\manage.ps1` (PowerShell 5.1/7), verb + target (`build`, `run`, `test`, `publish`, `clean`,
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
window. Licenses and source offer: `THIRD-PARTY-NOTICES.md`. Never add GPL or nonfree FFmpeg
builds.

# Solution Layout

```
Oadm.sln
src/
  Oadm.Contracts/      protobuf files + generated gRPC stubs, shared enums
  Oadm.Sdk/            plugin SDK: interfaces, attributes, context objects. No Avalonia, no EF
  Oadm.Sdk.Client/     client-side plugin SDK: dialog/page/toolbar interfaces, ITaskDialogContext,
                       IToolbarContext, shared controls (Controls/: IconLabel, SearchBox, OadmIcon,
                       DialogTitleBar, CardHeader, DialogFooter, StatusChip, FileRow, ProgressRow,
                       ToolbarButton, ToolbarSeparator, PasswordBox, MessageWindow)
  Oadm.Core/           domain model, VAPIX client, discovery, task engine, persistence (EF Core)
  Oadm.Server/         host: gRPC services, plugin loader, polling, Serilog setup
  Oadm.Client/         Avalonia app: views, view models, gRPC client, plugin loader
plugins/                (layout and SDK guide: plugins/README.md)
  Oadm.Plugins.Restart/   first Task plugin (server only)
  Oadm.Plugins.SnapshotReport(.Client)/   first Core plugin: rail page + PDF maintenance report
  Oadm.Plugins.VapixCommander(.Client)/   core plugin: VAPIX command library, raw requests, rollouts
  Oadm.Plugins.NtpServer(.Client)/        core plugin: NTP server (RFC 5905 server mode) + "Use OADM as NTP server"
  Oadm.Plugins.<Name>/          server part: Oadm.Plugins.<Name>.Server.dll + plugin.json
  Oadm.Plugins.<Name>.Client/   optional Avalonia part: Oadm.Plugins.<Name>.Client.dll
                                (both copy their output to artifacts/plugins/<plugin id>/)
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
- `DiscoveryService`: `StartZeroConf`, `StartRangeScan(from, to)`, `ProbeAddress(address)` (one
  entered IP or host name, optional port/scheme; INVALID_ARGUMENT for an unusable or unresolvable
  address), `WatchDiscovered` (stream; every device with its automatic login result
  `auth_state = 14` (PENDING, AUTHENTICATED, PASSWORD_NOT_SET, LOGIN_FAILED, UNREACHABLE,
  ALREADY_ADDED), `auth_user_name = 15`, `credential_id = 16` ("list:<id>", "device:<id>",
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
- `TaskService`: `ListTaskPlugins` (context-menu entries incl. those contributed by Core
  plugins; `TaskPluginInfo.display_name` normalized by the server, `group = 8` never empty), `Run(pluginId, deviceIds, payloadJson)` (one task per device, reply `task_ids`;
  `task_id` is deprecated = first id), `List`, `Watch` (stream), `Cancel`, `Delete`,
  `DeleteAll`, `GetLog(taskId)` (per-task log, oldest first, live while running); `TaskInfo` carries
  `repeated TaskStep steps = 13` (index, name, `TaskStepState`, detail, progress, started, finished) and
  `current_step_index = 14` (-1 none),
  `Query(pluginId, deviceId, method, payloadJson)` (read-only `ITaskPluginQuery` for task
  dialogs, 30 s timeout; NOT_FOUND, UNIMPLEMENTED, FAILED_PRECONDITION for an incompatible device,
  changed certificate or rejected credentials, UNAVAILABLE, DEADLINE_EXCEEDED, INVALID_ARGUMENT;
  the status detail is the user message).
- `FileService`: `Upload` (client stream: header {name, size} then 256 KB data chunks; returns
  id, name, size, SHA-256; INVALID_ARGUMENT, RESOURCE_EXHAUSTED over `Uploads.MaxMegabytes`),
  `Delete(fileId)`. Uploads live in `<datafolder>/uploads/<id>.bin` + `<id>.json`, are deleted
  after `Uploads.RetentionHours` (checked every 15 min) and reach tasks through `IUploadedFiles`.
- `PluginService`: `ListCorePlugins` (navigation pages), per-plugin generic
  `Invoke(pluginId, method, payloadJson)` for Core plugin UI pages (NOT_FOUND unknown plugin or
  object, FAILED_PRECONDITION not running, INVALID_ARGUMENT for an `ArgumentException` of the
  plugin, INTERNAL otherwise; the status detail is the message), `Watch(plugin_id)` (stream of `PluginEvent`
  {plugin_id, topic, payload_json} the plugin publishes through `ICorePluginContext.Events` from the call on; NOT_FOUND
  unknown plugin; `Oadm.Core.Plugins.PluginEventHub` fans out with 256 events buffered per watcher, oldest dropped).
  Users: "Snapshot report", "VAPIX Commander", "NTP server".
- `SettingsService`: `Get`, `Set` (`ServerSettings.zero_conf_seconds = 8`, 0 keeps the stored value),
  `ListCredentials`, `AddCredential(user_name, password)`
  (INVALID_ARGUMENT, RESOURCE_EXHAUSTED over 20 entries; an identical pair returns the existing
  entry; a new entry is tried on the failed devices of every open add session), `RemoveCredential(id)` (NOT_FOUND). Credential entries carry id, user name and created
  time, never a password.
- `LiveViewService`: `Watch(device_id, max_width, max_height, fps, accepted_codecs, camera)`
  (stream of encoded access units), `ListSources(device_id)` (view areas / sensors / channels).
  See "Live view".

# Data Model (EF Core, SQLite)

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
  The technician's credential list (Settings page), at most 20 entries, tried in the order added.
- `Task`: Id, BatchId (shared by the tasks of one Run; proto `batch_id`), PluginId, Name, Status
  (Queued, Running, Done, Failed, Cancelled, DoneWithWarnings), Owner (client machine/user name),
  CreatedUtc, StartedUtc, FinishedUtc, Progress (0-100), PayloadJson (column kept but always NULL:
  payloads may carry secrets and are never persisted; the migration clears old values),
  ScheduledUtc (nullable, unused in Goal 1).
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

Certificate trust enum: `Trusted` (chain builds to a root in the server OS trust store),
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
  HTTP and Basic or Digest over HTTPS. Basic is never sent over plain HTTP.
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
  says "Scan finished, N devices found" or "Scan stopped, N devices found" ("1 device found").
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
Login failed (error, reason as tooltip), Unreachable (error), Already added / Added (neutral / ok,
row greyed). Only addable devices can be checked: authenticated ones, and factory-default ones
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
lockout risk): first the credential list in the order added, then the distinct credentials stored
for managed devices (most used first). One try = `basicdeviceinfo getAllProperties` with the
credential (plus `param.cgi` network parameters when the device allows anonymous access); 401 = next,
transport failure = UNREACHABLE, another serial at the address = UNREACHABLE. At most 8 devices log
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
returns (the devices show PENDING); (b) a credential added to the credential list (Settings page or
"Save to credential list") reloads the candidates of every session. The follow-up uses the normal login
loop: one login at a time per device, at most 8 devices at once, at most 10 rejected credentials per
device in total (typed credentials of `RetryAuth` are remembered as rejected but not counted), entered
credentials first, then list, then managed-device credentials; credentials added while it runs are
tried in the same run. Unreachable attempts count neither as attempt nor as rejected.

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
  Font Inter (Avalonia.Fonts.Inter), 11.7 px body, 9.9 px small, 18 px card titles, 21.6 px page titles, semibold titles.
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
   Columns: Name, Device, Status, Current step, Start time, Owner, Progress (bar). **Current step** is
   "Step 3/6 · Upload firmware" plus " · 45 %" while the running step reports progress (tooltip: the text
   and the step detail); a failed task shows its failed step; empty for plugins without steps. **Device** is the task's
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
full refreshes go through one deduplicating queue with bounded parallelism.

# Tasks

- **A task always targets exactly one device.** Running a plugin on N selected devices creates
  N tasks (one Run, one shared BatchId, the payload shared in memory), so one failing device
  never marks the others as failed. The task state is the device state.
- States: Queued, Running, Done, Failed, Cancelled, DoneWithWarnings (the plugin called
  `ReportWarning`; a failure wins over a warning). Persisted, visible in the tasks pane, history
  kept until the user deletes it.
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
  tasks pane reads "Step 8/8 · Completed"; Failed and Cancelled tasks get none (the failed step stays
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
- Task engine: at most 8 running tasks per plugin (`TaskEngineOptions.MaxParallelTasksPerPlugin`,
  overridden by `ITaskPlugin.MaxParallelDevices`, e.g. firmware 2); further tasks wait in Queued
  and a cancel while queued ends them as Cancelled ("Cancelled before start."). Cancellation via
  `CancellationToken`, exceptions become `Failed` with message (also logged as an Error entry),
  never crash the server.
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
    int? MaxParallelDevices => null;   // concurrent tasks of this plugin; null = 8
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
}
// DIM defaults throw NotSupportedException. UpdateDeviceAddressAsync throws DeviceIdentityException (Oadm.Sdk.Plugins)
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
}
```

Task plugin names and groups: `DisplayName` is at most `TaskPluginNames.MaxDisplayNameLength` (32)
characters and never ends with "..." (dialog tasks too); it is the menu and toolbar name only, the tasks
pane shows `GetTaskName` (see Tasks). The server registry (`RegisteredTaskPlugin.DisplayName`
/ `.Group`) strips trailing "..." / "…" and shortens longer names to 31 characters + "…", logging a
warning for either; empty groups become `General`, groups are shortened the same way. The task name in
the tasks pane, `TaskPluginInfo` and the client menu and toolbar use the normalized name. Bundled plugins:
Restart, Upgrade firmware and Date and time (Maintenance), Applications (ACAP) (Applications), Users (Users), Network
settings and Assign IP address (Network); `TaskPluginNamesTests` checks every plugin deployed to
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
`DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip`, `FileRow`, `ProgressRow`,
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
  Set NTP configuration / Turn off NTP, Set date and time, Verify time settings, Verify NTP settings (only the changed
  sections are planned; values the device already has are Skipped "Already ...").
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
- Page: toolbar Refresh all, "Select all" check box, **Export PDF...** (primary); picture size slider
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
JSON schema: `docs/vapix-commander/command-format.md` + `command.schema.json` (the contract; category
`Custom` for saved commands, optional `response.errorPattern`).

- Library: every `Library/*.json` next to the plugin assembly (`ICorePluginContext.PluginDirectory`,
  fallback `AppContext.BaseDirectory/Library`), loaded once at start with strict JSON (unknown
  properties are errors) and `CommandValidator`; broken files and invalid or duplicate commands are
  skipped, logged and listed on the page ("N library entries could not be loaded"). The bundled library
  (163 commands, separate commit) is checked by `tests/Oadm.Plugins.VapixCommander.Library.Tests` and
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
  header tables with Make field, body type + body, timeout, response kind, fields from `{{placeholders}}`
  typed by hand, "Add to rollout", "Save as command..." with name, category, description, requires
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
  order: `Too many requests, dropping`, `Upstream <host> not reachable, serving the server clock`, `Server clock differs
  from upstream by 3.2 s` (> 1 s). Mapping in `NtpStatusTexts.ForBindError` (SocketError x OS).
- Request log: last 40 in memory (`RequestLog` ring buffer of value entries, no table): time, client, offset (client
  transmit minus server receive; "-" beyond one day, e.g. clients that randomize the transmit timestamp), result Answered
  / Rate limited. Pushed live (see `ICorePluginContext.Events`, batched every 500 ms); the page resolves the device column
  ("P3265-V (10.0.0.48)") from the client's device list (O(n) per device change).
- Page methods (`NtpServerMethods`): `getState` -> `NtpState` (config, status, interfaces, requests, upstream, stratum,
  port); `save` ({enabled, interfaceId, upstream}) validates the upstream with one query (same timeouts; the running
  server keeps serving) and replies {saved, upstreamError, upstreamResult, state}; an upstream that does not answer is
  not saved (error under the field). Events: `state` (status/upstream changed), `requests` (new entries).
- Page (one card in the host card): `ui:CardHeader` "NTP server" with the status chip on the right; Enable NTP server;
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

## Date and time plugin

`plugins/Oadm.Plugins.DateTime` (+ `.Client`), id `oadm.datetime`, context menu (group Maintenance, icon `clock`)
**Date and time**: a clone of the ADM / AXIS Camera Station "Set date and time" dialog (the ADM manual has no date and
time chapter; wording from the ACS 5 manual, sources in the plugin `README.md`), for any number of devices.
- Dialog "Set date and time": **Device time** card for the first selected device (read-only query `getTimeSettings`:
  device time and offset, time zone, time mode with sync state, server time and difference); **Time zone** card: the 313
  IANA zones of AXIS OS 12.11 (bundled list, offsets from the OS time zone database) in a DataGrid (UTC offset, City,
  Time zone, DST) with `ui:SearchBox`, "Automatically adjust for daylight saving time changes", Keep unchanged;
  **Time mode** card: Keep unchanged, Synchronize with server computer time (NTP off, the OADM server's UTC sent once per
  device at execution time, the devices get the server's time zone), Synchronize with NTP server (Obtain from DHCP / Use
  servers, up to 5, one per line; Use NTS with NTS KE servers on ntp 1.5+), Set manually (date + time in the device's
  zone, NTP off). OK. Field errors via INotifyDataErrorInfo under the inputs; device notes are O(n) summaries of the
  cached API lists ("500 of the selected devices have no Time API ...").
- APIs: time-service 1.x (`getDateTimeInfo`, `setTimeZone`, `setPosixTimeZone` for DST off, `setDateTime`), ntp 1.x
  (`getNTPInfo`, `setNTPClientConfiguration`; NTS from 1.5), param.cgi `Time.*` for older firmware (time zone as POSIX,
  one NTP server; no date and time without the Time API; date.cgi is not used). Decision table and what 10.0.0.48
  reports: plugin `README.md`. Only changed sections are written; values the device already has are skipped; every
  write is verified by reading again (Warning on a mismatch or a clock more than 3 s off).
- Task names (`GetTaskName(payloadJson)` on the plugin class): "Set time zone Europe/Vienna", "Set NTP servers 10.0.0.17,
  pool.ntp.org", "Set NTP servers from DHCP", "Sync with server time", "Set date and time 2026-10-07 18:00", "Change
  date and time" (zone and time mode).
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
1..8760; both server-only, not on the settings page yet), `Devices.UseHostName` (bool, false: add devices by host name when one is
known, otherwise by IP address; proto `optional bool use_host_name = 7` so a partial `Set`
keeps it). Settings page in the client exposes them; `Devices.UseHostName` is the checkbox
"Use host name when available, otherwise IP address". Settings page card **Credential list**:
entries (key icon, user name, added time, Remove), add form (user name, password, "Add
credential"); stored encrypted on the server (`CredentialListStore`, table CredentialListEntries),
never shown again, tried on every discovered device (see "Add Devices Page"). Client-side (local JSON in
LocalApplicationData): server address, grid column layout, bottom pane state.

# Security

- Device passwords: AES-256-GCM, key in `<datafolder>/master.key` (0600 on Unix), random
  nonce per record, stored as `nonce|ciphertext|tag`. OS keyring integration is a later goal.
- Credentials never leave the server; gRPC returns only "has credentials". The credential list
  (same AES-256-GCM, entry id as associated data) returns only ids and user names; credentials the
  technician types for a login (RetryAuth) or a first password travel only client -> server.
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
- **HARD RULE, reuse UI components.** Anything that appears in more than one place is one
  shared control, never a copy: in `src/Oadm.Sdk.Client/Controls` (namespace
  `Oadm.Sdk.Client.Controls`, XAML prefix `ui`) when plugin dialogs need it too, else in
  `src/Oadm.Client/Controls`. Existing shared ones: `IconLabel` (every icon + text row: navigation
  rail, dialogs), `ToolbarButton` (every toolbar button) and `ToolbarSeparator`, `SearchBox`
  (every search field), `OadmIcon`, and for
  dialogs `DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip` (status chip),
  `FileRow` (chosen file + "Choose file..."), `ProgressRow` (progress bar + status text),
  `MessageWindow` (every message box and confirmation popup, host and plugins; dialogs never show
  inline "I understand" risk sections), `PasswordBox` (every password field: TextBox with bullet mask and eye button "Show password" /
  "Hide password", icons `Icon.eye` / `Icon.eyeOff`; never a `TextBox` with `PasswordChar`), `CodeView`
  (every read-only code / response display: JSON and XML pretty-printed with 2 spaces and highlighted,
  param.cgi `key=value` highlighted with `# Error` lines red, `Language` Auto/Json/Xml/KeyValue/Plain with
  Auto from the content type then the text, `IsFormatted` false = exact text; unparsable text plain, above
  512 K characters plain; monospace `Oadm.FontFamilyMono`, no wrapping, selectable and copyable; colors
  `Oadm.Code.*Brush`; logic in `CodeText` without UI; editable bodies stay `TextBox.code`). Before
  writing new XAML, check both `Controls/` folders and reuse; if a second place needs something
  that exists only inline, extract it into a control first.
- Plugin projects copy their output to `artifacts/plugins/<id>/` after every build. A running
  client or server keeps those files open; build or test with `-p:OadmSkipPluginDeploy=true` (e.g.
  together with `--artifacts-path`) to skip the copy while the apps run.
- **HARD RULE, scale to thousands of devices.** A site can have 1000+ cameras. Every list of
  devices, discovered devices, tasks, sources or tiles is virtualized (DataGrid, virtualizing
  ListBox/ItemsRepeater; never an ItemsControl/StackPanel creating one control per item), search,
  filter, sort and select-all are O(n) without per-item UI work, server calls are batched (one call
  for many devices, bounded parallelism on the server), per-device work is lazy for visible rows
  or summarized ("4,812 compatible, 188 missing API"), and images/snapshots load only for visible
  tiles. Every feature with a device list has a test with at least 5000 fake devices that keeps
  filtering/selection/summary fast.
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
DHCP, IDP) with their UI pages, backup/restore, certificates, warranty
and replacement data from Axis online services, installers/packaging, localization.

# Resources

- ADM user manual: https://help.axis.com/en-us/axis-device-manager
- VAPIX library: https://developer.axis.com/vapix/
- Reference screenshot: docs/adm-manage-devices.png
