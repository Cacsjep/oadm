# Bundled plugins

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Every task plugin describes its steps, task names, dialog and device API in its own `README.md` (`plugins/<name>/`).
Common to all: every device request and every wait is a named step; every successful task ends with the engine's step
**Completed**; names follow "Tasks" (no secrets, at most 48 characters).

- **Restart** (`oadm.restart`, always on, toolbar and context menu, no dialog): `restart.cgi`, then waits until the
  device went offline and answers again (3 min); a different serial afterwards is a Warning. Task name "Restart device".
- **Users**, **Upgrade firmware**, **Date and time**, **Applications (ACAP)**, **Network settings**, **Assign IP
  address**, **Export / Import configuration**: in their READMEs. A new static address (IPv4, IPv6) is probed
  before the first write (`IAddressProbe`: ping, 2 tries x 1 s, plus TCP 80 / 443; the device's own addresses are not
  probed); an answer fails "<address> is already in use (answers ping). Nothing was changed.".

## Network settings and Assign IP address

`plugins/Oadm.Plugins.Network` (+ `.Client`), always on; details, the VAPIX decision table and the verified device
behavior: plugin `README.md`.

- **Network settings** (`oadm.network`, context menu Network): IPv4 / IPv6 / DNS / Host name sections, each "Keep
  unchanged" by default; several devices get their new addresses per row in the shared address table
  (`AddressAssignmentGrid`). **Assign IP address** (`oadm.network.assign-ip`, context menu and toolbar): a clone of ADM's
  "Assign IP address to selected devices": DHCP, or an IP range in ADM / ACS syntax (`IpRangeExpression`) with suggested
  addresses that skip the router, managed devices and addresses in use (query `checkAddresses`).
- Rules that must hold: a risky change (address, subnet, router, DHCP switch, IPv6 over IPv6) asks in `ui:MessageWindow`
  ("The devices may become unreachable"); conflicts block Apply / Finish; a new static address is probed before the first
  write; the address family OADM connects with is written last; after a new static address OADM follows the device (90 s,
  serial verified, `UpdateDeviceAddressAsync`; otherwise a Warning and the record stays); with DHCP the periodic re-find
  moves the record.

## Snapshot report (core plugin)

`plugins/Oadm.Plugins.SnapshotReport` (+ `.Client`), id `oadm.snapshot-report`, rail page **Snapshot report** (group
Reporting, icon `snapshot`), on by default, switchable. The yearly maintenance check: current snapshots of every video
source of the managed video devices, checked fast and exported as a PDF report (PDFsharp + MigraDoc, MIT). Details:
plugin `README.md`.

- Devices: the purple "Selected cameras" button (`PageDeviceChoice`: All devices, tags, chosen cameras; the Devices page selection to start with, else the last choice on this computer); the page reads only the cameras of the choice and nothing before one.
- Rules that must hold: read-only for devices (param.cgi reads and image.cgi snapshots with the stored credentials,
  server side); opening the page contacts no device (**Create snapshots** starts the first load); at most 4 device
  requests at a time, 3 s each; devices with Credentials required, Password not set or Certificate changed are one error
  tile without a request; error texts from the shared `DeviceMessages`; report jobs live in server memory (30 minutes
  after last use), the PDF is read in 2 MB chunks.
- Tests: `tests/Oadm.Plugins.SnapshotReport.Tests` (PDF parsed back with PDFsharp); hardware test through the in-process
  server writing the PDF to `OADM_SCREENSHOT_DIR`.

## VAPIX Commander (core plugin)

`plugins/Oadm.Plugins.VapixCommander` (+ `.Client`), id `oadm.vapix-commander`, rail page **VAPIX Commander** (group
Automation, icon `command`), on by default, switchable. Pick VAPIX commands from a library (176 bundled, `Library/*.json`)
or saved ones, fill their fields and roll them out to many devices, or send one to one device. Details: plugin
`README.md`; command format: `Schema/command-format.md` + `command.schema.json` (the contract).

- File fields (Siren and light **Upload siren and light profiles**): the file (at most 1 MB) travels inline with the
  command as {"name", "data"} and only as a part of a `multipart` body, never in a URL, header or log.
- Rules that must hold: every value is checked before anything is sent; requests stay on the device (paths through
  `CommandValidator.IsDevicePath`); payloads stay in memory, passwords are never logged (`***` in request lines and
  bodies), saved passwords are encrypted with the master key and never listed or exported; a rollout checks every
  command first (nothing starts on an error), writes need a confirmation, dangerous commands a second one, and each
  device re-checks a fresh API list before a write ("Missing API x. Nothing was sent."). The response kind decides how
  success, errors and values are read (Axis devices answer errors with HTTP 200).
