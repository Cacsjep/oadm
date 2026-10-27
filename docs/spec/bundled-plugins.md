# Bundled plugins

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

One entry per plugin: id, what it does, on or off by default, the rules that must never break. Steps, task names,
dialogs, pages, device API, decisions and tests are in each plugin's `README.md` (`plugins/<name>/`). On / off and
always on: `docs/spec/plugins.md` "Plugins on and off".

Common to all task plugins: every device request and every wait is a named step; every successful task ends with the
engine's step **Completed**; names follow `docs/spec/tasks.md` (no secrets, at most 48 characters). A new static
address (IPv4, IPv6) is probed before the first write (`IAddressProbe`: ping, 2 tries x 1 s, plus TCP 80 / 443; the
device's own addresses are not probed); an answer fails "<address> is already in use (answers ping). Nothing was
changed.".

Device groups (`docs/spec/device-scope.md`): every bundled package sets `minSdkVersion` 0.4.0 (Upgrade firmware 0.5.0). Tasks run only on devices
of the user's view (the host refuses the whole run otherwise) and touch only their own device; queries read only the
queried device, which the host checks. Packages of task plugins and actions only run no `PluginCanary`: the host scopes
their runs and queries (server `CanaryTests.PluginsConverted`); plugins with a page run one. Event rules are for users
with all devices, so their actions (Set output, the Notifications, Run VAPIX command) act on any device or address the
rule names.

## Restart

`plugins/Oadm.Plugins.Restart`, id `oadm.restart`, always on, toolbar and context menu (Maintenance), no dialog:
`restart.cgi`, then waits until the device went offline and answers again (3 min); a different serial afterwards is a
Warning. Task name "Restart device".

## Users

`plugins/Oadm.Plugins.Users` (+ `.Client`), id `oadm.users`, always on, context menu (Users) **Users**: add a user,
change a password or role, or remove users on every selected device.

- Rules that must hold: never removes, demotes or changes the password of the account OADM uses, never removes or demotes
  the last administrator, never removes `root` before AXIS OS 11.5, and changes nothing while the OADM account is not
  known ("Nothing was changed"); passwords only in memory and the POST body, never in a step, log, warning, error or URL;
  every change is read back.

## Upgrade firmware

`plugins/Oadm.Plugins.Firmware` (+ `.Client`), id `oadm.firmware`, always on, toolbar and context menu (Maintenance)
**Upgrade firmware**: installs AXIS OS images on the selected devices, one file per model
(`FirmwareCompatibility.Choose`; a device without one shows "No file for this model."), waits for the restart, verifies
the version and commits it.

- Rules that must hold: the file is never judged by its content (name and size only, the device checks the signature);
  the decision table runs in the dialog and again on the server before the upload; downgrades need "Allow downgrade" and
  a factory default; a device with an uncommitted earlier upgrade is not touched; tasks start only for devices that
  install a file (none for up to date, no file for their model or refused); the device rolls back by itself unless OADM
  verified and committed (Commit firmware waits up to 5 minutes while AXIS OS has not finished booting); at most 4
  devices at a time.

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
- Device groups: `checkAddresses` of a user with a device group names only the group's devices; an address another
  managed device on the route has comes back "In use" (`AddressStatus.UsedByDevice`, from
  `ITaskQueryContext.ManagedAddressesAsync`), never with its name or id. The tasks refuse such an address before the
  first write for every user (`ITaskExecutionContext.ManagedAddressesAsync`, "Another device managed by OADM uses this
  address. Nothing was changed.").

## Snapshot report (core plugin)

`plugins/Oadm.Plugins.SnapshotReport` (+ `.Client`), id `oadm.snapshot-report`, rail page **Snapshot report** (group
Reporting, icon `snapshot`), on by default. The yearly maintenance check: current snapshots of every video source of the
chosen cameras, exported as a PDF report (PDFsharp + MigraDoc, MIT).

- Devices: the "Selected cameras" command button in the page toolbar (`PageDeviceChoice`, `plugins/README.md` "Choose
  devices first"); the page reads only the cameras of the choice and nothing before one.
- Rules that must hold: read-only for devices (param.cgi reads and image.cgi snapshots with the stored credentials,
  server side); opening the page contacts no device (**Create snapshots** starts the first load); at most 4 device
  requests at a time, 3 s each; devices with Credentials required, Password not set or Certificate changed are one error
  tile without a request; error texts from the shared `DeviceMessages`; report jobs live in server memory only from
  finished until the client downloaded the PDF (2 MB chunks; the last chunk drops the report at once, the client never
  downloads twice: a second read answers "The report no longer exists; create it again."); a report never downloaded is
  dropped 30 minutes after it ended (by a timer), and with the user's job when the host ends it (no re-download); a user
  builds at most 2 reports at once, a further one waits its turn ("Wait for your earlier reports to finish"); all
  finished PDFs together stay within 1 GB (the oldest finished ones are dropped first).
- Device groups: only cameras of the user's view ("All" = the view); reports are jobs of their user, another user's
  report id answers like an unknown one.

## VAPIX Commander (core plugin)

`plugins/Oadm.Plugins.VapixCommander` (+ `.Client`), id `oadm.vapix-commander`, rail page **VAPIX Commander** (group
Automation, icon `command`), on by default. Pick VAPIX commands from a library (176 bundled, `Library/*.json`) or saved
ones, fill their fields and roll them out to many devices, or send one to one device; the event rule action **Run VAPIX
command**. Command format: `Schema/command-format.md` + `command.schema.json` (the contract).

- Rules that must hold: every value is checked before anything is sent; requests stay on the device (paths through
  `CommandValidator.IsDevicePath`); payloads stay in memory, passwords are never logged (`***` in request lines and
  bodies), saved passwords are encrypted with the master key and never listed or exported; a file field (at most 1 MB)
  travels inline with the command and only as a part of a `multipart` body, never in a URL, header or log; a rollout
  checks every command first (nothing starts on an error), writes need a confirmation, dangerous commands a second one,
  and each device re-checks a fresh API list before a write ("Missing API x. Nothing was sent."). The response kind
  decides how success, errors and values are read (Axis devices answer errors with HTTP 200).
- Saved commands: plugin setting `savedCommands`, at most 500, shared by all clients, export / import as library files.
- Roles: methods are Operator; `tryRequest` and `rollout` are audited. Scale: 30,000 target devices, every list action
  under 200 ms (test).
- Device groups: devices, clients and tasks only through the call (`tryRequest`, `checkCompatibility`, `rollout`: an id
  outside the user's view refuses the request like an unknown one); `save`, `delete` and `import` only for users with
  all devices (`RequiresAllDevices`; the page hides New command, Import, Edit, Delete and Save with values); a saved
  command's stored passwords only for users with all devices (others get `CommanderTexts.SavedPasswordsRefused` and
  enter the passwords); the task owner is the caller.

## NTP server (core plugin)

`plugins/Oadm.Plugins.NtpServer` (+ `.Client`), id `oadm.ntp-server`, rail page **NTP server** (group Servers, icon
`metronome`), on by default. Own RFC 5905 server-mode implementation (SNTPv4 compatible) on UDP 123, no library;
contributes the task **Use OADM as NTP server**.

- Rules that must hold: requests are always answered at once from the current time source state, never waiting for the
  upstream; without an upstream OADM serves its own clock as stratum 10 (never "unsynchronized"); the OS clock is served,
  never set; an upstream is validated with one query before it is saved; abuse protection per client IP (burst 8, 1 per
  2 s, one Kiss-o'-Death RATE a minute) and 2,000 requests/s overall; a failed bind is retried every 30 s; status texts
  in plain words (`NtpStatusTexts`: port in use, permission, interface missing, upstream not answering); tests never use
  port 123 on a real interface.
- Roles: `save` is Admin and audited. The Windows service opens UDP 123 in the firewall while enabled.
- Device groups: `save` only for users with all devices (the form is read-only for the others); users with a group see
  the requests and sites of their own devices, live through their group's scope. NTP and DHCP: a publish round never
  reads the device table (each group's view is kept, built again when the group's devices change or after 5 minutes); a
  group page reads the state again every 5 minutes while shown (`Renew`), a group not read for 15 minutes is forgotten.

## DHCP server (core plugin)

`plugins/Oadm.Plugins.DhcpServer` (+ `.Client`), id `oadm.dhcp-server`, rail page **DHCP server** (group Servers, icon
`network`), on by default, starts disabled, not for devices at a site (`siteSupport` none). Own RFC 2131 / 2132
implementation, IPv4 only, one interface, no relay agents; static leases, the lease list, and optionally adding Axis
devices that get an address. Per-OS setup and the manual test plan: plugin `README.md`.

- Rules that must hold: before a new address is offered it is probed (ping + TCP 80 / 443; static leases are not
  probed); another DHCP server on the network is checked on Save, every 10 minutes and at start, and enabling next to one
  needs an explicit "Enable anyway"; abuse protection per MAC and overall, pending offers capped at half the pool; a
  DECLINE counts only from the MAC that holds the address (bound or offered; dynamic ones inside the pool), any other is
  ignored (debug log), so a forged one never moves a lease; mask, router, DNS and domain come from the interface, never
  asked; leases survive restarts; the automatic add uses the add page's pipeline on the server (Axis MAC blocks only, the
  Axis check before any credential, the credential list only) and follows managed devices to their new address also when
  it is off; tests never send DHCP on a real interface.
- Roles: `save`, `saveStatic`, `deleteStatic`, `makeStatic`, `release` are Admin and audited. The Windows service opens
  UDP 67 in the firewall while enabled.
- Device groups: every change only for users with all devices; users with a group see the leases of their own devices
  (by MAC address = serial only, never by address), live through their group's scope. A change and a removal of one batch
  go in one event (parts of one version only beyond 2,000 leases; the page applies every part of a version).

## PKI (core plugin)

`plugins/Oadm.Plugins.Pki` (+ `.Client`), id `oadm.pki`, rail page **PKI** (group Security, icon `shield`), always on.
The package (one server and one client assembly, one `plugin.json`) also holds the core plugins **EST server**
(`oadm.est`, `Est/`) and **802.1X server** (`oadm.radius`, `Radius/`), whose pages are tabs of the PKI page
(`ICorePluginPage.TabOf`). Only these two servers need OADM PRO, PKI itself never does. Spec: `docs/spec/pki.md`.

PKI holds the CAs for device certificates (a root CA "OADM Root CA <server>" made on the first start, then an ordinary
root CA; Create CA makes root and intermediate CAs, Import adds CAs, Delete takes any CA out of the list; every CA with
its key can issue), the server's trust in them, the policies (every rule about certificates, also those of the 802.1X
and EST servers), the list of every certificate OADM uses, the events "Certificate expires soon" and "Certificate
expired" (group "Certificates"), and the Security tasks of the context menu: Enable / Disable HTTPS, Enable / Disable
IEEE 802.1X, Renew certificates, View, Delete and Install certificates, Install CA certificates.

- Rules that must hold: the CA key is stored only encrypted with the master key (without `Secrets` no CA is kept; a key
  that cannot be decrypted shows "Key cannot be read" in the CA's row, nothing is overwritten; a stored CA that cannot be
  read is a row with Delete only); a CA is never deleted while a policy or a service names it or while a CA it signed is
  listed, and its certificates stay in the list; the CAs with their key are trust anchors, so OADM-issued device
  certificates rate **Trusted** without an OS change; keys of device certificates are created on the device, OADM signs
  with its own subject and SAN (never the CSR's); every task checks AXIS OS 6.53+ (REST cert v1 from a fresh
  `config/discover`, else the SOAP certificate service; `docs/spec/pki.md` "Older AXIS OS") and the key type of the
  chosen issuing policy against the device's keystore before the first write and fails "Nothing was changed" otherwise;
  the dialogs of Enable HTTPS, Enable IEEE 802.1X, Renew certificates and Install certificates (IEEE 802.1X) pick an
  issuing policy and show its summary, the run uses the policy's stored values; the built-in policies (the first HTTPS
  certificate, IEEE 802.1X certificate, 802.1X server, EST server, Expiry warning and Enrollment policy) and an issuing
  policy an EST profile names are never deleted; the 802.1X tasks always install the policy's RADIUS server CA and never
  compare it with the 802.1X server's certificate; Enable HTTPS pins the new certificate through
  `UpdateDeviceTlsAsync`; the dialogs ask when a camera's clock is behind the certificate's start, a run without that
  answer refuses; only OADM's own certificates that nothing uses are removed; passwords of imports and backups are never
  stored or logged; nothing in tests touches a real trust store; View and Delete certificates read at most 5,000 devices
  per window (each device is read, 4 at a time; a larger selection gets "Choose at most 5,000 devices."), the other tasks
  take any number; while it runs it offers its CAs to other plugins (`IDeviceCertificateAuthority`,
  `docs/spec/plugins.md` "Plugin services"), which signs the same way; every certificate a CA of PKI issues for a device
  (tasks and the service) goes into the list of issued certificates with its DER; a certificate deleted while valid,
  replaced or unused goes on the internal list of certificates no longer accepted (written before the call returns, never
  undone, never shown; `IsRevoked` is a hash lookup in memory, `docs/spec/radius-server.md` "Certificates and the list of
  certificates no longer accepted"); a certificate stays in the store 30 days after it ended, an entry of that list while
  its certificate is valid; nothing revokes in the UI; the list shows each camera's current certificate per use
  (`docs/spec/pki.md` "Current and pending").
- Roles: everything but `getState`, `getCertificates` and `getCertificate` is Admin and audited (Create CA, Delete CA,
  and the Certificates card's Create, Sign request, Import, Export and Delete: `docs/spec/pki.md` "Certificates");
  "Install in trusted root store" (every root CA) asks with the CA's SHA-256 fingerprint.
- Device groups: every method but `getState`, `getCertificates` and `getCertificate` is for users with all devices
  (`RequiresAllDevices`); the Certificates card of a user with a group lists only the device certificates of the group's
  devices; a user with a group sees the page read only, the counts of issued certificates only of the group's devices
  (the `state` event per group that read the page, `PluginEventAudience.DeviceScope`). Host names, FQDNs and a name
  address go into a device certificate only when a user with all devices added the device
  (`IDeviceInfo.AddedByAllDevices`); else only its IP addresses and `axis-<serial>.local`.

## EST server (core plugin of PKI, OADM PRO)

`plugins/Oadm.Plugins.Pki/Est` (+ `Oadm.Plugins.Pki.Client/Est`), core plugin id `oadm.est`, its page the tab **EST
server** of the PKI page, its own source-available `Est/LICENSE`. Off by default: **Turn on** / **Turn off** on its tab
(stored choice); it runs, and opens TCP 9454, only while it is on and OADM PRO is active. Contributes the Security task
**EST - Certificate Enrollment** (in the device menu only with OADM PRO). Spec: `docs/spec/est-server.md`; details:
`Est/README.md`.

- Rules that must hold: only cameras that sign in with a certificate of a CA an EST sign-in CA policy of PKI names (the
  Axis device ID, or a valid certificate a CA made in OADM itself issued them, naming exactly one camera; to renew also
  the camera's current certificate from OADM, whatever CA of PKI issued it, an imported CA with its key too), that an
  Enrollment policy holds for the profile, and that OADM manages (at a site: through that site) get certificates; OADM
  sets the names, never the request; the CA of the profile's issuing policy signs (no such CA, no PKI or no OADM PRO:
  503, nothing issued); unknown and disabled profiles look the same (404); no keys or requests are logged; a certificate
  PKI no longer accepts renews nothing (403 "Certificate deleted in OADM" or "Certificate replaced by a newer
  certificate"); the plugin checks the license on every request, and changing or removing that check is not allowed by
  its license.
- Roles: reading for everyone; Turn on, Turn off, Save and Delete all of the enrollments Admin (permission "Change the
  EST server", `oadm.est:manage`), audited.
- Device groups: Turn on, Turn off, Save and Delete all only for users with all devices; a user with a group sees the
  enrollments, Not renewed, certificates and listeners (this server, sites) of the group's devices only, never refusals
  of cameras OADM does not manage. Host names never go into certificates of a device a user with a group added.

## 802.1X server (core plugin of PKI, OADM PRO)

`plugins/Oadm.Plugins.Pki/Radius` (+ `Oadm.Plugins.Pki.Client/Radius`), core plugin id `oadm.radius`, display name
**802.1X server** (group Security, icon `lock`), its page the tab **802.1X server** of the PKI page, its own
source-available `Radius/LICENSE`. Off by default: **Turn on** / **Turn off** on its tab (stored choice); it runs, and
opens UDP 1812, only while it is on and OADM PRO is active. The switches' RADIUS server on UDP 1812 (Listen on) and at
every site with switches (the node's relay); EAP-TLS with the server certificate from the CA of its 802.1X server
policy; Sessions, Log, the event rule events "Device authenticated" and "Authentication failed"; its rules (its
certificate, TLS, trusted CAs, VLAN, Block) are PKI's policies (`docs/spec/pki.md` "Policies"). Spec:
`docs/spec/radius-server.md`; details: `Radius/README.md`.

- Rules that must hold: only devices whose client certificate passes (issued by a CA of an 802.1X trusted CA policy,
  directly or through intermediate CAs OADM issues with, a root above it only anchoring the chain, or the Axis device ID
  roots; nothing without PKI and its policies; client authentication; exactly one managed device unless the policy
  allows any device; at a site a device of that site; not on PKI's list of certificates no longer accepted; no Block
  policy holds it) are accepted; every Access-Request needs a valid Message-Authenticator; malformed packets and requests
  of unknown senders are dropped, never answered; OADM PRO is checked on every request; shared secrets are stored only
  encrypted, never logged and never sent to a page; keys are never logged.
- Roles and device groups: reading for everyone with the page; Turn on, Turn off, saves and Delete all Admin (permission
  "Change the 802.1X server", `oadm.radius:manage`), audited, and only for users with all devices; the policies need
  PKI's "Manage the certificate authority"; a user with a group sees the Sessions and Log rows of the group's devices
  (rows without a managed device never), no server-wide settings and only the switches of the group's routes.

## Metadata Monitor (core plugin)

`plugins/Oadm.Plugins.MetadataMonitor` (+ `.Client`), id `oadm.metadata-monitor`, rail page **Metadata Monitor** (group
Monitoring, icon `activity`), **off by default**. A port of the AXIS Metadata Monitor tool: pick one camera
(`DevicePickerWindow.ShowOneAsync`), Start, and watch its event stream live.

- Rules that must hold: read-only for devices; the server opens the RTSP event stream with the stored credentials
  (Digest; Basic on plain RTSP only with the server setting Devices.AllowBasicOverHttp; `ICorePluginContext.EventStreams`),
  passwords never reach the client; events only (`media.amp?video=0&audio=0&event=on`), one camera at a time, nothing
  saved; refused statuses without a request; a stream ends with Stop, a page switch or 20 s without the page's
  keep-alive; the client keeps the newest 10,000 messages; XML through `DeviceXml` (1 MB, no DTD). The RTSP port of a
  camera behind a port forward is set in the device list (**OADM > RTSP port**); Start never changes it (an older
  client's port is ignored).
- Device groups: streams are jobs of their user (another user's id answers like an unknown one), opened through the
  user's call, sent to the page's session only; a stream ends Stopped when the user's group or its rules change (the host
  ends the job), with Error when the camera alone leaves the user's view. At most 16 streams (the place is taken before
  the stream opens); a stream the server no longer has (a lease that expired, a server restart) shows as Stopped on the
  page at the next keep-alive, never as Live.

## Image Health Dashboard (core plugin)

`plugins/Oadm.Plugins.ImageHealth` (+ `.Client`), id `oadm.image-health`, rail page **Image Health Dashboard** (group
Monitoring, icon `eye`), **off by default**. One table with the AXIS Image Health Analytics status of every chosen camera
that has the app, with five count cards (Good, Detected, Pending, Detection off, Errors). Not in fake mode.

- Devices: the "Selected cameras" command button in the page toolbar (`PageDeviceChoice`); the page reads only the
  cameras of the choice and nothing before one.
- Rules that must hold: read-only for devices (`GET /local/AXISImageHealthAnalytics/v1/status` with the stored
  credentials); only on request and for the chosen cameras (the page opens, Refresh, Auto refresh every 10 s while the
  page is shown, off by default), nothing in the background and no events (event rules use the app's own events through
  Device triggers); 24 cameras at a time, 3 s each; a stopped app stays as **Not running**; a detection turned off is no
  problem (the camera counts as Good); refused statuses without a request. Methods `check` and `getState` are Operator.
- Device groups: one table per device scope; a group user checks and sees only the group's cameras, chosen ids outside it
  are refused whole (NOT_FOUND).

## I/O Monitor (core plugin)

`plugins/Oadm.Plugins.IoMonitor` (+ `.Client`), id `oadm.io-monitor`, rail page **I/O Monitor** (group Monitoring, icon
`toggle`), **off by default**. A live display of the inputs and outputs of one device (`DevicePickerWindow.ShowOneAsync`),
drawn as a faceplate; no events, no actions, no switching.

- Rules that must hold: read-only (`getPorts` of `io-port-management` 1.x every 200 ms while a page shows the device, 3 s
  per request, 1 s pause after a failure); one reading per device shared by every page; stopped after 20 s without the
  page's keep-alive; at most 20 devices at once; refused statuses without a request; the VAPIX client always from the
  factory, never kept or disposed.
- Device groups: streams are jobs of their user (another user's id answers like an unknown one), ports go to the page's
  session only, a reading is shared within one device scope and ends when the device leaves its view.

## Device triggers (core plugin)

`plugins/Oadm.Plugins.DeviceTriggers` (+ `.Client`), id `oadm.device-triggers`, rail page **Device triggers** (group
Automation, icon `flame`), **off by default**. A trigger is an event the chosen devices offer (I/O ports, virtual inputs,
temperature, storage, Object Analytics scenarios, other apps), optionally narrowed per source item; every trigger is an
event of the event rules, plus "<Trigger>: connection lost".

- Rules that must hold: the event list is read live from the devices for every create and edit, never cached; devices
  come from the shared device picker (All devices, tags, single devices), resolved after every change and every 30 s; all
  turned-on triggers together watch at most 10,000 devices (`MaxWatchedDevices`; one permanent connection each, Windows
  has 16,384 outgoing ports by default): a save or turn-on over it is refused ("Device triggers watch at most 10,000
  devices. Choose tags or fewer devices."), a tag or All devices that grows past it keeps the devices it watches and the
  row says "More than 10,000 devices: some are not watched"; devices the status poll found not answering are not
  contacted (no connection attempts in an outage; the device connects again once a poll sees it Ok); one permanent
  WebSocket per device (session token from `wssession.cgi`, the pinned certificate), at most 16 connecting at once, a new
  filter only where triggers changed; a refused filter is a problem of its own triggers only; turned-off and empty
  triggers report off at once (rules never wait for a report that cannot come); a problem keeps the last state and turns
  "connection lost" on after 30 s; refused statuses are not contacted. Administrators change triggers (audited),
  operators see them.
- Device groups: every trigger belongs to the group of the user who made it (none = all devices) and watches only that
  group's devices ("All devices" and tags = the group's), so its events come only from them; group users see and change
  only their group's triggers; a deleted group's triggers watch nothing; the editor's reads are the user's jobs.

## Action Rule Editor (core plugin)

`plugins/Oadm.Plugins.ActionRules` (+ `.Client`), id `oadm.action-rules`, rail page **Action Rule Editor** (group
Automation, icon `workflow`), **off by default**. The devices' own action rules, recipients and schedules (their web
interface: Events > Rules, Recipients, Schedules) of the chosen devices in one page, grouped by what they do, and
changed, turned on or off, copied to other devices, deleted and undone for many at once; **New rule** builds one rule for
many devices. Every settings form uses the bundled action catalog (`Catalog/action-catalog.json`).

- Devices: the "Selected devices" command button in the page toolbar (`PageDeviceChoice`); the page reads only the
  devices of the choice and nothing before one.
- Rules that must hold: read only on request, over SOAP (passwords in plain text stay on the server, never reach the
  client or a log, a login inside an address shows as `user:********@` and keeps each device's own password); an edit
  changes only the checked parts, everything else stays as each device has it; a rule is written only while it is
  exactly as the page read it (a version keyed with a server-only secret, so no password can be guessed against it), one
  task per device at a time; in place over REST `action-rules` v2 where the device lists it (AXIS OS 12; the action only
  when it changes, never a `**Redacted**` password), else recreated over SOAP (new rule first, then the old one); every
  write is saved for undo (before and after, 30 days, passwords encrypted with the master key, also values with a
  password inside an address) and undo puts back only what is still as the run left it, once no task of the run is
  queued or running; a hidden password inside an address is kept only for the same user and server; OADM never creates,
  changes a rule to or copies "Erase configuration"; a new rule is checked whole on the server before any task starts and
  created by the same task as a copy; refused statuses are not contacted; a copied rule brings the schedules it uses
  first (OADM's own id `com.axis.schedules.oadm.<name>`, the same on every device); topics are written without the `//.`
  AXIS OS 10 appends; built-in schedules are never changed or deleted, a used schedule is deleted only together with its
  rules.
- Roles: "show" (Operator, Viewer), "Change action rules" (`manage`, Operator yes), "Delete action rules" (`delete`,
  Operator yes); every change, copy, delete and undo is audited.
- Device groups: one table per device scope; every device id of a request in the caller's devices or the whole request
  is refused (NOT_FOUND) before anything is read, saved or started; undo runs per group.

## System report (core plugin without a page)

`plugins/Oadm.Plugins.SystemReport` (+ `.Client`), id `oadm.system-report`, always on, a toolbar button **System report**
like ADM's "Get system report": the server downloads the server report of every selected device and the client saves
one ZIP for Axis support.

- Rules that must hold: read-only for devices (`serverreport.cgi`, `zip_with_image` for video devices, else `zip`); the
  answer is streamed to disk in the plugin's data folder (`VapixRequestOptions.StreamResponse`), must start with a ZIP
  signature, at most 256 MB and 5 minutes per device, at most 4 devices at a time for all jobs; refused statuses without
  a request; jobs live 30 minutes after their last use, their files are deleted then, on `delete` and when the plugin
  stops; 1..50 devices per job (the toolbar button is disabled above 50: "Choose at most 50 devices."), status polls
  carry only the changed devices; methods are Operator, `start` is audited.
- Device groups: every method is for users with all devices (`RequiresAllDevices`, like backups and the audit log); the
  toolbar button is hidden for users with a device group.

## Hardening scan (core plugin)

`plugins/Oadm.Plugins.HardeningScan` (+ `.Client`), id `oadm.hardening-scan`, rail page **Hardening scan** (group
Security, icon `clipboardCheck`), **off by default**. One read-only scan of the chosen devices against the AXIS OS
Hardening Guide in its two levels (Basic, Extended), one icon column per check, CSV export. Check table and evidence:
plugin `README.md`.

- Devices: the "Selected devices" command button in the page toolbar (`PageDeviceChoice`); the page scans the devices of
  the choice (one **Scan** button) and shows only their results; nothing before a choice.
- Rules that must hold: strictly read-only (GETs and list calls only); 15 s per request, 2 min per device, 16 devices at
  once; one failed read marks only its checks; Certificate changed, Credentials required, Password not set and
  Unreachable devices are not contacted; items that cannot be checked remotely are not shown; the latest AXIS OS is
  information only; every method is Operator and not audited; results kept per device and level (at most 30,000, the
  device limit `DeviceLimits.MaxDevices`).
- Device groups: one scan at a time per device scope, a job of its user that every user of the scope may stop, results
  and devices of the user's view only; the scans of all scopes share one limit (16 devices at once for the plugin, the
  setting `parallelism`). The same for the shared `DeviceSweep` of Image Health Dashboard, Edge storage and Action Rule
  Editor: one limit per plugin, never per device scope.

## Web View (core plugin)

`plugins/Oadm.Plugins.WebView` (+ `.Client`), id `oadm.web-view`, rail page **Web View** (group Utilities, icon `globe`),
on by default. The web interface of the managed devices in browser tabs inside OADM, logged in with the stored login.
Without the OS browser runtime (WebView2, WebKitGTK) the page says what to install.

- Rules that must hold: passwords never reach the client: the embedded browser talks to a relay of its own per tab (own
  loopback address `127.1.x.y`, so the devices' login cookies never mix; macOS 127.0.0.1,
  `Oadm.Client/DeviceWeb/DeviceWebRelay`), the server sends every request and WebSocket to the device with the stored
  login and the pinned certificate (`DeviceWebService`); only the browser that opened the relay's one-time start address
  gets its session cookie (others 403, another Host 400); requests stay on the device; refused statuses are not
  contacted; every open is audited. Permissions "oadm.web-view:show" and "oadm.web-view:open" (full device control), both
  off for the built-in Operator by default; without "open" the page says "Your role does not allow opening devices."
- Device groups: no page methods; the tree lists only the user's devices and `DeviceWebService` takes the device from the
  user's view, so a tab's requests end when its device leaves the view (its tab closes like a removed device's).

## Live View (core plugin)

`plugins/Oadm.Plugins.LiveView` (+ `.Client`), id `oadm.live-view`, rail page **Live View** (group Monitoring, icon
`grid`; New view `gridAdd`), **off by default**. Saved views of several cameras at once, like a VMS: a view is a grid of
cameras, opened in a tab.

- Rules that must hold: views live on the server, shared by the users of the same devices (plugin setting `views`; each
  view belongs to the device group of the user who made it, none = all devices; group users see and change only their
  group's views with the group's cameras, saving refuses cameras outside the owner's devices, an edit keeps the owner),
  every change pushed to the open pages that see it; a save of an older version is refused (two users); the grid is 20 x
  20 cells and always fills the tab, a camera at least 2 x 2, never overlapping, at most 16 cameras per view and 200
  views; the server checks every rule again (`ViewRules`, shared with the page; the grid is the shared `ui:TileGrid` with
  `LiveViewPluginInfo.Layout`). Every camera is the host's live video component (`ICorePluginClientContext.LiveVideo`);
  only the active tab is visible, so other tabs' streams rest. A removed device keeps its place and says "Removed device
  1a2b3c4d".
- Roles: "show" (Operator and Viewer), "Change views" (`manage`, Operator yes, Viewer no; save and delete audited); the
  pictures need "Live view".

## Dashboards (core plugin)

`plugins/Oadm.Plugins.Dashboards` (+ `.Client`), id `oadm.dashboards`, rail page **Dashboards** (group Monitoring, icon
`gauge`), **off by default**. Live values of OADM's events (mostly device triggers: a temperature, a plate, a gate open)
and live video on saved dashboards: a grid of widgets, each one value of one device (On / Off, Text, Number, Gauge, Live
video). Empty states for Device triggers off (`ICorePluginClientContext.IsPluginOn`), no trigger yet (**Open Device
triggers**) and no dashboard.

- Rules that must hold: the plugin never talks to a device; the values come from `ICorePluginContext.EventSubscriptions`
  and live in server memory only (latest per event type and device, at most 100,000 keys, least recently updated dropped
  first, O(1) per event; nothing kept while the plugin is off); an event offers its declared values at once (Active of an
  on / off event first; received values refine the type of a text value) and only its own devices
  (`EventTypeInfo.DeviceIds`); a value that may be old is dimmed with the reason (event gone, its plugin off, the
  trigger's connection lost, the device Unreachable / Credentials required / Certificate changed); pages watch only the
  widgets of their open tabs (renewed every 10 s, forgotten after 30 s), changes are pushed at most every 250 ms;
  dashboards live on the server like Live View's views (plugin setting `dashboards`, every change pushed, an older
  version refused), the grid is the shared `ui:TileGrid` (20 x 20, at least 2 x 2, never overlapping), at most 50 widgets
  of them 16 live videos and 200 dashboards; colors and icons are theme names; the server checks every rule again
  (`DashboardRules`).
- Device groups: each dashboard belongs to the group of the user who made it (none = all devices); group users see and
  change only their group's dashboards with the group's devices, get only the events of their devices and sites and only
  the values their own pages watch; saving refuses devices outside the owner's devices; an edit keeps the owner. Pushes
  never read the device table: each group's devices are read once and again only when the group's devices change (or
  after 5 minutes), a device's membership is the host's check per device. At most 1,000 watches, 8 per login session (a
  new one replaces the session's oldest).
- Roles: "show" (Operator and Viewer), **Change dashboards** (`manage`, Operator yes, Viewer no; save and delete
  audited); video needs "Live view".

## Edge Recording Export (core plugin)

`plugins/Oadm.Plugins.EdgeRecordingExport` (+ `.Client`), id `oadm.edge-recording-export`, rail page **Edge Recording
Export** (group Maintenance, icon `hardDriveDownload`), **off by default**. Choose cameras, all recordings or a range in
this computer's time, Search, then Download into a chosen folder: a folder per camera ("10.0.0.48 P3265-V
B8A44F631339"), the camera's file names.

- Rules that must hold: read-only for the cameras (list, capabilities, properties, export, disks; Viewer rights on the
  device suffice); the files travel camera -> server -> client through `PluginService.Download`, never stored on the
  server, never client to camera; one recording at a time; each recording cut to the range exactly as the camera listed
  it; MP4 when offered, else Matroska; files written as `.part` and renamed when complete, existing files skipped, so a
  stopped run starts again; refused statuses are not contacted; at most 50,000 recordings per camera and search, sizes
  estimated from at most 40 recordings per camera.
- Roles: "show" (Operator and Viewer: search), "Export recordings" (`export`, Operator yes, Viewer no); every downloaded
  recording is audited with the camera and the times.
- Device groups: search and export take the camera from the user's call; a camera outside the user's group answers like
  an unknown one and is never contacted.

## Edge storage (core plugin)

`plugins/Oadm.Plugins.EdgeStorage` (+ `.Client`), id `oadm.edge-storage`, rail page **Edge storage** (group Maintenance,
icon `hardDrive`), **off by default**. The SD card of every chosen camera in one table (state, size, used, wear,
encryption, retention, five count cards) and the tools of the camera's web interface for the selected cameras: Check, Set
retention, Repair, Format, Encrypt, Turn off encryption, each a task per camera in the tasks pane below the page. SD cards
only. Compliance check type **SD card** (`SdCardCheck`, `docs/spec/compliance.md`): a working card, optional free space;
only while Edge storage is on.

- Devices: the "Selected cameras" command button in the page toolbar (`PageDeviceChoice`); the page reads only the
  cameras of the choice and nothing before one.
- Rules that must hold: the table is read only on request and for the chosen cameras (page opens, Refresh), read-only,
  refused statuses not contacted; the tools are hidden tasks (`ShowInMenus` false) started by the page methods `change`
  (check, retention) and `format` (format, repair, encryption), whose rights decide (an action through the other method is
  refused); every run asks first and says which recordings are lost; a fresh API list before the first write ("Nothing
  was changed"); format, check and repair unmount the card first (the camera refuses them on a mounted card) and the task
  always mounts it again, also after a failure, and ignores Cancel from the unmount on; the passphrase only in memory and
  the POST body, never stored, logged or in a URL; retention 0..7,000 days (0 = no age limit).
- Roles: "show" (Operator, Viewer), "Change storage settings" (`manage`, Operator yes), "Format and encrypt storage"
  (`format`, full device control, Operator no); both run methods audited.
- Device groups: one table per device scope; a group user reads and runs the tools only on the group's cameras (ids
  outside it refuse the whole request, NOT_FOUND); a user with all devices reads the chosen cameras that still exist
  (removed ones are skipped; runs stay refused as a whole).

## Date and time

`plugins/Oadm.Plugins.DateTime` (+ `.Client`), id `oadm.datetime`, always on, context menu (Maintenance, icon `clock`)
**Date and time**: a clone of the ADM / AXIS Camera Station "Set date and time" dialog for any number of devices (time
zone from the 313 IANA zones of AXIS OS, daylight saving, time mode: server time, NTP (DHCP, up to 5 servers, NTS from ntp
1.5) or manual).

- Rules that must hold: behaves exactly like ADM (no "Keep unchanged": OK writes the time zone and the chosen time mode
  to every selected device); values the device already has are skipped; every write is verified by reading again;
  devices without the Time API fail setting the clock with "Nothing was changed" (no `date.cgi`); task names say what
  changes ("Set NTP servers ...", "Sync with server time", else "Change date and time").

## Device configuration

`plugins/Oadm.Plugins.DeviceConfig` (+ `.Client`), id `oadm.device-config`, always on, context menu (Maintenance)
**Export configuration** and **Import configuration** (AXIS OS 12.5 or later: the device's own configuration export and
import, `/config/rest/$export`, `$import`, like its web interface), **Configure devices** and **Create configuration file**
(ADM's Configure Devices: any AXIS OS with `param.cgi`). Compliance check type **Matches configuration file**
(`Configure/ConfigMatchCheck`, `docs/spec/compliance.md`).

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
  dangerous parameters (`DangerousParameters`: network identity and connection, incl. the web server ports and HTTP /
  HTTPS policy, HTTP/2 and the web interface) carry a warning, are confirmed, need their own right (Operator no) and are
  written last; the device's IP addresses are never written (Network settings and Assign IP address check a new address
  and follow the device); names that must differ per device are not offered when several devices are selected; the
  source device keeps its own rules, schedules and stream profiles (skipped there); a rule or stream profile with the
  same name is replaced, a schedule with the same name and recurrence used again (applying twice changes nothing more);
  ADM files are read and written byte for byte (format 3, known from files only, ADM is never decompiled), passwords in
  them like ADM; password values never reach a log or an error text. **Create configuration file** is a dialog for one
  device, never a task.
- Device groups: the source device of Configure devices and Create configuration file is read with the user's query
  (only devices of the user's view); configuration files stay on the client (an import uploads its file for its own
  run), so the server keeps no shared file with saved passwords that a group user could use.

## Applications (ACAP)

`plugins/Oadm.Plugins.Acap` (+ `.Client`), id `oadm.acap`, always on, context menu (Applications) **Applications
(ACAP)**: lists the installed applications, starts, stops and removes them (remove confirmed) and installs or upgrades
an uploaded `.eap` on all selected devices (classic Application API 1.x), and installs license key files (with the
package or on their own, matched by the MAC address in the key). Compliance check type **Application running**
(`ApplicationRunningCheck`, `docs/spec/compliance.md`): the application is installed / running, optionally at least a
version and a valid license; the choices are read from one device.

- Rules that must hold: before uploading it reads the package manifest and refuses ("Nothing was changed") a wrong
  architecture, an AXIS OS outside the package's range or below its schema minimum, root apps on AXIS OS 12+ and a
  downgrade without the explicit option; a license key is sent only to the device whose MAC address it names, for the
  installed application's id and not expired (a device without a key: Done with warnings, nothing sent); activation with
  a license code is not offered (the device has no API for it, its web page asks Axis online).

## Outputs and Notifications (event rule actions)

Server only, always on, no page. Spec: `docs/spec/event-rules.md`.

- **Outputs** (`plugins/Oadm.Plugins.Outputs`, id `oadm.outputs`): the action **Set output** switches an output port of
  a device (follow the rule, pulse, set active / inactive, toggle). Rule that must hold: over the I/O port management API the
  port must exist and be an output before anything is written.
- **Notifications** (`plugins/Oadm.Plugins.Notifications`, id `oadm.notifications`): the actions **Send email**, **Send
  HTTP request** and **Send TCP message**, without devices. Rules that must hold: placeholder values are escaped for where
  they go, never the text around them; passwords and tokens are stored encrypted; untrusted certificates only with
  "Allow untrusted certificates" (off by default).
