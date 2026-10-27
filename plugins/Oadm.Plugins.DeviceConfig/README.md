# Device configuration (`oadm.device-config`)

Task plugins (+ `.Client`), always on. Four context menu entries under **Maintenance**, like ADM's configuration
handling. Rules: [Bundled plugins](../../docs/spec/bundled-plugins.md#device-configuration).

- **Export configuration**: save the configuration of the selected devices to files (AXIS OS 12.5 or later).
- **Import configuration**: apply one configuration file to the selected devices (AXIS OS 12.5 or later).
- **Configure devices**: ADM's Configure Devices: set chosen parameters, action rules and stream profiles on many devices,
  taken from a device or an ADM configuration file (any AXIS OS with `param.cgi`).
- **Create configuration file**: save chosen settings of one device as an ADM file.

## Export and Import configuration

Users expect "the same as the camera's web interface" (Maintenance > Export / Import), so OADM uses the same device API
and the same file format. Like the web interface, it does not cover every setting.

### Device API (AXIS OS 12.5 or later)

Source: [Device Configuration APIs](https://developer.axis.com/vapix/device-configuration/device-configuration-apis/),
the OpenAPI documents of the device (`/config/discover/apis/<api>/<ver>/openapi.json`) and the device itself.

| Operation | Request |
|---|---|
| Export all | `GET /config/rest/$export` |
| Export one API | `GET /config/rest/<api>/<ver>/$export` (param: `/config/rest/param/v2beta/$export`) |
| Import all | `PATCH /config/rest/$import` |
| Import one API | `PATCH /config/rest/<api>/<ver>/$import` |

- Export answer: `{"status":"success","data":{"<api>.<ver>":{...}},"metadata":{"fwVersion":"12.11.77","prodNbr":"P3265-V"}}`,
  optional `warnings` / `information` (string arrays).
- Import body: `{"data":{...}}`; options as query parameters: `importType` `merge` (keep values not in the file) or
  `default` (reset the API, missing values become defaults); `strict` (bool): whether unknown properties are refused.
- Import answer: `{"status":"success","warnings":[...],"information":[...]}`; errors 400 (schema), 413 (too large), 414,
  401; error body `{"status":"error","error":{"code":n,"message":"..."}}`.
- **No dry run.** The device validates the import itself; OADM shows its answer.
- AXIS OS: export and import from 12.5 behind a feature flag, on by default from 12.10; 12.11 has no flag.
- Each API decides for itself: `export_import` on the API and per property in `model.json`. Secrets (`secret: true`) are
  never exported but may be imported.

What an AXIS P3265-V with AXIS OS 12.11.77 exports: 7 of the 38 configuration APIs export, 1,315 values, 58 KB; two exports a minute apart were identical.

| API | Content |
|---|---|
| `param.v2` (beta, path `param/v2beta`) | 183 parameters: Network (60), System (23), RemoteService, SNMP, API (syslog), Layout, PTZ, Image, Audio, Time, HTTPS, StatusLed, ... |
| `action-rules.v2` | Rules and recipients (recipient passwords left out) |
| `event-schedules.v2` | Schedules |
| `event-mqtt-bridge.v2` | MQTT event publication / subscription |
| `time.v2` | Time zone |
| `power-settings.v2` | Power settings |
| `user-management.v2` | Password policy only (no users) |

Not exported: users and passwords, certificates, network-settings v2, firewall, SSH, SNMP v1 (REST), OIDC, recordings,
analytics, ACAP applications and their settings, overlays, stream profiles beyond param, and every API without the flag.

The export contains values that belong to **one device** and some that cannot be written:

- Identity: `Network.HostName`, `Network.IPAddress`, `Network.SubnetMask`, `Network.DefaultRouter`, `Network.Broadcast`,
  `Network.BootProto`, `Network.Bonjour.FriendlyName`, `Network.UPnP.FriendlyName` (contains the MAC address),
  `Network.VolatileHostName.*`. Imported as they are, one file would give every device the same host name and, with
  `BootProto=none`, the same IP address.
- Read-only or runtime: `Network.DHCP.VendorClass` (model and firmware), `SNMP.EngineBoots`, `SNMP.InitialUserPasswdSet`,
  `power-settings` `powerStatus`.
- Derived from the MAC address: the multicast addresses `Network.RTPCollection.R<n>.VideoAddress` / `AudioAddress`.
- **Connection settings** decide whether OADM can still reach and log in to the device: `HTTPS.Enabled`, `HTTPS.Port`,
  `System.BoaPort` (HTTP port), `System.BoaGroupPolicy.*` (HTTP / HTTPS per user group), `System.WebInterfaceDisabled`,
  `Network.HTTP.AuthenticationPolicy`, `Network.IPv6.Enabled`, `Network.InterfaceCollection.I<n>.dot1x.Enabled`
  (`ConfigIdentity.ConnectionPaths`).

### Decisions

| Topic | Decision |
|---|---|
| Merge only | `importType=default` resets whole APIs (network included) and can make a device unreachable; not offered. |
| Network identity | Never imported (the list above); no option to turn this off. |
| Backup | None by OADM. Whoever wants to go back exports first. |
| Same model only | A device of another model than the file's `metadata.prodNbr` is refused ("Nothing was changed"). Other AXIS OS versions are allowed. |
| The device validates | OADM only uses `$export` / `$import` and does not check the file against the device's OpenAPI schema; it removes the identity values, sends each area and shows the device's answer per step. |
| Two import modes | One `.json` file applied to every selected device of its model, or the ZIP of a multi-device export where each device gets its own file back (matched by MAC address). |
| Connection settings | Their own area, off by default: listed after the file's areas (unchecked) with a warning; unchecked, their values are left out of "Network and system settings" and of the verify like identity values; checked, they are written last in their own step and the confirmation warns. |

### Export

- `oadm.device-config.export` (icon `export`) is dialog only, never a task. `CanRun`: status Ok and AXIS OS 12.5 or
  later (cached firmware version); reason "Needs AXIS OS 12.5 or later (this device has ...)".
- The save dialog comes first: one device `<address>_<MAC>_<model>_config.json`, several devices
  `oadm-config-<yyyy-MM-dd>.zip` with one file each plus `summary.txt` when some failed.
- The window (`ExportConfigWindow`, like System report: Devices card with progress row, result chip, virtualized grid
  Address, MAC address, Model, Status Waiting / Exporting / Done + size / Failed + reason; Cancel, then Close) reads each
  device through the read-only query `export` (`ITaskPluginQuery`, `GET /config/rest/$export`, stored credentials) at
  most 8 at a time and writes each answer into the ZIP as it arrives (one entry open at a time; 5,000 devices tested).
  One device: the file is opened only when the export succeeded.
- The file is exactly the device's answer (`status`, `data`, `metadata`), so the camera web interface can import it too.
  The file name carries what OADM knows beyond that; the MAC address in it is what the ZIP import matches on.
- Refused without a request: every status but Ok and Unknown (shared `TaskSupportReasons.ForStatus` texts). Feature off
  (12.5-12.9 without the flag): 404 -> "Configuration export is turned off on this device".

### Import dialog (`ImportConfigWindow`)

- Choose file (`ui:FileRow`, `.json` at most 2 MB, or `.zip` at most 1 GB and 5,000 `.json` entries). Each configuration
  file is checked at once: valid JSON, `data` object with at least one `<api>.v<n>` object and `metadata.prodNbr`; else
  the error below the field ("Not a device configuration file", "The file does not name the device model").
- **One file** (`.json`): source line "From AXIS P3265-V, AXIS OS 12.11.77"; it goes to every selected device of that
  model (compared with the device grid's Model without "AXIS ").
- **ZIP**: every `*.json` entry is matched to a selected device by a MAC address in its name (12 hex digits, also with
  ":" "-" "." between the bytes; the first one that is a selected device's serial). Devices card (only for a ZIP):
  summary "4 ready · 2 left out · 1 file has no selected device", virtualized grid MAC address, Address, Model, File,
  Status ("Ready", "No file for this device", "Two files for this device", "Not a device configuration file",
  "Other model: the file is for AXIS P3265-V"). Read off the UI thread, matched entries only.
- Settings card: one check box per area (all on except "Connection settings", in columns): "Network and system settings
  (1,245 values)" for one file (values that would be imported), names only for a ZIP; "Connection settings (10 values)"
  last when the parameters have connection values, with the warning chip "Connection settings (HTTPS, HTTP port, login
  method, IEEE 802.1X, IPv6, web interface) can make the devices unreachable.". Notes below, only when they apply: "Host name, IP address and
  device names stay as they are on each device." (parameters in the file), "The file has no passwords. Event recipients
  that need one must get it again." (event recipients), "2 of 6 devices are other models and are left out." / "None of
  the selected devices is an AXIS ...".
- Import (primary, disabled with the reason as tooltip) asks in `ui:MessageWindow`: "Import the configuration into 4
  devices? The settings in the file replace the current ones." (+ " The devices may become unreachable." when Connection
  settings is checked), uploads the file (`ITaskDialogContext.UploadAsync`, progress
  row) and returns `ImportPayload` {fileId, fileName, isZip, sections, files: device id -> ZIP entry ("" = the single file),
  leftOut: device id -> reason}. The host runs the task on every selected device; a device in `leftOut` (or missing from
  `files`) ends Done with warnings "<reason>. Nothing was changed." without a request.

### Import task steps (`oadm.device-config.import`, one task per device)

1. **Read configuration file**: the upload (or its ZIP entry), parsed like in the dialog; detail = the source line. A ZIP
   entry whose name does not carry the device's MAC address fails "The file <entry> is not for this device (<MAC>). Nothing
   was changed."; an upload removed meanwhile "The configuration file is no longer on the server. Import it again. Nothing
   was changed.".
2. **Check compatibility**: status, AXIS OS 12.5+ and the model from a fresh basicdeviceinfo ("The file is for AXIS
   Q6135-LE; this device is AXIS P3265-V. Nothing was changed."), `GET /config/discover` (area keys `<api>.<vN>`); none of
   the chosen areas on the device = "None of the settings in the file exist on this device. Nothing was changed.".
3. **Import <area>** (Import network and system settings, Import event rules, Import schedules, Import MQTT events, Import
   time zone, Import power settings, Import password policy): the area without identity and read-only values
   (`ConfigIdentity`), sent as `PATCH /config/rest/$import?importType=merge` with `{"data":{"<key>":{...}}}` (the export's
   own format, one area per request; 60 s). Skipped "Not in the file" / "This device does not have these settings" /
   "Nothing to import". Device `warnings` -> step Warning with the first 3 (all in the task log); `information` -> the task
   log. An error (HTTP status or `"status":"error"`) fails the step with "<HTTP text>: <device message>", a timeout or lost
   connection with the shared device texts ("Timeout after 60 s", "Unreachable - ..."); the next areas still run and the
   task ends Failed "<area>: <error>" (+N more failed). A changed certificate stops the task (`DeviceMessages.CertificateChanged`):
   every later request would fail the same way.
4. **Import connection settings** (only when chosen, always the last import): only the connection values of `param.v2`
   (collection items with their `groupId`), same request and answers as above.
5. **Verify configuration**: export again; every imported value must be there (collections matched by `groupId` / `id`);
   differences -> Warning "2 settings differ after the import: Network.DNSServer1; ...", all in the task log. A device that
   cannot be read fails the step ("Verify configuration: Unreachable - ...").

Task name: "Import configuration <file name>" (the engine shortens it to 48 characters).

### Device answers to imports

`ImportHardwareWriteTests` on AXIS M3098-LV and AXIS Q3546-LVE (both AXIS OS 12.11.118): a camera's own export imported
back with the real task and VAPIX client passes on both, and both cameras ended exactly as exported at the start.

| Test | Device answer | OADM |
|---|---|---|
| Own data, merge | 200 `{"status":"success"}` | Done |
| `?importType=bogus` | 400 "Invalid option: Option 'importType' has invalid value 'bogus'" | the query parameter is honored; `options` in the body is ignored |
| Unknown property (default, `strict=false`) | 200 success, `information` "Field 'x' has been removed from the request"; the rest is imported | step Done "N values, 1 not on this device"; not a verify difference |
| Unknown property, `strict=true` | 200 success, warning "Failed to import time.v2: ... Additional properties are not allowed" | OADM never sends `strict` |
| Wrong type (`"notabool"` for a boolean, a number for a parameter string) | **200 success** with warning "Failed to import <key>: Trying to import '<key>' with invalid data: ..."; **nothing of the area is imported** | step Failed "The device refused these settings: ... Nothing of them was changed." |
| Parameter the device refuses (`Network.DNSUpdate.Enabled=maybe`) next to a valid one | 200 success, warning "Failed to set property: root.Network.DNSUpdate.Enabled"; the valid one is set | step Warning |
| Parameter with a nonsense string (`Network.DNSUpdate.TTL=abc`) | 200 success, stored as is | not checked (the device does not either) |
| `information` about a restart | never seen; no import restarted a camera (uptime kept counting) | - |
| Full parameter import | 2.6 s (M3098-LV), 8 s (Q3546-LVE); then the web server resets connections for under a second | requests retried on connection errors for 30 s |
| `Time.POSIXTimeZone` / `DST` / `ObtainFromDHCP` in the parameters | set a fixed zone and turn the DHCP time zone off in `time.v2` | left out; the time zone comes from its own area |
| `MediaClipCollection` | the Q3546-LVE refuses its own clip name ("Failed to set property: root.MediaClip.M9.Name") | left out (the clips are not in the file) |
| `Rotation` (also the same value, also with `AutoRotationEnabled=yes` in the request) | turns auto rotation off on the image source and every view area | left out where the file has auto rotation on |
| `power-settings.v2` without `poeAllocationRequest` | Q3546-LVE: "No 'poeAllocationRequest' object provided in import data", area not imported | imported (only `powerStatus` is left out) |

## Configure devices

IP addresses (`Network.IPAddress`, `Network.IPv6.IPAddress`) are never offered or written here
(`DangerousParameters.IsAddress`): Network settings and Assign IP address check that a new address is free and follow the
device to it. The web server's ports and HTTP / HTTPS policy (`System.BoaPort`, `System.AlternateBoaPort`,
`System.BoaGroupPolicy.*`), `System.HTTP2.*`, `System.WebInterfaceDisabled` and `Network.RTSPS.Enabled` are connection
parameters (dangerous, written last). Values of numbers, addresses, names and choices are written trimmed; SNMP community
strings and other names with "pass", "secret" or "community" are masked like passwords in logs and errors.

### Decisions

| Topic | Decision |
|---|---|
| Workflow | ADM's: wizard Settings (tabs Parameters, Action rules, Stream profiles; "Use configuration from: Device / Configuration file" on the right) and Overview, then Apply: one task per selected device. |
| Values | From the source only (a device, default the first selected one, or a file), like ADM. A checked parameter writes that value to every device. Typing a value checks the parameter. |
| Several models | Definitions are read once per model and AXIS OS of the selection (the source's group from its read): shown are the parameters every group has (check box "Show parameters that not every selected device has"), choices every group shares (info icon "Only the choices every selected device has."), ranges narrowed to the overlap. A group that cannot be read is a note; its devices check the settings when they run. |
| Not configurable | Type flags `internal`, `hidden`, `readonly`, `const`, `writeonly`, and write level 7 (the last digit of `securityLevel`: Brand, firmware version, serial number "7707"). AXIS P3265-V 12.11.77: 879 of 1,518 parameters; M3215-LVE 12.11.77: 874. |
| Dangerous parameters | Every writable parameter is offered. `DangerousParameters` names the network identity (addresses, subnet, router, IPv6, host name, DNS, DHCP, device names) and the connection (HTTPS, `Network.HTTP.*`, 802.1X, RTSP port, the IP address filter `Network.Filter.*`): amber icon with the reason, the overview warns, Apply asks ("Apply anyway"), written last in their own step, permission `dangerous` (Operator no). |
| The source device | When the source is one of the selected devices it keeps its own action rules, schedules and stream profiles (the task skips those steps there: "This is the source device", payload `SourceDeviceId`); writing them back would only remove and recreate them. Its parameters are set like on every device. With only the source selected, the two tabs offer nothing ("These are the own settings of 10.0.0.48; they are already on it."). |
| Addresses and names | Values that must differ per device (`Network.IPAddress`, `Network.IPv6.IPAddress`, `Network.HostName`, the Bonjour, UPnP and DNS update names; `DangerousParameters.IsPerDevice`) are not offered when several devices are selected (no note, like ADM), and left out of a file with the note "Addresses and names differ per device: use Network settings or Assign IP address."; for one device they stay, with the warning. |
| Passwords | The device never sends a password back: a checked password parameter needs a value ("Enter the password."), in the dialog and again on the device side, so an empty one never clears it on every device. |
| Dialog safety | The overview is built whenever it opens (Next or its step dot), so Apply sends what was reviewed; Next follows every value change; Enter does not apply; a server error (gRPC status, timeout) shows in the dialog; a model whose read failed is read again with the next source; closing the dialog cancels its reads. |
| Same name on the device | Replace: the new rule is created first (its action, then the rule) and only then the old rules of that name and their actions (when no other rule uses them) are removed, so a rule the device refuses never leaves it without the old one; a refused rule's new action is removed again. A stream profile is updated. A schedule with the same name and recurrence is used again. Applying twice gives the same result. Two checked rules or profiles with one name are refused in the dialog and by the payload check. |
| Files | ADM `.cfg` format 3 (`AdmConfigFile`): read from Axis' hardening template (Generic) and ADM files, written byte for byte like ADM (UTF-8 BOM, CRLF, `<ActivationTimeout />`). The format is known from files only, ADM itself is never decompiled. Passwords are written like ADM. No files ship with OADM. |
| Create configuration file | Its own entry for one device: the same tabs, overview, Save. With an action rule every schedule of the device goes into the file, like ADM. Never a task. |
| Storage | Files only: the server keeps no configuration file. |

### Device API (reads verified on AXIS P3265-V and M3215-LVE, AXIS OS 12.11.77)

| Need | Request |
|---|---|
| Definitions and values | `param.cgi?action=listdefinitions&listformat=xmlschema`: one request (395 KB, 2.4 s) while the answer is below the 1 MB XML limit, else per top-level group (`param.cgi?action=list&group=root` for the names), a too large group by its subgroups. 29 group requests took about 10 s (each pays the login again). |
| Write parameters | `param.cgi` POST `action=update&<name>=<value>...` in one request (encoded with `%20` like the other param.cgi writers, 2 minutes timeout); `OK` or `# Error: Error setting 'root.X' to 'y'!`: the named parameter is skipped and the rest sent again (at most 20 times). Password values are masked in every text. |
| Action rules, configurations | `/vapix/services` action1 `GetActionRules`, `GetActionConfigurations`, `AddActionConfiguration` (attribute values with line breaks and tabs as character references), `AddActionRule` (filters as `wsnt:TopicExpression` / `MessageContent`, `tns1` / `tnsaxis` declared; topics with other prefixes are not supported), `RemoveActionRule`, `RemoveActionConfiguration`. |
| Schedules | event1 `GetScheduledEvents`, `AddScheduledEvent` (the device gives the next `genid` number, which can equal another id of the source: the rule's filter is rewritten in one pass, `ScheduleRefs.Rewrite`). Built-in `com.axis.schedules.*` (not `genid`) are never created. A rule whose own schedule could not be created is not written; the payload must carry every own schedule its rules use. |
| Stream profiles | `streamprofile.cgi` `list` (`maxProfiles` 26 checked first), `create`, `update`. |

### Task steps (`oadm.device-config.configure`, one task per device)

| Step | What happens |
|---|---|
| Check compatibility | fresh API list (`param-cgi`); definitions of the groups written |
| Set parameters | each checked against this device's definition; missing, invalid or refused ones skipped by name (Done with warnings) |
| Create schedules | the rules' own schedules |
| Create action rules | each rule on its own: a refused rule fails the step at the end, the others are written |
| Set stream profiles | create or update |
| Set connection parameters | the dangerous ones, last |
| Verify parameters | read back in requests of at most 2,000 characters of names (passwords not) |

A failing step fails only itself; the task fails at the end with the first failure (+N more); error texts of every step
have password values masked. Task names "Configure 12 parameters", "Configure 1 action rule", "Configure 12 parameters,
1 rule, 3 profiles" (at most 48 characters).

### Queries

`read` (definitions with values, rules with their actions, schedules, stream profiles; problems per part) and
`definitions` (definitions only, cached on the server 10 minutes per model and AXIS OS). Create configuration file uses
`read`.

### Configure devices on hardware

`ConfigureHardwareWriteTests` (`Category=HardwareWrite`, only with `OADM_CONFIGURE_WRITE=1` and `OADM_DEV_CAMERAS`; the
first dev camera) passed on AXIS P3265-V, AXIS OS 12.11.77: the real task set `Image.I0.Appearance.Compression` 30 -> 31,
created an own schedule (the device gave it `com.axis.schedules.genid.id-1` and the rule's filter points to it), a
turned-off action rule with a recording action, and a stream profile; every step Done, Verify matched. A second run
reported "1 already there" (schedule), "1 replaced" (rule, its old action removed with it) and "1 replaced" (profile), and
added nothing. The cleanup removed rule, action, schedule and profile and set the parameter back; everything was exactly
as before. 15 s for both runs and the cleanup.

## Code

- `plugins/Oadm.Plugins.DeviceConfig` (four task plugins, no core plugin): `ExportConfigTask` (dialog only, query
  `export`), `ImportConfigTask`, `ConfigApi` (export, discover, import, error texts), `ConfigCompare` (verify);
  `Configure/`: `ConfigureDevicesTask` + `CreateConfigFileTask` (dialog only), `ParamDefinitionsReader`, `ActionEngineApi`
  (the shared `plugins/Shared/ActionEngine` SOAP engine of the Action Rule Editor, mapped to the ADM file models),
  `StreamProfileApi` + `ParamWriter`, `DeviceSettingsReader`, `ConfigMatchCheck`.
- `Shared/` (also used by the client part): `ConfigFile` (parse, areas, `ConfigIdentity`, `ConfigFileNames`),
  `ImportPayload`, `DeviceConfigIds` (ids, texts, limits, `ModelNames`, `FirmwareVersions`), `ConfigureModels` (ids,
  payload, query answer, rules, schedules, profiles, `ScheduleRefs`), `ParamRules` (checks, hints, `DangerousParameters`),
  `AdmConfigFile`.
- `plugins/Oadm.Plugins.DeviceConfig.Client`: `DeviceConfigDialogs` (`ExportConfigDialog`, `ImportConfigDialog`),
  `ExportConfigViewModel` / `ExportConfigWindow`, `ImportConfigViewModel` (+ `ZipPlan`) / `ImportConfigWindow`;
  `Configure/`: `ConfigureDialogs` (`ConfigureDevicesDialog`, `CreateConfigFileDialog`, `WindowConfigureUi`: the
  single-device picker `DevicePickerWindow.ShowOneAsync` over `ITaskDialogContext.AllDevices`, file and save pickers),
  `ConfigureDevicesViewModel` + `CreateConfigFileViewModel`, `SettingsEditorViewModel` (+ `DefinitionMerge`, row view
  models) with `SettingsEditorView`, the two windows.

## Compliance check "Matches configuration file"

`Configure/ConfigMatchCheck` (type `oadm.device-config.matches-file`, group Configuration; see [Compliance](../../docs/spec/compliance.md)): the
user adds a `.cfg` (Create configuration file or ADM, at most 1 MB) to the check; it is kept in the check's settings and
parsed once per content. Per device and run: the file's parameters through `param.cgi action=list` (in chunks like the
verify step), the action rules over SOAP (only when the file has rules; schedules compared by name, topics without the
`//.` suffix) and the stream profiles (only when the file has profiles). A rule or profile of the file's name must exist
with the file's settings; extra rules and profiles on the device are fine. Never compared: addresses and per-device names
(`DangerousParameters.IsAddress` / `IsPerDevice`) and secret names (pass, secret, community). Parameters the device does
not have are skipped ("3 not on this model"). Failed: "2 settings differ" and up to 20 differences
("ImageSource.I0.CaptureFrequency: 50Hz (file: 60Hz)", "Action rule X: missing"). Read only. Tests
`ConfigMatchCheckTests` (the fake camera the recorded ADM file was made from).

## Permissions

| Key | Covers | Operator default |
|---|---|---|
| `task:oadm.device-config.export` | Export configuration | yes |
| `task:oadm.device-config.import:import` | every import | yes |
| `task:oadm.device-config.import:connection` | the "Connection settings" area (not offered without it) | no |
| `task:oadm.device-config.configure:configure` | Configure devices | yes |
| `task:oadm.device-config.configure:dangerous` | the warned parameters (not offered without it; full device control) | no |
| `task:oadm.device-config.create` | Create configuration file (the file can hold passwords; Viewer no) | yes |

## Device groups

The tasks run only on devices of the user's view and touch only their own device
([device groups](../../docs/spec/device-scope.md)). The source device of Configure devices and Create configuration file
is read with the user's query, so only a device of the user's view can be the source; the dialog picks it from the
client's device list, which holds only those. Configuration files stay on the client (an import uploads its file for its
own run): the server keeps no shared file with saved passwords. Tests: server `DeviceGroups/CanaryTests`.

## Tests

`tests/Oadm.Plugins.DeviceConfig.Tests`. Not in fake mode.

- Fixtures `export-all.json`, `discover.json` recorded read-only from an AXIS P3265-V.
- File format, identity filter, file names and MAC notations, models, firmware check, payload, discover, device answers and
  error texts, verify comparison.
- Import task against a stateful fake camera (`ConfigCamera`): every area as merge without identity, chosen areas, ZIP
  entry, other model / old firmware / feature off with no write, missing areas skipped, a refused area while the others
  run, device warnings and differing values, devices left out, broken or missing file, connection settings only when
  chosen and last, a timeout failing one area, a changed certificate stopping, verify unreadable, ZIP entry of another
  device, upload removed meanwhile.
- Export query and window: one file, failed single export writes nothing, ZIP with summary, cancel, Perf 5,000 devices.
- Import dialog: one file with other models, confirmation, connection settings off by default and warned about, broken
  files, ZIP matching (every status), Perf 5,000 files.
- Headless screenshots `device-config-export.png`, `device-config-import-other-model.png`, `device-config-import-zip.png`.
- Configure devices (fixtures in `Fixtures/configure/`, read-only from 10.0.0.48, plus two ADM files): definitions (types,
  ranges, choices, write level), value checks and dangerous parameters, an ADM file read and written back byte for byte,
  a Generic template, rules / configurations / schedules from the device answers, the read query (one request; per group
  when larger than the limit), the task against `ConfigureCamera` (parameters set and skipped, the schedule used again, the
  rule replaced, profiles created and updated, running twice changes nothing, a new schedule's id in the rule, dangerous
  parameters last and their right, a refused part failing only its step, the profile limit), the payload; the dialogs
  against the real queries (merge of models, one read per model, checks below the input, overview and payload, dangerous
  confirmation and the hidden ones without the right, an ADM file with unknown parameters, a non-ADM file, Create
  configuration file); Perf 5,000 devices of 20 AXIS OS versions (20 reads); headless screenshots
  `device-config-configure.png`, `-configure-rules.png`, `-configure-overview.png`, `device-config-create-file.png`;
  read-only `ConfigureHardwareTests` (every dev camera answers the read query).
- Opt-in `ImportHardwareWriteTests` (`Category=HardwareWrite`, `OADM_CONFIG_WRITE_URL` / `_USER` / `_PASSWORD`) and
  `ConfigureHardwareWriteTests`: see the device answers above.

## Open points

- For one device, an address parameter is not probed before writing like Network settings does, and OADM does not follow
  the device to the new address (the periodic re-find does, by serial).
- The definitions cache of the dialog is per model and AXIS OS; dynamic groups (view areas, apps) can differ between
  devices of one model. The task reads each device's own definitions, so only the dialog can show a parameter a device
  lacks.
- The element name of an ADM rule condition (`ConditionList` children): only files with an empty list were seen; OADM
  reads any child with a `TopicFilter` and writes `Condition`.
- AXIS OS 12.5-12.9 with the feature flag off: which status `$export` answers (404 assumed).
- Importing into another camera of the same model (the two test cameras are different models).