- Page: Library (search, tree Built-in / Saved; a click adds a command to the rollout set; **+** opens the request
  builder, the pen edits a saved command), Rollout set (grid with per-row icons, field form, Send with the result card)
  and Target devices (the shared device picker with All devices and tags, the Devices page selection to start with, compatibility from the
  cached API lists with only the problem devices listed, Stop on first error, Run on N devices); the tasks pane
  below the page. The request builder is its own dialog: start from scratch or from an existing command, send it to
  try, save (a saved command is replaced in place).
- Saved commands: plugin setting `savedCommands`, at most 500, shared by all clients, export / import as library files.
- Methods are Operator; `tryRequest` and `rollout` are audited. Scale: 30,000 target devices, every list action under
  200 ms (test).
- Tests: `tests/Oadm.Plugins.VapixCommander.Tests`, `.Library.Tests`; read-only hardware test `VapixCommanderHardwareTests`.

## NTP server (core plugin)

`plugins/Oadm.Plugins.NtpServer` (+ `.Client`), id `oadm.ntp-server`, rail page **NTP server** (group Servers, icon
`metronome`), on by default, switchable. Own RFC 5905 server-mode implementation (SNTPv4 compatible) on UDP
123, no library; contributes the task **Use OADM as NTP server**. Details: plugin `README.md`.

