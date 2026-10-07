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
`scripts/publish-*`. Plugins ship next to the exe in `plugins/<name>/`.
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
                       ToolbarButton, ToolbarSeparator)
  Oadm.Core/           domain model, VAPIX client, discovery, task engine, persistence (EF Core)
  Oadm.Server/         host: gRPC services, plugin loader, polling, Serilog setup
  Oadm.Client/         Avalonia app: views, view models, gRPC client, plugin loader
plugins/                (layout and SDK guide: plugins/README.md)
  Oadm.Plugins.Restart/   first Task plugin (server only)
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
  "entered"), `auth_detail = 17`, `passphrase_policy = 18`, `entered_address = 19`; range scans and
  address probes end after scan_finished once every login finished), `Stop`.
- `AddDevicesService`: `RetryAuth(session_id, discovered_id, user_name, password,
  save_to_credential_list)` (returns the updated DiscoveredDevice, also pushed to the stream;
  NOT_FOUND, FAILED_PRECONDITION factory default / already added, INVALID_ARGUMENT),
  `Commit(session_id, discovered_ids, initial_passwords map = 6, initial_root_password)` (reply:
  `device_ids`, `results = 3` with discovered_id, device_id, status), `Prepare` (legacy, unused).
  See "Add Devices Page".
- `TaskService`: `ListTaskPlugins` (context-menu entries incl. those contributed by Core
  plugins), `Run(pluginId, deviceIds, payloadJson)` (one task per device, reply `task_ids`;
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
  `Invoke(pluginId, method, payloadJson)` for Core plugin UI pages (later goal).
- `SettingsService`: `Get`, `Set`, `ListCredentials`, `AddCredential(user_name, password)`
  (INVALID_ARGUMENT, RESOURCE_EXHAUSTED over 20 entries; an identical pair returns the existing
  entry), `RemoveCredential(id)` (NOT_FOUND). Credential entries carry id, user name and created
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
Must work on all three OS and on multiple NICs (bind one socket per interface). Runs
continuously while the add page is open in scan mode and once at server start. SSDP and WS-Discovery
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
- **Scan**: zero-conf (mDNS) discovery starts immediately; devices appear live while it is open.
- **Scan IP range**: From / To inputs on top; Enter or the Scan button starts (several ranges add to
  the same list).
- **Add manually**: Address input (IP or host name, optional port and scheme,
  `https://camera.example.com:8443`); Enter or Find probes that address (`ProbeAddress`); every
  address adds a row; "No Axis device answered at X." when nothing answers. The entered address
  (host[:port]) becomes the device address on add, regardless of `Devices.UseHostName`.

List: checkbox, category icon, Address, MAC address, Model, Login (status), Action. Above it
"Select all authenticated", a summary ("10 found · 3 ready to add · 1 need a login · 2 need a
password · 3 selected"), search box and the scan progress row. Login status per device
(`ui:StatusChip`): Checking... (accent), Authenticated (user) (ok), Password not set (warning),
Login failed (error, reason as tooltip), Unreachable (error), Already added / Added (neutral / ok,
row greyed). Only addable devices can be checked: authenticated ones, and factory-default ones
once a first password is entered. **Add** (footer, "Add 3 devices") commits the checked devices
in one click and closes; "Keep open after adding" keeps the page open and marks them Added.

- **Login failed**: a click on the row or the "Log in" link opens the inline editor below the list
  (user name, password, "Save to credential list" checked by default, Retry, Cancel). Retry calls
  `RetryAuth` right away and updates the row; success checks the row and closes the editor, a wrong
  password shows "The user name or password is wrong.".
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
- Status shown as small rounded chips, not plain text: transparent background, 1 px border in
  the status color (OK green, warning amber, error red, running violet, neutral grey), normal
  text color, regular weight (not bold), no dot. No status dot on the device icon either.
  Count badges stay a small solid violet pill.
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
   client, horizontal scroll, multi-select, right-click context menu with core actions and
   all Task plugins whose `CanRun` is true for the whole selection.
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
  12 of 80 MB"). Overall progress = steps with equal weights, the running one with its own progress,
  unless the plugin calls `ReportProgress` (then that value wins). Step changes go to the change feed
  immediately and are persisted with the throttled writes (max 1/s per device) plus every state
  transition. Recovery after a server restart marks a Running step Failed ("Server stopped while the
  task was running.") and Pending ones Skipped.
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
}

public interface ITaskPlugin : IPlugin
{
    bool ShowInToolbar { get; }
    bool RequiresDialog { get; }       // client opens the matching ITaskPluginDialog first
    bool CanRun(IDeviceInfo device);
    int? MaxParallelDevices => null;   // concurrent tasks of this plugin; null = 8
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
}

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
the shared controls from `Oadm.Sdk.Client.Controls` (`IconLabel`, `SearchBox`, `OadmIcon`,
`DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip`, `FileRow`, `ProgressRow`,
`ToolbarButton`, `ToolbarSeparator`; usage in `plugins/README.md`) and
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
   `test-hardware`), and never run against a device without the developer explicitly asking.

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
- Users: Check compatibility, Read password policy, Identify OADM account, Read users, Validate change,
  Add/Update/Remove user <name> (one per write), Verify users.
- Network: Check compatibility, Read current settings, Read IPv6 address mode, Validate settings, Set
  host name, Set DNS, Set IPv6 (+ Enable IPv6), Set IPv4 (order as written), Wait for the settings to
  apply, Check reachability; unchanged sections Skipped "Keep unchanged".
- Firmware: Check compatibility, Read device info, Validate file, Read firmware status, Upload firmware
  (byte progress), Install firmware (until offline), Wait for device to come back, Verify version, Read
  commit state, Commit firmware (retries add "Wait before retrying the commit" and "(attempt n)" steps).
- ACAP install: Check compatibility, Read package, Read device info, Read embedded development version,
  Read unsigned application setting, Read installed applications, Check compatibility of package, Upload
  package (byte progress), Verify installation (+ Start application, Verify application state); remove,
  start, stop: Check compatibility, Read installed applications, <action> application, Verify ....

## Network settings plugin

`plugins/Oadm.Plugins.Network` (+ `.Client`), id `oadm.network`, context menu "Network settings...",
dialog with IPv4 / IPv6 / DNS / Host name sections (each "Keep unchanged" by default), address range
assignment with preview for several devices, acknowledged warning before any change that can cut OADM
off. Uses network-settings 1.x (`getNetworkInfo`, `setIPv4AddressConfiguration`,
`setResolverConfiguration`, `setHostnameConfiguration`, `setIPv6AddressConfiguration` >= 1.6) and
param.cgi `Network.*` for the IPv6 address mode and for devices without network-settings. Writes the
address family OADM connects with last, then reports re-addressing with `ReportWarning`; it does not
update the OADM device record. Decision table and verified device behavior: plugin `README.md`.

## Applications (ACAP) plugin

`plugins/Oadm.Plugins.Acap` (+ `.Client`), id `oadm.acap`, context menu "Applications (ACAP)...",
dialog. Lists installed applications (query `listApplications`), start/stop/remove (remove asks for
confirmation) and install/upgrade of an uploaded `.eap` on all selected devices. Uses the classic
Application API (`application 1.x`: list/upload/control/config.cgi). Before uploading it reads the
package manifest and refuses (nothing changed) on architecture mismatch, AXIS OS outside the
package's range or below its manifest schema minimum, root apps on AXIS OS 12+, and downgrades
without the explicit option. Decision table and research in `plugins/Oadm.Plugins.Acap/README.md`.

# Settings

Server-side in `Setting`. Goal 1 keys: `Polling.IntervalSeconds` (60, 5..86400),
`Polling.FullRefreshMinutes` (10, 1..1440), `Scan.Parallelism` (32), `Scan.TimeoutMs` (1500), `Server.Name` (hostname),
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
  `FileRow` (chosen file + "Choose file..."), `ProgressRow` (progress bar + status text). Before
  writing new XAML, check both `Controls/` folders and reuse; if a second place needs something
  that exists only inline, extract it into a control first.
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
