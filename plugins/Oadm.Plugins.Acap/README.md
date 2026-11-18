# Applications (ACAP) (`oadm.acap`)

Task plugin with a dialog (`.Client`), always on: **Applications (ACAP)** in the context menu group Applications, no
toolbar entry. Spec: [Bundled plugins](../../docs/spec/bundled-plugins.md#applications-acap).

The dialog lists the applications on the first selected device (device picker for several devices). The user starts,
stops or removes one, installs / upgrades an `.eap` package (or several builds per architecture and AXIS OS, also as a ZIP) or installs license keys. The
Applications page (`oadm.app-management`, [spec](../../docs/spec/app-management.md)) also starts the action Change
settings; the dialog does not offer it. Each action runs on all selected
devices, one task per device. The package preview reads at most 25 devices (first the first device of every model and AXIS OS version, 4 queries at
a time); the other rows say "Checked at install", and the task checks every device before it writes.
The package facts in the card header show Package, Vendor, Architecture, AXIS OS (when the package names a range) and
Manifest: the `schemaVersion` of `manifest.json` ("1.3", "2.1"), or `package.conf` for a package without `manifest.json`
(`AcapDialogViewModel.ManifestOf`).

| Part | Assembly | Contents |
|---|---|---|
| `plugins/Oadm.Plugins.Acap` | `Oadm.Plugins.Acap.Server` | `AcapTaskPlugin` (`ITaskPlugin` + `ITaskPluginQuery`), `ApplicationApiClient` (VAPIX), `Shared/` (`.eap` reader, `EapPackageFiles` (a pick with ZIPs as `.eap` paths), `EapZip` (the rules of a ZIP, client and server), `AcapBuilds.cs` (`AcapBuildChooser`: the build per device group; `AcapPackageRules`: the mapping file), compatibility rules and the per-device install preview `AcapCompatibility.Preview`, license key files and `LicenseKeyMatcher` (the key per device and application), payload and query models, all also linked into the Applications page `oadm.app-management`, `ApplicationSettings.cs` = `AppSettings` settings definitions parser and checks, `ApplicationLog.cs` = `AppLogReader`) |
| `plugins/Oadm.Plugins.Acap.Client` | `Oadm.Plugins.Acap.Client` | `AcapTaskDialog` (`ITaskPluginDialog`), `AcapWindow` + `AcapDialogViewModel` |

The client part references the server assembly only for the shared models (same plugin folder) and never talks to
devices.

## Decisions

| Topic | Decision |
|---|---|
| API used | Classic Application API (`application 1.x`). `packagemanager` is undocumented and is not used. |
| `CanRun` | Status Ok/Unknown **and** cached `device.Apis.Supports("application", "1.0")`. Empty API list = not offered. VAPIX identifies the Application API by `Properties.EmbeddedDevelopment.Version`, not by the API list: AXIS OS 11.x devices with applications may not list it, so `VapixClient.GetApiListAsync` adds "application" 1.0 (status "from parameters") when that property exists (seen on an AXIS OS 11.9 camera). |
| Before any write | Fresh `GetApiListAsync().Require("application", "1.0")`; payload validated first (no device access on bad payload). |
| Package source | Uploaded by the dialog (`ITaskDialogContext.UploadAsync`), read in the task from `ctx.Files`; SHA-256 and app name must match what the dialog checked. |
| ZIP of packages | `EapZip` holds the rules of a ZIP for client and server: only the `.eap` entries (also in subfolders) and one `oadm-packages.json` (at the top level or next to the `.eap` files; one anywhere else is refused, never ignored); refused: a name with a path outside the ZIP, two `.eap` of one name, a password, more than 10,000 entries, 16 `.eap` or 2 GB unpacked (declared sizes first, then counted while reading), an entry that unpacks to more than 100 times its packed size. Client (`EapPackageFiles`): unpacks the builds into a private temp folder of the dialog (file names on disk made safe, the names in the ZIP kept for the mapping), then the path of loose files runs unchanged; the dialog never uploads the ZIP. Deleted on a new pick, a refused read, Cancel and when the window closes; a read or an upload holds it (`Hold`); leftovers older than a day go at the next client start (`SweepOnce`). Server (agents, the Assistant: `zipFileId`): the plan and each task read the uploaded ZIP as a stream (`EapZip.ReadAsync`: manifests, SHA-256 per build, the mapping file) and stream the chosen entry to the device (`OpenBuildAsync`); nothing is written to disk. Spec: `docs/spec/app-management.md` "A ZIP of packages". |
| Package parsing | `manifest.json` wins, `package.conf` fills gaps; top-level entries only; metadata max 1 MB; app name `^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$`; version required. Not an `.eap` -> Failed, nothing changed. |
| Architecture | Package arch (`architecture` / `APPTYPE`) must equal basicdeviceinfo `Architecture` (`arm64`=`aarch64`, `armhf`=`armv7hf`). Missing / `all` / `noarch` = independent. Device arch unknown + native package -> refuse. |
| AXIS OS | A package with `compatibleOsVersions`: AXIS OS must be inside one range (bounds inclusive at their own precision: max `12` = any 12.x), the range alone decides (schema 2.1.0 with 11.11.0 - 12 runs on 11.11.238). Without a range: at least the schema's minimum OS; newer unknown schema -> highest known minimum. Newer unknown schema always gives a warning. Unknown AXIS OS version with requirements -> refuse. |
| SDK version | `AcapCompatibility.CheckSdk`. Required = manifest `embeddedSdkVersion` (package.conf `REQEMBDEVVERSION` for a package without `manifest.json`); absent -> no check. Below 3 it is compared by its numbers with `Properties.EmbeddedDevelopment.Version` (2.9 < 2.18): lower -> refuse "The application needs a newer ACAP version (2.20) than this device has (2.18). Upgrade AXIS OS first.", not reported -> warning. 3 and later is another scale: the ACAP 3 and Native SDKs write "3.0" while AXIS OS 12.11 reports 2.18 (8.40: 2.16) and runs them, so no check against a 2.x device; a device that reports 3 or later below the package -> warning only (no recorded device reports 3.x, so a refusal would be a guess). |
| SDK type | `applications/info.cgi` `supportedSdks` (read-only, `ApplicationApiClient.TryGetSupportedSdksAsync`; no answer, an error or no entry -> unknown, no check). A package with `manifest.json` is from the ACAP 3 SDK or the ACAP Native SDK and does not say which: it needs `acap3` or `acap4-native`, else refuse "This device does not support ACAP 3 or ACAP 4 applications.". A `package.conf`-only package: no check. Nothing in a package signals `acap4-cv` (computer vision apps ship as containers, not as `.eap`), so it is never checked. |
| Manifest | A package without `manifest.json` (only `package.conf`) on AXIS OS 13+ -> refuse "The package has no manifest.json, which AXIS OS 13 and later need." (`AcapCompatibility.ManifestMissing`; AXIS OS 13.0 no longer supports `package.conf`). |
| Root apps | Package user `root`: refuse on AXIS OS >= 12.0; warning (AllowRoot needed) on 11.8-11.x. |
| Signing | Signature cannot be checked locally. `AllowUnsigned=false`, or AXIS OS 13+ (the setting is gone, signed only) -> warning in the dialog; device error 2 becomes "The package signature is missing or invalid; the device accepts only signed applications.". |
| Install kind | Not installed -> Install; lower -> Upgrade; equal -> Reinstall (allowed); higher -> Downgrade, refused unless the dialog's "Allow downgrade" is set. Vendor change -> warning (device refuses with error 27). |
| Upload | Streams the file from `ctx.Files` as multipart `file`; step progress in bytes. `upload.cgi` answers only after the installation, so the upload waits up to 10 minutes (`ApplicationApiClient.UploadTimeout`). |
| Lost upload answer | Timeout / connection error while waiting for `upload.cgi` -> poll `list.cgi` up to 5 min for the expected version; found -> Done with warning, else Failed. |
| Verify | After install: listed with the expected version, else Failed. Optional start: control start, then status must be Running, else Done with warning. |
| Device errors | Every code VAPIX lists is translated with what to do (`UploadErrors` incl. developer mode 51-54, `ControlErrors`, `LicenseErrors` 21-31); unknown codes "The device refused it. (error N)". Code 15 (outcome unknown) is handled like a lost answer: the result is read from `list.cgi`. Code 2 says "the device accepts only signed applications" only when the device accepts only signed ones, else "The package signature is not valid. Download the package again. (error 2)". A failed upgrade that removed the application (VAPIX: both versions are removed) says so. HTTP 401 / 403 / other statuses use the shared `DeviceMessages` texts. A name that differs only in upper and lower case from an installed application is refused before the upload (code 14). A license key for other application versions (code 26) is refused before sending. A start that does not reach Running within 15 s names a missing or invalid license. |
| Control timeout | `control.cgi` (start, stop, remove) waits up to 2 minutes (`ApplicationApiClient.ControlTimeout`): a remove was seen to take about 60 s. A remove whose answer is lost is verified by polling `list.cgi` for up to 5 minutes (Done with warnings when the application is gone). |
| Remove | Confirmation in the dialog. Bundled (`Bundled=Yes`) apps are refused. Not installed -> Done with warning. Verified gone afterwards. |
| Start / Stop | Already in the requested state -> Done, no write. Errors 6/7 treated as success. Not installed -> Done with warning (multi-device selections). Status verified afterwards. |
| Query | `listApplications` (read-only): API check, basicdeviceinfo, `EmbeddedDevelopment.Version`, `info.cgi`, `config.cgi get AllowUnsigned`, `list.cgi`. Returns `{device: facts, applications: [...]}`. |
| Start view | `ITaskDialogContext.StartView` (SDK 0.7.0, from a page's `RunTaskAsync(..., startView, ct)`): "license" opens the license card at once ("Select the application in the list.", then "Choose the license keys of the application."; the first selected application that needs a license becomes the card's), "license:<package name>" also selects that application; only with the license permission; other names: the usual start. Used by **Install licenses** of the Applications page. |
| Own page | `list.cgi` `ConfigurationPage` ("local/ax_msf/redirect.html") -> `InstalledApplication.ConfigurationPage`, `ConfigurationPath` ("/" + it; null when it would leave the device). The Applications page opens it in Web View. |
| Payload | `{action: install|remove|start|stop|installLicense|settings, application, fileId, sha256, files, zipFileId, rules, unmatched, mapping, version, allowDowngrade, startAfterInstall, licenseKeys, values}`; kept in memory only. |
| Several packages | `files: [{fileId, sha256, architecture}]` (at most 16: three architectures times three AXIS OS tracks fit; `fileId`/`sha256` = the first, an older payload with only `fileId` still works). One `appName`, no two files of one name; several builds per architecture, other versions and a package for any architecture are fine (`AcapCompatibility.CheckPackageSet`, refused at the dialog's file row before any upload, and again by the task). |
| Build per device group | `AcapBuildChooser.Choose(builds, group, rules, mapping, unmatched, device)`, one pure function for the dialog's preview, the plan and the task's run. Group = model, AXIS OS, architecture (`AcapDeviceGroup`; SoC and short name for the mapping file). Order: the matching rows of the mapping file, also for a single package (most specific wins: models, then SoC, then architecture and AXIS OS; a tie of different files is unclear); no row matches and `unmatched` is `skip`: "No package" ("No row of the mapping file matches this device."); then automatic (a single package without a row is always used, its own checks decide): architecture fits (`arm64`=`aarch64`) and AXIS OS inside `compatibleOsVersions` (without a range: the schema's minimum); newest version first, then the range inside the others'; any architecture only when no native build fits. Same version and range, ranges not inside each other, or a build without a range next to another: unclear. The payload's `mapping` decides only an unclear group, never one the rows or the automatic pick decided or left without a package. A chosen build that does not fit the architecture or AXIS OS: "No package" with the reason. With the device's own read (dialog, Applications page, task; `Choose(..., device)`) every candidate build must also pass the SDK rules (rows, automatic pick and the person's choice): another build is taken, none -> "No package: <the refusal>", a chosen one -> "x.eap does not fit: <reason>.". The plan works from cached data without SDK facts: the task checks them. |
| Dialog | The preview names the file each device gets ("lpr_2.6_aarch64.eap") or "No package for AXIS OS 9.80.3.". A table above it (Devices, Package, Decided by) shows only when a group is unclear (a list of its candidates) or the ZIP brings a mapping file (every group, read only, with the row that decided); groups from the cached facts of all selected devices, the read ones from the read, O(n). Install stays disabled until every unclear group has a file ("Choose the package for P3265-V, AXIS OS 12.11.77, aarch64."). The choices go into `mapping` (file ids), the mapping file's rows into `rules` (also with one file) and its `"unmatched": "skip"` into `unmatched`. |
| Task | Reads every file (or the ZIP), then picks again from a fresh read (Read SDK version, then basicdeviceinfo `ProdNbr`, `ProdShortName`, `Soc`, `Version`, `Architecture`): no build fits: Read device info Warning "No package for armv7hf. Nothing was changed.", the rest Skipped; unclear without a choice, or a chosen file that does not fit: Failed "... Nothing was changed.". A file whose architecture differs from the one the dialog declared fails. |
| Change settings | `{action: settings, application, values: {name: value}}`, names below `root.<Name>` (`AppSettings.IsValidName`, at most 200, values at most 4,096 characters). Definitions from `param.cgi?action=listdefinitions&listformat=xmlschema&group=root.<Name>` (`AppSettings.Parse`: `int` with `min`/`max`/`rangeEntry`, `enum`, `bool` with its `true`/`false` texts, `password`, `string` with `maxlen`; `hidden`, `readonly`, `const`, `internal` cannot be changed; "# Error" = no settings). Values normalized (numbers, the device's yes/no text, list values as spelled); only changed values (and passwords, always) are sent, in one `param.cgi?action=update` POST body. Verify reads the definitions again; passwords are never read back. A refused update names the setting from the device's error line, never the value. |
| Settings are secret | Values are never in a log, step text, task name, exception or the audit log (names only), like a password. |
| AI agents | `AcapMcpPayload` (SDK 0.13.0 `PayloadType`): `{action: install|remove|start|stop|installLicense|settings, application, files, zipFileId, rules, mapping, allowDowngrade, startAfterInstall, values, passwords}`, read by the task as an `AcapPayload`; no license key files. `ValidatePayload` answers the settings problems that need no device at `values` or `values.<name>` (`AppSettings.OfflineProblems`: at least one, at most 200, valid names, the text rules of the dialog, a name that points to a secret ("Mqtt.Password is a password. List it in passwords: the user enters it.")). The installed applications and their settings are not in the cached data, so the plan runs every device that can run the task and warns that each device checks the values first (`SettingsCheckedOnDeviceWarning`); Read settings then fails "Nothing was changed" for a value that does not fit. Passwords: only the Applications page sets `passwordsAllowed` (a person typed them); without it a setting the application declares as a password is refused before the write, except the names in `passwords`: the agent lists them without a value, `SecretsOf` makes a slot of each (`password_<i>` at `passwords[i].value`, "password for <name>", at most 8; docs/spec/mcp.md "Secrets"), the user enters the value, it is checked with the text rules when entered and on the device before the write, and the plan says "Sets <names> with the values the user enters." Permission `:settings`. License key: `installLicense` takes one key file the user enters (slot `license_key`, label "license key", `[Secret]` `licenseKey`, at most 4,096 bytes), parsed like a chosen file ("This is not a license key file.") and added to `licenseKeys`; a key fits one device, so a plan of several devices skips them all ("A license key fits one device. Choose one device."). Permission `:license`. Install takes `files` (several builds, optional `rules` with file ids, checked like the rows of the mapping file, and `unmatched`) or one uploaded ZIP `zipFileId` (its `oadm-packages.json` applies; `rules` and `unmatched` refused then). The plan (`PlanAsync` with the uploads) reads the builds and answers per device group: a line in `details`, "No package" as a skip reason, an unclear group as an error at `mapping` naming the group and the candidate files, a broken ZIP or file at `zipFileId` / `files`. `mapping: [{axisOs, architecture, file, model?}]` decides the unclear groups and no other (`file` = a file id, or the `.eap` name in the ZIP). |
| Log | `ApplicationApiClient.ReadLogAsync`: `GET /axis-cgi/admin/systemlog.cgi?appname=<Name>` read as a stream, the newest 10,000 lines and 2 MB kept (`AppLogReader`, read in blocks: a line is cut to 8 KB while it is read, the read stops after 64 M characters, Truncated); header and blank lines skipped. For the Applications page; the task does not use it. |
| License keys | Key files only (`.key`, `.xml`, or a ZIP of them; `LicenseKeySet`): several at once, at most 10,000 keys, 8 KB each, a ZIP at most 64 MB unpacked (entries read with a bound, never by their declared size); the same device and application twice keeps the latest end date. Matched by `DeviceID` = the device's MAC address; the dialog shows per device Key found, No key, Key expired, Other application (Other version on the Applications page, which knows the installed version). Of several keys of a device for the application `LicenseKeyMatcher` takes the best: not expired, fitting the installed version when known, the latest end date first; `Match` and `PayloadKeys` use the same choice. Optional with an install (installed right after Verify installation, before the start) and on its own (toolbar **Install license** for the selected application, or the start view below). The payload carries one key per selected device (memory only, like every payload). |
| License column | `list.cgi` `License`: "Valid until <date>" / "Valid", "Not required" (`None`: the application needs no license, e.g. AXIS Object Analytics; **Install license** is disabled for it), "Missing", "Invalid", other values as sent (`InstalledApplication.LicenseText`). |
| License checks | Before anything is sent: the key parses, names this device's MAC address, the installed application's `ApplicationID` (when the device lists one) and has not expired; a device without a key is Done with warnings, nothing sent. Install license on its own: any other problem fails "Nothing was changed". With an install: a key problem or a refused key is a warning (the application is installed). Verify license: `list.cgi` License = Valid, else a warning with the device's value. |

## Mapping file for vendors

`oadm-packages.json` in a ZIP, at its top level or next to the `.eap` files (name in any case); anywhere else the ZIP is
refused ("app.zip has oadm-packages.json in the folder docs, which holds no .eap file. Put it at the top level or next to
the .eap files."), never ignored. OADM's own format, Axis has none. `AcapPackageRules.Parse`: strict JSON (no comments, no
trailing commas), at most 64 KB and 500 rows; a key that starts with `$` or `x-` (at the top level and in a row) is a note
and ignored; any other unknown key refuses the ZIP, so an older OADM never ignores a rule it does not know. A problem
refuses the ZIP at the file row ("oadm-packages.json row 2 names lpr.eap, which is not among the .eap files.").

```json
{ "unmatched": "skip",
  "packages": [
  { "file": "lpr_2.4_aarch64.eap", "models": ["P3265-LVE"], "soc": ["CV25"], "architecture": "aarch64", "axisOs": { "min": "10.0", "max": "10.12" } }
] }
```

`file` is required and names an `.eap` of the pick; `models` match the model with or without "AXIS " (any case), `soc` the
SoC as the SoC column shows it (`AcapPackageRules.SocKey`: "A8" = "ARTPEC-8" = "Axis Artpec-8"), `axisOs` bounds inclusive
at their own precision (`min` above `max` is refused, "12.5" to "12" is fine). The most specific matching row wins
(models, then SoC, then architecture and AXIS OS); two rows of the same weight naming different files: unclear. Rows
apply to a single package too. `unmatched`: what a device no row matches gets, `"automatic"` (the default: the manifests
decide) or `"skip"` (no package, "No row of the mapping file matches this device.": only the devices a row lists get one).
A choice in the table or an agent's `mapping` decides only an unclear group. A row never overrides the architecture and
AXIS OS checks of the build's manifest. The dialog says "Mapping from the package" in the package facts
and shows every group with its file and the row that decided.

## Device API

Device used (read-only): AXIS P3265-V, AXIS OS 12.11.77, `aarch64`, Artpec-8.

| Source | Finding |
|---|---|
| `apidiscovery getApiList` | `application 1.0` (official, "Application"), `packagemanager 1.4` (no docLink, no status), `fwmgr 1.10`, `basic-device-info 1.3` |
| `applications/list.cgi` (POST, GET also works) | XML `<reply result="ok"><application Name NiceName Vendor Version Bundled Status License LicenseExpirationDate ApplicationID ConfigurationPage SignatureStatus ...><Resources/><CompatibleOsVersions><VersionRange><Min/><Max/>...`. Recorded in `tests/.../Fixtures/list-p3265v-12.11.xml` |
| `applications/config.cgi?action=get&name=AllowUnsigned` | `<reply result="ok"><param name="AllowUnsigned" value="true"/></reply>` (this device: unsigned allowed) |
| `applications/info.cgi` | `<supportedSdks><sdk name="acap3"/><sdk name="acap4-cv"/><sdk name="acap4-native"/></supportedSdks>` (attribute form, the docs show element text; both are read). `Fixtures/info-p3265v-12.11.xml` holds this answer as noted here, not a new recording |
| `param.cgi Properties.EmbeddedDevelopment` | `Version=2.18`, `EmbeddedDevelopment=yes` |
| `basicdeviceinfo` | `Architecture=aarch64`, `Version=12.11.77`, `Soc=Axis Artpec-8` |
| `param.cgi?action=listdefinitions&listformat=xmlschema&group=root.AXISImageHealthAnalytics` | 14 parameters, 13 `hidden`, all `string`, one visible (`ONVIFEvents`). Recorded in `Fixtures/paramdefinitions-imagehealth-p3265v-12.11.xml` (AXIS OS 12.11.118). Other types as in the full list of the Configure devices fixture. |
| `admin/systemlog.cgi?appname=eva` | Header "----- Contents of SYSTEM_LOG for 'eva' -----", blank lines, then `<time> <host> [ LEVEL ] process[pid]: message`. Recorded in `Fixtures/systemlog-eva-p3265v-12.11.txt` |
| `packagemanager.cgi getSupportedVersions` | `1.4` only; the API is not documented publicly (`listPackages` -> "Unknown method") |

Application API (developer.axis.com/vapix/applications/application-api):
- `upload.cgi` POST multipart, field `file`, `application/octet-stream`. Installs or upgrades, then answers `OK` or
  `Error: <code>`. Codes: 1 invalid package,
  2 signature missing/invalid, 3 too large/disk full, 5 incompatible, 10 unspecified, 12 upload
  unavailable, 13 invalid user/group, 14 already exists, 15 timeout, 27 vendor mismatch on
  upgrade, 29 invalid manifest.json/package.conf. Signed packages need about 2x the file size in RAM.
- `control.cgi?action=start|stop|restart|remove&package=<name>`. Codes: 4 not found,
  6 already running, 7 not running, 8 failure state, 9 too many running (gone in 12.6).
- `config.cgi?action=get|set&name=AllowUnsigned|AllowRoot` (AXIS OS 11.2+). `AllowUnsigned`
  default true up to 11.11, false from 12.0; AXIS OS 13 removes it (signed only). `AllowRoot`
  default false from 11.8, not supported from 12.0.
- `license.cgi?action=uploadlicensekey&package=<name>` POST multipart, field `fileData` (the key file), and
  `action=removelicensekey`. Not in the public docs we used; seen in the device's own web page script (AXIS OS 12.11,
  read-only research). The page treats every answer without "Error" as accepted; OADM does the same.
- Automatic activation with a license code is not a device API: the device's web page loads
  `https://www.axis.com/app/acap/sdk.js` in the browser, which asks an Axis gateway for the key (with a token of its own)
  and then uploads it with `uploadlicensekey`. OADM does not call Axis' service; users activate codes on the device's
  Apps page.

License key file (seen in a real key of AXIS Missing Features): XML `<LicenseKey>` with `FormatVersion` 1, `ApplicationID`
(= `list.cgi` `ApplicationID`, = manifest `appId` / package.conf `APPID`), `Minimum/MaximumMajor/MinorVersion` (-1),
`ExpirationDate` (`yyyy-MM-dd`), `DeviceID` (the MAC address), `SignatureKeyID`, `Signature`. The device checks the
signature.

`.eap` package: gzip-compressed tar. `manifest.json` (ACAP 3/4; `schemaVersion`,
`acapPackageConf.setup.{appName, friendlyName, vendor, version, architecture, embeddedSdkVersion,
runMode, user}`, schema 2.x requires `architecture` and `compatibleOsVersions [{min,max}]`) and/or
`package.conf` (legacy ACAP 2, also generated by acap-build: `APPNAME`, `APPNICENAME`, `VENDOR`,
`APPMAJORVERSION/MINOR/MICRO`, `APPTYPE` = architecture, `REQEMBDEVVERSION`, `APPUSR`).
Manifest schema -> minimum AXIS OS (packages without a range only): 1.0-1.2 10.7, 1.3 10.9, 1.3.1 11.0, 1.4.0 11.7, 1.5.0 11.8,
1.6.0 11.9, 1.7.0 11.10, 1.7.1 12.0, 1.7.2 12.1, 1.7.3 12.2, 1.7.4 12.4, 1.7.5 11.11 (ACAP Native SDK 1.15.4, the 11.11 LTS track; not in Axis's table, measured), 1.8.0 12.6, 1.9.0 12.8,
1.10.0 12.10, 1.11.0 12.11, 2.0.0 12.10, 2.1.0/2.2.0 12.11.

## Task steps

Every device request and every wait is a named step (`AcapTaskPlugin.Steps`, planned up front); task progress comes
from the steps. Steps that do not apply end Skipped with the reason. Task names: "Install <app> <version>", "Upgrade
<app> to <version>" (both + " with license"), "Install license of <app>", "Remove <app>", "Start <app>", "Stop <app>",
"Change settings of <app>". Builds of several versions: "Install <app>" without a version.

| Action | Steps |
|---|---|
| Install / upgrade | Check compatibility, Read package, Read SDK version (param.cgi and info.cgi, "2.18; ACAP 3, computer vision, ACAP 4", "Not reported" when neither answers; before Read device info, whose pick needs it), Read device info (basicdeviceinfo), Read unsigned application setting (config.cgi `AllowUnsigned`), Read installed applications, Check package, Upload package (byte progress, then "The device installs the package"), Verify installation (Warning when the upload answer was lost but the version is listed); with "Start after install": Start application, Verify application state (Warning if not running; both Skipped when already running) |
| Install license | Check compatibility, Read installed applications (Failed when not installed), Check license key (Warning without a key: the other two Skipped), Install license key, Verify license (Warning when the device does not show Valid). With an install the last three follow Verify installation. |
| Remove | Check compatibility, Read installed applications (Warning when not installed, Failed for bundled apps), Remove application, Verify removal |
| Start / Stop | Check compatibility, Read installed applications (Warning when not installed), Start application / Stop application, Verify application state (both Skipped when already in the requested state) |
| Change settings | Check compatibility, Read installed applications (Failed when not installed), Read settings (Failed "Nothing was changed" for an unknown name, a setting that cannot be changed, a value outside its type or range, or no settings at all), Change settings (one `param.cgi` update; both Skipped when every value is already set), Verify settings (Failed when the device did not keep a value; passwords counted "not read back") |

## Compliance check "Application running"

`ApplicationRunningCheck` (type `oadm.acap.running`, group Applications; see [Compliance](../../docs/spec/compliance.md)): the chosen application
is installed, by default also running, optionally at least a version (leading numbers, "1.4") and with a valid license
(Valid and not past its expiration date, or None / Custom). The application is chosen from the list of one device ("Read
from device"). Read only: one `applications/list.cgi` per device and run (shared key `oadm.acap:list`). Detail texts:
"AXIS Object Analytics is stopped", "parking is not installed", "AXIS Object Analytics 1.26.205, needs 1.30",
"AXIS License Plate Verifier: license missing".

## Permissions

Keys `task:oadm.acap:install`, `:downgrade` (install with "Allow downgrade"), `:startStop`, `:remove`, `:license`
(install license keys, also with an install) and `:settings` ("Change application settings"). The dialog hides the buttons
and the option the role lacks. Operator default: install, start / stop, license keys and settings.

## Device groups

The tasks run only on devices of the user's view and touch only their own device; the query reads only the queried
device ([device groups](../../docs/spec/device-scope.md)). Tests: server `DeviceGroups/CanaryTests`, TaskService cases.

## Tests

`tests/Oadm.Plugins.Acap.Tests`: package reader and compatibility rules, license keys, the API client from recorded
answers (`Fixtures/`), the settings parser and log reader (`AppSettingsTests`), Change settings (`SettingsTaskTests`: no
value in any text), several builds, the mapping file, the plan and ZIP installs (`PackagesPerArchitectureTests`), the ZIP rules (`EapZipTests`), the task with a fake device, the compliance check (`ApplicationRunningCheckTests`), the dialog
view model, the SDK rules (`SdkChecksTests`), Perf scale tests and a headless screenshot. One opt-in hardware write test, `SettingsHardwareWriteTests`
(`Category=HardwareWrite`, only on request): changes one setting, reads it back and restores it; passed on an AXIS
P3265-V (AXIS OS 12.11.118, Image Health Analytics ONVIFEvents) on 2026-10-28. Installs and removes have no hardware test.