- Rules that must hold: requests are always answered at once from the current time source state, never waiting for the
  upstream; without an upstream OADM serves its own clock as stratum 10 (never "unsynchronized"); the OS clock is served,
  never set; an upstream is validated with one query before it is saved; abuse protection per client IP (burst 8, 1 per
  2 s, one Kiss-o'-Death RATE a minute) and 2,000 requests/s overall; a failed bind is retried every 30 s; status texts
  in plain words (`NtpStatusTexts`: port in use, permission, interface missing, upstream not answering).
- Roles: `save` is Admin and audited. The Windows service opens UDP 123 in the firewall while enabled.
- Tests: `tests/Oadm.Plugins.NtpServer.Tests` (loopback with random ports, never port 123 on a real interface).

## DHCP server (core plugin)

`plugins/Oadm.Plugins.DhcpServer` (+ `.Client`), id `oadm.dhcp-server`, rail page **DHCP server** (group Servers, icon
`network`), on by default, switchable, starts disabled. Own RFC 2131 / 2132 implementation, IPv4 only, one interface, no
relay agents; static leases, the lease list, and optionally adding Axis devices that get an address. Details, per-OS
setup and the manual test plan: plugin `README.md`.

- Rules that must hold: before a new address is offered it is probed (ping + TCP 80 / 443; static leases are not
  probed); another DHCP server on the network is checked on Save, every 10 minutes and at start, and enabling next to one
  needs an explicit "Enable anyway"; abuse protection per MAC and overall, pending offers capped at half the pool; mask,
  router, DNS and domain come from the interface, never asked; leases survive restarts; the automatic add uses the add
  page's pipeline on the server (Axis MAC blocks only, the Axis check before any credential, the credential list only)
  and follows managed devices to their new address also when it is off; tests never send DHCP on a real interface.
- Roles: `save`, `saveStatic`, `deleteStatic`, `makeStatic`, `release` are Admin and audited. The Windows service opens
  UDP 67 in the firewall while enabled.
- Tests: `tests/Oadm.Plugins.DhcpServer.Tests` (in-memory network and loopback with random ports).

## PKI (core plugin)

`plugins/Oadm.Plugins.Pki` (+ `.Client`), id `oadm.pki`, rail page **PKI** (group Security, icon `shield`), always on.
One certificate authority for device certificates (generated on first start or imported), the server's trust in it, and
the Security tasks of the context menu: Enable / Disable HTTPS, Enable / Disable IEEE 802.1X, Renew certificates, View,
Delete and Install certificates, Install CA certificates. Details: plugin `README.md`; device API research:
`tests/Oadm.Plugins.Pki.Tests/Fixtures/`.

- Rules that must hold: the CA key is stored only encrypted with the master key (without `Secrets` no CA is kept; a key
  that cannot be decrypted is the state "CA key cannot be read", nothing is overwritten); replacing the CA asks how many
  devices have certificates from the current one; the CA and its previous CAs are trust anchors, so OADM-issued device
  certificates rate **Trusted** without an OS change; keys of device certificates are created on the device, OADM signs
  with its own subject and SAN (never the CSR's); every task checks AXIS OS 11.11+ (cert v1 from a fresh
  `config/discover`) before the first write and fails "Nothing was changed" otherwise; Enable HTTPS pins the new
  certificate through `UpdateDeviceTlsAsync`; 802.1X checks the device clock first; only OADM's own certificates that
  nothing uses are removed; passwords of imports and backups are never stored or logged; nothing in tests touches a real
  trust store; View and Delete certificates read at most 5,000 devices per window (each device is read, 4 at a time; a
  larger selection gets "Choose at most 5,000 devices."), the other tasks take any number; while it runs it offers the
  OADM CA to other plugins (`IDeviceCertificateAuthority`, `docs/spec/plugins.md` "Plugin services"), which signs the
  same way.
- Roles: everything but `getState` is Admin and audited; "Install in trusted root store" on the server asks with the CA's
  SHA-256 fingerprint.
- Tests: `tests/Oadm.Plugins.Pki.Tests` (every task against a stateful fake camera); opt-in `HardwareWriteTests`
  (`OADM_PKI_HARDWARE_WRITE=1`, not written yet).

## Metadata Monitor (core plugin)

`plugins/Oadm.Plugins.MetadataMonitor` (+ `.Client`), id `oadm.metadata-monitor`, rail page **Metadata Monitor** (group
Monitoring, icon `activity`), **off by default**. A port of the AXIS Metadata Monitor tool: pick one camera, Start, and
watch its event stream live (I/O ports, virtual inputs, storage, temperature, tampering, ACAP events). Details: plugin
`README.md`.

- Camera: a button that names the chosen camera and opens the shared device picker for one device (search; the former drop-down could not hold 30,000 devices).
- Rules that must hold: read-only for devices; the server opens the RTSP event stream with the stored credentials
  (Digest; Basic on plain RTSP only with the server setting Devices.AllowBasicOverHttp; SDK `ICorePluginContext.EventStreams`), passwords never reach the client; events
  only (`media.amp?video=0&audio=0&event=on`), one camera at a time, nothing saved; refused statuses without a request;
  a stream ends with Stop, a page switch or 20 s without the page's keep-alive; the client keeps the newest 10,000
  messages; XML through `DeviceXml` (1 MB, no DTD). The RTSP port of a camera behind a port forward is set in the
  device list (**OADM > RTSP port**); Start never changes it (an older client's port is ignored).
- Tests: `tests/Oadm.Plugins.MetadataMonitor.Tests` (+ Perf `ScaleTests`, hardware `MetadataHardwareTests`); Core
  `MetadataDepacketizerTests`, `RtspMetadataSourceTests`, `DeviceEventStreamsTests`.

## Image Health Dashboard (core plugin)

`plugins/Oadm.Plugins.ImageHealth` (+ `.Client`), id `oadm.image-health`, rail page **Image Health Dashboard** (group
Monitoring, icon `eye`), **off by default**. One table with the AXIS Image Health Analytics status of every chosen camera that
has the app, with five count cards (Good, Detected, Pending, Detection off, Errors). Details: plugin `README.md`.

- Devices: the purple "Selected cameras" button (`PageDeviceChoice`: All devices, tags, chosen cameras; the Devices page selection to start with, else the last choice on this computer); the page reads only the cameras of the choice and nothing before one.
- Rules that must hold: read-only for devices (`GET /local/AXISImageHealthAnalytics/v1/status` with the stored
  credentials); only on request and for the chosen cameras (the page opens, Refresh, Auto refresh every 10 s while the page is shown, off by
  default), nothing in the background and no events (event rules use the app's own events through Device triggers); 24
  cameras at a time, 3 s each; a stopped app stays as **Not running**; a detection turned off is no problem (the camera
  counts as Good); refused statuses without a request. Methods `check` and `getState` are Operator. One table per device
  scope: a group user checks and sees only the group's cameras, chosen ids outside it are refused whole (NOT_FOUND).
- Tests: `tests/Oadm.Plugins.ImageHealth.Tests` (+ read-only hardware test `ImageHealthHardwareTests`). Not in fake mode.

## I/O Monitor (core plugin)

`plugins/Oadm.Plugins.IoMonitor` (+ `.Client`), id `oadm.io-monitor`, rail page **I/O Monitor** (group Monitoring, icon
`toggle`), **off by default**. A simple, fast live display of the inputs and outputs of one device, drawn as a faceplate;
no events, no actions, no switching. Details: plugin `README.md`.

- Device: a button that names the chosen device and opens the shared device picker for one device (search; the former drop-down could not hold 30,000 devices).
- Rules that must hold: read-only (`getPorts` of `io-port-management` 1.x every 200 ms while a page shows the device, 3 s
  per request, 1 s pause after a failure); one reading per device shared by every page; stopped after 20 s without the
  page's keep-alive; at most 20 devices at once; refused statuses without a request; the VAPIX client always from the
  factory, never kept or disposed.
- Tests: `tests/Oadm.Plugins.IoMonitor.Tests` (+ read-only hardware test `IoMonitorHardwareTests`).

## Device triggers (core plugin)

`plugins/Oadm.Plugins.DeviceTriggers` (+ `.Client`), id `oadm.device-triggers`, rail page **Device triggers** (group
Automation, icon `flame`), **off by default**. A trigger is an event the chosen devices offer (I/O ports, virtual inputs,
temperature, storage, Object Analytics scenarios, other apps), optionally narrowed per source item; every trigger is an
event of the event rules, plus "<Trigger>: connection lost". Decisions, device research and details: plugin `README.md`.

- Rules that must hold: the event list is read live from the devices for every create and edit, never cached; devices
  come from the shared device picker (All devices, tags, single devices), resolved after every change and every 30 s; all
  turned-on triggers together watch at most 10,000 devices (`MaxWatchedDevices`; one permanent connection each, Windows
  has 16,384 outgoing ports by default): a save or turn-on over it is refused ("Device triggers watch at most 10,000
  devices. Choose tags or fewer devices."), a tag or All devices that grows past it keeps the devices it watches and the
  row says "More than 10,000 devices: some are not watched"; devices the status poll found not answering are not
  contacted (no connection attempts in an outage; the device connects again once a poll sees it Ok); one
  permanent WebSocket per device (session token from `wssession.cgi`, the pinned certificate), at most 16 connecting at
  once, a new filter only where triggers changed; a refused filter is a problem of its own triggers only; turned-off and
  empty triggers report off at once (rules never wait for a report that cannot come); a problem keeps the last state and
  turns "connection lost" on after 30 s; refused statuses are not contacted. Administrators change triggers (audited),
  operators see them.
- Tests: `tests/Oadm.Plugins.DeviceTriggers.Tests` (fake camera speaking the WebSocket protocol); opt-in write test
  (virtual input 64, Object Analytics test alarm; `OADM_TRIGGER_URL` / `_USER` / `_PASSWORD`, `OADM_TRIGGER_OA_SCENARIO`).

## Action Rule Editor (core plugin)

`plugins/Oadm.Plugins.ActionRules` (+ `.Client`), id `oadm.action-rules`, rail page **Action Rule Editor** (group
Automation, icon `workflow`), **off by default**, switchable. The devices' own action rules (their web interface: Events >
Rules, Recipients, Schedules) of every device in one page, grouped by what they do (same trigger and action; or by name,
action, trigger), and changed, turned on or off, copied to other devices, deleted and undone for many at once, e.g. "Send
notification through TCP" to "... while the rule is active" on 500 cameras; recipients (a change can take the rules that
send to them along; new) and schedules (new, edit, copy, delete) the same way; an edit can also change the event, the
schedule and the recipient (each device's own one of a name); **New rule** builds one rule for many devices
(an event they offered, conditions, a schedule, an action with its settings). Every settings form uses the bundled action
catalog (`Catalog/action-catalog.json`: labels, kinds, defaults from the camera's web interface). Decisions, research
and device API: plugin `README.md`.

- Devices: the purple "Selected devices" button (`PageDeviceChoice`: All devices, tags, chosen devices; the Devices page selection to start with, else the last choice on this computer); the page reads only the devices of the choice and nothing before one.
- Rules that must hold: read only on request, over SOAP (passwords in plain text stay on the server, never reach the
  client or a log, a login inside an address shows as `user:********@` and keeps each device's own password); an edit
  changes only the checked parts, everything else stays as each device has it; a rule is written
  only while it is exactly as the page read it (a version keyed with a server-only secret, so no password can be guessed
  against it), one task per device at a time; in place over REST `action-rules` v2 where the device lists
  it (AXIS OS 12; the action only when it changes, never a `**Redacted**` password), else recreated over SOAP (new rule
  first, then the old one); every write is saved for undo (before and after, 30 days, passwords encrypted with the
  master key, also values with a password inside an address) and undo puts back only what is still as the run left it,
  once no task of the run is queued or running; a hidden password inside an address is kept only for the same user and
  server; OADM never creates, changes a rule to or copies "Erase configuration";
  a new rule is checked whole on the server before any task starts and created by the same task as a copy; refused
  statuses are not contacted; a copied rule brings the schedules it uses first (OADM's own id
  `com.axis.schedules.oadm.<name>`, the same on every device); topics are written without the `//.` AXIS OS 10 appends;
  built-in schedules are never changed or deleted, a used schedule is deleted only together with its rules.
- Device groups: one table per device scope; every device id of a request in the caller's devices or the whole request
  is refused (NOT_FOUND) before anything is read, saved or started; undo runs per group.
- Roles: "show" (Operator, Viewer), "Change action rules" (`manage`, Operator yes), "Delete action rules" (`delete`,
  Operator yes); every change, copy, delete and undo is audited.
- Tests: `tests/Oadm.Plugins.ActionRules.Tests` (+ Perf 5,000 devices); opt-in `RulesHardwareWriteTests` and
  `ServerHardwareWriteTests` (`OADM_ACTION_RULES_WRITE=1`, every dev camera, own rules only, also a built rule and every
  action with the catalog's defaults; passed on AXIS OS 10.12, 11.9 and 12.11).

## System report (core plugin without a page)

`plugins/Oadm.Plugins.SystemReport` (+ `.Client`), id `oadm.system-report`, always on, a toolbar button **System report**
like ADM's "Get system report": the server downloads the server report of every selected device and the client saves
one ZIP for Axis support. Details: plugin `README.md`.

- Rules that must hold: read-only for devices (`serverreport.cgi`, `zip_with_image` for video devices, else `zip`); the
  answer is streamed to disk in the plugin's data folder (`VapixRequestOptions.StreamResponse`), must start with a ZIP
  signature, at most 256 MB and 5 minutes per device, at most 4 devices at a time for all jobs; refused statuses without
  a request; jobs live 30 minutes after their last use, their files are deleted then, on `delete` and when the plugin
  stops; 1..50 devices per job (the toolbar button is disabled above 50: "Choose at most 50 devices."), status polls carry only the changed devices; methods are Operator, `start` is audited.
- Tests: `tests/Oadm.Plugins.SystemReport.Tests` (+ read-only hardware test `SystemReportHardwareTests`),
  `tests/Oadm.Server.Tests/SystemReportServerTests`.

## Hardening scan (core plugin)

`plugins/Oadm.Plugins.HardeningScan` (+ `.Client`), id `oadm.hardening-scan`, rail page **Hardening scan** (group
Security, icon `clipboardCheck`), **off by default**. One read-only scan of the chosen devices against the AXIS OS
Hardening Guide in its two levels (Basic, Extended), one icon column per check, CSV export. Check table, evidence and
decisions: plugin `README.md`.

- Devices: the purple "Selected devices" button (`PageDeviceChoice`: All devices, tags, chosen devices; the Devices page selection to start with, else the last choice on this computer); the page scans the devices of the choice (one **Scan** button) and shows only their results; nothing before a choice.
- Rules that must hold: strictly read-only (GETs and list calls only); 15 s per request, 2 min per device, 16 devices at
  once; one failed read marks only its checks; Certificate changed, Credentials required, Password not set and
  Unreachable devices are not contacted; items that cannot be checked remotely are not shown; the latest AXIS OS is
  information only; every method is Operator and not audited; results kept per device and level (at most 10,000).
- Tests: `tests/Oadm.Plugins.HardeningScan.Tests` (+ read-only hardware test).

## Web View (core plugin)

`plugins/Oadm.Plugins.WebView` (+ `.Client`), id `oadm.web-view`, rail page **Web View** (group Utilities, icon `globe`), on by
default, switchable. The web interface of the managed devices in browser tabs inside OADM, logged in with the stored
login. Decisions, the relay and tests: plugin `README.md`.

- Rules that must hold: passwords never reach the client: the embedded browser talks to a relay of its own per tab (own loopback
  address `127.1.x.y`, so the devices' login cookies never mix; macOS 127.0.0.1, `Oadm.Client/DeviceWeb/DeviceWebRelay`), the server sends every request and WebSocket to the device with the
  stored login and the pinned certificate (`DeviceWebService`); only the browser that opened the relay's one-time start
  address gets its session cookie (others 403, another Host 400); requests stay on the device; refused statuses are not
  contacted; every open is audited. Permissions "oadm.web-view:show" and "oadm.web-view:open" (full device control), both
  off for the built-in Operator by default; without "open" the page says "Your role does not allow opening devices."
- Page: left card: the page select (Web interface, Overlays, Privacy masks, Image Health Analytics, Object Analytics, License Plate Verifier,
  Custom with a path field, error below it; it shows the active tab's page, changing it opens the page in the active tab,
  new tabs use the page chosen last), the search, the devices grouped by tag (like Group by tag, "No tag" last, no
  counts; `ui:DeviceIcon` + address). Right card: one row Close all tabs, Reload, Back (browser history), the tabs (one per
  device, select-field height, scrolling sideways), then the page edge to edge. Without the OS browser runtime (WebView2,
  WebKitGTK) the page says what to install. The tree and the tabs are the shared `ui:DeviceTree` and `ui:PageTab`.

## Live View (core plugin)

`plugins/Oadm.Plugins.LiveView` (+ `.Client`), id `oadm.live-view`, rail page **Live View** (group Monitoring, icon `grid`; New view `gridAdd`),
**off by default**, switchable. Saved views of several cameras at once, like a VMS: a view is a grid of cameras, opened in a
tab. Decisions, methods and tests: plugin `README.md`.

- Rules that must hold: views live on the server, shared by the users of the same devices (plugin setting `views`; each
  view belongs to the device group of the user who made it, none = all devices; group users see and change only their
  group's views with the group's cameras, saving refuses cameras outside the owner's devices, an edit keeps the owner),
  every change pushed to the open pages that see it; a save of an older version is refused (two users); the grid is 20 x 20 cells and always fills the tab,
  a camera at least 2 x 2, never overlapping, at most 16 cameras per view and 200 views; the server checks every rule
  again (`ViewRules`, shared with the page; the grid is the shared `ui:TileGrid` with `LiveViewPluginInfo.Layout`). Roles: "show" (Operator and Viewer), "Change views" (`manage`, Operator yes,
  Viewer no; save and delete audited); the pictures need "Live view". Every camera is the host's live video component
  (`ICorePluginClientContext.LiveVideo`, controls closed in the tile, open in fullscreen); only the active tab is visible,
  so other tabs' streams rest. A removed device keeps its place and says "Removed device 1a2b3c4d".
- Page: left card (Collapse / Expand at its bottom like the rail; collapsed a strip with the views as icons) Views (New view, the list; Edit and Delete view at the right of each row) and Cameras (search, devices with video by tag, `ui:DeviceTree`); right card
  one row Close all tabs (editing: Name, Save, Cancel), the tabs (`ui:PageTab`), then the grid.
  Editing: cell lines, a frame per camera (address, X; drag inside to move, an edge or a corner to resize), a camera dragged from the tree shows a
  frame with its address where it will land: the largest free square at the drop point (up to 10 x 10), a click on a camera puts it in the first free place.
  Outside editing a click on a camera opens it alone in its own tab (filling it, never edited or saved); the camera under
  the pointer gets a thin accent frame.

## Dashboards (core plugin)

`plugins/Oadm.Plugins.Dashboards` (+ `.Client`), id `oadm.dashboards`, rail page **Dashboards** (group Monitoring, icon
`gauge`), **off by default**, switchable. Live values of OADM's events (mostly device triggers: a temperature, a plate, a
gate open) and live video on saved dashboards: a grid of widgets, each one value of one device (On / Off, Text, Number,
Gauge, Live video; a gauge has the Number settings plus Min, Max and the line width only, ticks, sizes and labels are automatic). Decisions, methods and tests: plugin `README.md`.

- Rules that must hold: the plugin never talks to a device; the values come from `ICorePluginContext.EventSubscriptions`
  and live in server memory only (latest per event type and device, at most 100,000 keys, least recently updated dropped
  first, O(1) per event; nothing kept while the plugin is off); an event offers its declared values at once (Active of an
  on / off event first; received values refine the type of a text value) and only its own devices (`EventTypeInfo.DeviceIds`); a value that may be old is dimmed with the reason (event gone, its plugin off, the
  trigger's connection lost, the device Unreachable / Credentials required / Certificate changed); pages watch only the
  widgets of their open tabs (renewed every 10 s, forgotten after 30 s), changes are pushed at most every 250 ms;
  dashboards live on the server like Live View's views (plugin setting `dashboards`, every change pushed, an older version
  refused), the grid is the shared `ui:TileGrid` (20 x 20, at least 2 x 2, never overlapping), at most 50 widgets of them
  16 live videos and 200 dashboards; colors and icons are theme names; the server checks every rule again
  (`DashboardRules`). Device groups: each dashboard belongs to the group of the user who made it (none = all devices);
  group users see and change only their group's dashboards with the group's devices, get only the events of their devices
  and sites and only the values their own pages watch; saving refuses devices outside the owner's devices; an edit keeps
  the owner.
- Roles: "show" (Operator and Viewer), **Change dashboards** (`manage`, Operator yes, Viewer no; save and delete audited);
  video needs "Live view".
- Page: left card Dashboards and Events (the events with values, device triggers first); right card the tabs, while
  editing **Add cameras** (the picker, cameras only, at most 16 live videos), and the grid; several widgets are selected with
  a selection rectangle over empty cells, Ctrl+click or Ctrl+A, moved together and changed in a group panel (what they share,
  "Mixed" where they differ); while editing the panel of the selected widget (device through the shared picker for one device,
  value, type, widget and its settings, every change shown at once); **Copy for other devices** (the copy icon of a widget's
  edit frame or the group panel: one copy per chosen device; the picker asks the server for the event's devices, `devices`). Empty states for Device triggers off
  (`ICorePluginClientContext.IsPluginOn`), no trigger yet (**Open Device triggers**) and no dashboard.
- Tests: `tests/Oadm.Plugins.Dashboards.Tests` (+ Perf 50 widgets at 4 updates a second next to 30,000 devices; headless
  screenshots with fake-mode video). Fake mode: `FakeOadmApi.Dashboards.cs`.

## Edge Recording Export (core plugin)

`plugins/Oadm.Plugins.EdgeRecordingExport` (+ `.Client`), id `oadm.edge-recording-export`, rail page **Edge Recording
Export** (group Maintenance, icon `hardDriveDownload`), **off by default**, switchable. Choose cameras (the shared device
picker, video devices only, tags allowed), all recordings or a range in this computer's time, Search, then Download into
a chosen folder: a folder per camera ("10.0.0.48 P3265-V B8A44F631339"), the camera's file names. Decisions, device API
and tests: plugin `README.md`.

- Rules that must hold: read-only for the cameras (list, capabilities, properties, export, disks; Viewer rights on the
  device suffice); the files travel camera -> server -> client through `PluginService.Download`, never stored on the
  server, never client to camera; one recording at a time; each recording cut to the range exactly as the camera listed
  it; MP4 when offered, else Matroska; files written as `.part` and renamed when complete, existing files skipped, so a
  stopped run starts again; refused statuses are not contacted; at most 50,000 recordings per camera and search, sizes
  estimated from at most 40 recordings per camera.
- Roles: "show" (Operator and Viewer: search), "Export recordings" (`export`, Operator yes, Viewer no); every downloaded
  recording is audited with the camera and the times.
- Tests: `tests/Oadm.Plugins.EdgeRecordingExport.Tests` (+ Perf 5,000 cameras, read-only hardware test that searches and
  downloads the last two minutes of the dev cameras through the in-process server).

## Edge storage (core plugin)

`plugins/Oadm.Plugins.EdgeStorage` (+ `.Client`), id `oadm.edge-storage`, rail page **Edge storage** (group Maintenance,
icon `hardDrive`), **off by default**, switchable. The SD card of every chosen camera in one table (state, size, used, wear,
encryption, retention, five count cards) and the tools of the camera's web interface for the selected cameras: Check, Set
retention, Repair, Format, Encrypt, Turn off encryption, each a task per camera in the tasks pane below the page. SD
cards only. Decisions, device API (verified on an AXIS Q1615 Mk III) and tests: plugin `README.md`.

- Devices: the purple "Selected cameras" button (`PageDeviceChoice`: All devices, tags, chosen cameras; the Devices page selection to start with, else the last choice on this computer); the page reads only the cameras of the choice and nothing before one.
- Rules that must hold: the table is read only on request and for the chosen cameras (page opens, Refresh), read-only, refused statuses not
  contacted; the tools are hidden tasks (`ShowInMenus` false) started by the page methods `change` (check, retention) and
  `format` (format, repair, encryption), whose rights decide (an action through the other method is refused); every run
  asks first and says which recordings are lost; a fresh API list before the first write ("Nothing was changed"); format,
  check and repair unmount the card first (the camera refuses them on a mounted card) and the task always mounts it again,
  also after a failure, and ignores Cancel from the unmount on; the passphrase only in memory and the POST body, never
  stored, logged or in a URL; retention 0..7,000 days (0 = no age limit).
- Roles: "show" (Operator, Viewer), "Change storage settings" (`manage`, Operator yes), "Format and encrypt storage"
  (`format`, full device control, Operator no); both run methods audited.
- Device groups: one table per device scope; a group user reads and runs the tools only on the group's cameras (ids
  outside it refuse the whole request, NOT_FOUND).
- Shared code (no copies): the disk list `EdgeDisk` / `EdgeDisks` (also Edge Recording Export and Hardening scan), the
  on-request table `plugins/Shared/DeviceSweep.cs` + `SweepRows.cs` (also Image Health Dashboard), `DeviceMessages.Refusal`
  (SDK) and `PluginEventWatch.RunAsync` (client SDK).
- Tests: `tests/Oadm.Plugins.EdgeStorage.Tests` (+ Perf 5,000 cameras); opt-in `StorageHardwareWriteTests`
  (`OADM_STORAGE_WRITE_URL` / `_USER` / `_PASSWORD`, formats the card; every tool, the card ends as it started).
- Compliance check type **SD card** (`SdCardCheck`, see "Compliance"): a working card, optional free space; only while
  Edge storage is on.

## Date and time

`plugins/Oadm.Plugins.DateTime` (+ `.Client`), id `oadm.datetime`, always on, context menu (Maintenance, icon `clock`)
**Date and time**: a clone of the ADM / AXIS Camera Station "Set date and time" dialog for any number of devices (time
zone from the 313 IANA zones of AXIS OS, daylight saving, time mode: server time, NTP (DHCP, up to 5 servers, NTS from ntp
1.5) or manual). Details, the VAPIX decision table and the device research: plugin `README.md`.

- Rules that must hold: behaves exactly like ADM (no "Keep unchanged": OK writes the time zone and the chosen time mode
  to every selected device); values the device already has are skipped; every write is verified by reading again;
  devices without the Time API fail setting the clock with "Nothing was changed" (no `date.cgi`); task names say what
  changes ("Set NTP servers ...", "Sync with server time", else "Change date and time").

## Device configuration

`plugins/Oadm.Plugins.DeviceConfig` (+ `.Client`), id `oadm.device-config`, always on, context menu (Maintenance)
**Export configuration** and **Import configuration** (AXIS OS 12.5 or later: the device's own configuration export and
import, `/config/rest/$export`, `$import`, like its web interface), **Configure devices** and **Create configuration file**
(ADM's Configure Devices: any AXIS OS with `param.cgi`). Decisions, device research, steps and the write tests on hardware:
plugin `README.md`.

- Rules that must hold: merge only; the network identity (host name, addresses, router, DHCP, device names, the multicast
  addresses) and read-only values are never imported; "Connection settings" (HTTPS, ports, login method, 802.1X, IPv6,
  web interface) are their own area, off by default, with a warning, written last when chosen; the same model only
  ("Nothing was changed" otherwise); no backup by OADM (export first); the device validates and OADM shows its answer per
  area, one failed area does not stop the others; a ZIP entry must carry the device's MAC address; Export is a dialog,
  never a task.
- **Configure devices** rules that must hold: values come from the source (a device or an ADM `.cfg` file), definitions
  are read once per model and AXIS OS (one `listdefinitions` request while it stays below the 1 MB XML limit, else per
  group); internal, hidden, read-only, constant, write-only and write-level-7 parameters are never offered; each device
  checks every value against its own definition before writing and skips what it does not have (Done with warnings); the
  dangerous parameters (`DangerousParameters`: network identity and connection, incl. the web server ports and HTTP / HTTPS
  policy, HTTP/2 and the web interface) carry a warning, are confirmed, need their own right (Operator no) and are written
  last; the device's IP addresses are never written (Network settings and Assign IP address check a new address and
  follow the device); names that must differ per device are not offered when several devices are selected; the source device keeps its own rules, schedules and stream profiles (skipped there); a rule or stream profile with the same name is replaced, a schedule with the
  same name and recurrence used again (applying twice changes nothing more); ADM files are read and written byte for byte
  (format 3, known from files only, ADM is never decompiled), passwords in them like ADM; password values never reach a log
  or an error text. **Create configuration file** is a dialog for one device, never a task.
- Tests: `tests/Oadm.Plugins.DeviceConfig.Tests` (+ Perf 5,000 devices, read-only `ConfigureHardwareTests`); opt-in
  `ImportHardwareWriteTests` (`OADM_CONFIG_WRITE_URL` / `_USER` / `_PASSWORD`) and `ConfigureHardwareWriteTests`
  (`OADM_CONFIGURE_WRITE=1`, the first dev camera: writes, writes again, removes everything; passed on the P3265-V).
- Compliance check type **Matches configuration file** (`Configure/ConfigMatchCheck`, see "Compliance").

## Applications (ACAP)

`plugins/Oadm.Plugins.Acap` (+ `.Client`), id `oadm.acap`, always on, context menu (Applications) **Applications
(ACAP)**: lists the installed applications, starts, stops and removes them (remove confirmed) and installs or upgrades
an uploaded `.eap` on all selected devices (classic Application API 1.x), and installs license key files (with the package
or on their own, matched by the MAC address in the key). Decision table and research: plugin `README.md`.

- Rules that must hold: before uploading it reads the package manifest and refuses ("Nothing was changed") a wrong
  architecture, an AXIS OS outside the package's range or below its schema minimum, root apps on AXIS OS 12+ and a
  downgrade without the explicit option; a license key is sent only to the device whose MAC address it names, for the
  installed application's id and not expired (a device without a key: Done with warnings, nothing sent); activation with a
  license code is not offered (the device has no API for it, its web page asks Axis online).
- Compliance check type **Application running** (`ApplicationRunningCheck`, see "Compliance"): the application is
  installed / running, optionally at least a version and a valid license; the choices are read from one device.

## EST server (core plugin, OADM PRO)

`plugins/Oadm.Plugins.Est` (+ `.Client`), id `oadm.est`, rail page **EST server** (group Security, icon `lock`), off by
default, `"pro": true` (runs only while OADM PRO is active), its own source-available `LICENSE`. Spec:
`docs/spec/est-server.md`; details: plugin `README.md`.

- Rules that must hold: only cameras that sign in with a valid Axis device ID (or a valid certificate the OADM CA itself issued them, naming exactly one camera) and that
  OADM manages (at a site: through that site) get certificates; OADM sets the names, never the request; the OADM CA of PKI
  signs (no CA, no PKI or no OADM PRO: 503, nothing issued); unknown and disabled profiles look the same (404); no keys or
  requests are logged; the plugin checks the license on every request, and changing or removing that check is not
  allowed by its license.
- Roles: reading for everyone; Save and Delete all of the enrollments Admin (permission "Change the EST server"), audited.
