# Device configuration export and import

Status: implemented (plugin `oadm.device-config`), write-tested on two loan cameras (see "Write tests on hardware"). Research of 2026-10-10 on 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77), read-only.

## Goal

Two context menu entries under **Maintenance**, like ADM's configuration handling:

- **Export configuration**: save the configuration of the selected devices to files.
- **Import configuration**: apply one configuration file to the selected devices.

Users expect "the same as the camera's web interface" (Maintenance > Export / Import), so OADM uses the same device API and
the same file format. Like the web interface, it does not cover every setting (see "What is in a file").

## Device API (AXIS OS 12.5+)

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
- Import body: `{"data":{...}}`; options as query parameters (OpenAPI) or `options` in the body (developer page):
  - `importType`: `merge` (keep values not in the file) or `default` (reset the API, missing values become defaults).
  - `strict` (bool): whether properties the device does not know are allowed.
- Import answer: `{"status":"success","warnings":[...],"information":[...]}`; errors 400 (schema), 413 (too large),
  414, 401; error body `{"status":"error","error":{"code":n,"message":"..."}}`.
- **No dry run.** The device validates the import itself; OADM shows its answer.
- History (Axis release notes): beta in 12.4 behind a feature flag, export and import in 12.5, schedules added in 12.8, on by
  default from 12.10. 10.0.0.48 on 12.11 has no feature flag for it and answers `$export` at once.
- Each API decides for itself: `export_import` on the API and per property in `model.json`. Secrets (`secret: true`) are
  never exported but may be imported.

### What 10.0.0.48 exports (12.11.77)

7 of the 38 configuration APIs export, 1,315 values, 58 KB; two exports a minute apart were identical.

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
  `Network.VolatileHostName.*`.
- Read-only or runtime: `Network.DHCP.VendorClass` (model and firmware), `SNMP.EngineBoots`, `SNMP.InitialUserPasswdSet`, `power-settings` `powerStatus` (`poeAllocationRequest` is writable and required, see the write tests);
  `power-settings` `powerStatus.powerReceived` and `poeAllocationRequest` (get only in the model).
- Derived from the MAC address: the multicast addresses `Network.RTPCollection.R<n>.VideoAddress` / `AudioAddress`.

Some values decide whether OADM can still reach and log in to the device (**connection settings**, user decision
2026-10-11): `HTTPS.Enabled`, `HTTPS.Port`, `System.BoaPort` (HTTP port), `System.BoaGroupPolicy.*` (HTTP / HTTPS per user
group), `System.WebInterfaceDisabled`, `Network.HTTP.AuthenticationPolicy`, `Network.IPv6.Enabled`,
`Network.InterfaceCollection.I<n>.dot1x.Enabled` (paths as in the 10.0.0.48 export, `ConfigIdentity.ConnectionPaths`).

Importing one file into many devices as it is would give every device the same host name and, with `BootProto=none`,
the same IP address.

## Decisions (user, 2026-10-10)

1. Package `oadm.device-config` (+ `.Client`), **always on** (Maintenance, like Upgrade firmware).
2. **Merge only.** `importType=default` resets whole APIs (network included) and can make a device unreachable; not offered.
3. **Network identity is never imported** (the list above); no option to turn this off.
4. **No backup** by OADM. Whoever wants to go back exports first.
5. **Same model only.** A device of another model than the file's `metadata.prodNbr` is refused ("Nothing was changed").
   Other AXIS OS versions are allowed.
6. **The device validates.** OADM only uses `$export` / `$import` and does not check the file against the device's
   OpenAPI schema; it removes the identity values, sends each section and shows the device's answer per step.
7. No write test by Claude; the user tests the import by hand on a spare camera.
8. **Two import modes**: one `.json` file applied to every selected device of its model, or the ZIP of a multi-device
   export where each device gets its own file back (matched by MAC address).
9. **Connection settings are their own area, off by default** (user decision 2026-10-11): "Connection settings" is listed
   after the file's areas (unchecked) with a warning; unchecked, its values are left out of "Network and system settings"
   and of the verify like identity values; checked, they are written last in their own step and the confirmation warns.

## Export configuration

- Context menu Maintenance > **Export configuration** (`oadm.device-config.export`, icon `export`), a dialog-only task
  plugin like PKI "View certificates": never a task. `CanRun`: status Ok and AXIS OS 12.5 or later (cached firmware
  version); reason "Needs AXIS OS 12.5 or later (this device has ...)".
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

## Import configuration

### Dialog (`ImportConfigWindow`)

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

### Task steps (one task per device, `oadm.device-config.import`)

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

Task name: "Import configuration <file name>" (the engine shortens to 48 characters).

## Server and client parts

- `plugins/Oadm.Plugins.DeviceConfig` (two task plugins, no core plugin, `alwaysOn`): `ExportConfigTask` (dialog only,
  query `export`), `ImportConfigTask`, `ConfigApi` (export, discover, import, error texts), `ConfigCompare` (verify);
  `Shared/` (also used by the client part): `ConfigFile` (parse, sections, `ConfigIdentity`, `ConfigFileNames`),
  `ImportPayload`, `DeviceConfigIds` (ids, texts, limits, `ModelNames`, `FirmwareVersions`).
- `plugins/Oadm.Plugins.DeviceConfig.Client`: `ExportConfigDialog` + `ExportConfigViewModel` / `ExportConfigWindow`,
  `ImportConfigDialog` + `ImportConfigViewModel` (+ `ZipPlan`) / `ImportConfigWindow`.
- Not in fake mode.

## Tests (`tests/Oadm.Plugins.DeviceConfig.Tests`)

- Fixtures `export-all.json`, `discover.json` recorded read-only from 10.0.0.48.
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
- Opt-in `ImportHardwareWriteTests` (`Category=HardwareWrite`): a camera's own export imported back, no change expected.

## Write tests on hardware (2026-10-10)

AXIS M3098-LV and AXIS Q3546-LVE, both AXIS OS 12.11.118, loan cameras, approved by the user. Both cameras ended exactly as
they were exported at the start. `ImportHardwareWriteTests` (opt-in, `OADM_CONFIG_WRITE_URL` / `_USER` / `_PASSWORD`)
imports a camera's own export with the real task and VAPIX client and passes on both.

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

## Open points

- 12.5-12.9 with the feature flag off: which status `$export` answers (404 assumed).
- Importing into another camera of the same model (both loan cameras are different models).
