# Upgrade firmware (`oadm.firmware`)

Task plugin with a dialog (`.Client`), always on: **Upgrade firmware** in the toolbar and the context menu group
Maintenance. Installs AXIS OS images on the selected devices, one file per model, waits for the restart, verifies the new
version and commits it. Common task rules: [Tasks](../../docs/spec/tasks.md).

| Part | Project | Assembly |
|---|---|---|
| Server task + read-only query | `plugins/Oadm.Plugins.Firmware` | `Oadm.Plugins.Firmware.Server.dll` |
| Avalonia dialog | `plugins/Oadm.Plugins.Firmware.Client` | `Oadm.Plugins.Firmware.Client.dll` |

The decision logic (`Shared/`: version compare, file checks, compatibility, upgrade path, payload) is compiled into both
assemblies, so the dialog preview and the server always agree.

Task names (`FirmwarePayload.TaskName`): "Upgrade firmware to 12.11.77"; "Downgrade firmware to 10.12.236" (every
installing device downgrades, payload `direction` from the dialog preview); "Install firmware 12.11.77" (mixed);
"Install firmware" (no version in the file name); several files of different versions: "Upgrade firmware" /
"Downgrade firmware". A soft or hard factory default adds " (factory default)".

## Device API

`POST /axis-cgi/firmwaremanagement.cgi`, JSON, administrator only. API id `fwmgr`; AXIS OS 12.11 lists **fwmgr 1.10**.
Everything the plugin uses (`status`, `upgrade`, `commit`) is in 1.0, so it requires `fwmgr` 1.x (>= 1.0) and sends
`"apiVersion":"1.0"` (1.10 accepts it; 2.0 answers error 417).

| Method | Used | Notes |
|---|---|---|
| `status` | query, pre-check, commit check | `activeFirmwareVersion`, `activeFirmwarePart`, `inactiveFirmwareVersion` (rollback image), `isCommited` (sic), `pendingCommit`, `timeToRollback`, `lastUpgradeAt`, `resetSource`. Only fields that apply are returned |
| `upgrade` | yes | multipart/form-data: part `json` (`application/json`) then part `file` (`application/octet-stream`). Params `factoryDefaultMode` none/soft/hard, `autoCommit` never/boot/started/default, `autoRollback` never/`<minutes>`/default. Purges the inactive image when it starts, answers `firmwareVersion`, then reboots |
| `commit` | yes | stops the auto-rollback timer, idempotent |
| `rollback` | no | reverts firmware **and** settings of the most recent upgrade (not ACAPs); 404 when no image |
| `purge` | no | removes the rollback image; needs committed firmware |
| `factoryDefault` | no | soft keeps IP, boot protocol, time, IEEE 802.1X; hard resets all |
| `stopAuto` | no | stops the rollback timer without committing |
| `reboot` | no | reboots with the active image, never triggers a rollback |
| `getSupportedVersions` | no | `["1.10"]` on the camera |

Errors: `{"error":{"code":N,"message":...}}`. 400 bad request, 405 unknown method, 417 API version, 423 busy, 500
internal; upgrade: 402 an installed application does not support the new version (AXIS OS 13), 409 downgrade without factory default, 410 revoked version, 412 previous upgrade not committed, 415
invalid image, 421 image does not match the device, 422 missing mandatory signature, 424 unknown custom firmware
certificate. All upgrade errors come before the installation, so the device still runs its old firmware (the plugin says
so).

### AXIS OS background

- Tracks: active (only the latest release supported), LTS every two years supported about 5 years (2022 LTS 10.12, 2024
  LTS 11.11, 2026 LTS 12.11), product-specific support for old products (9.80, 8.40, 6.50). AXIS OS 13 is the active
  track; the recommended path to 13 goes through 12 LTS. AXIS OS 13 refuses unsigned ACAPs and rolls back by itself when
  an upgrade finds unsigned applications installed.
- Images are signed by Axis; the device verifies the signature before it installs (422/424).
- Downgrade restriction (AXIS OS 11.6 / 10.12 LTS and later, CVE-2023-21414): ARTPEC-8 products only accept a downgrade
  to the latest supported 10.12 LTS release. Downgrades also require a factory default (409).
- The signed image format is not public, and real images start with very different bytes (`M3206-LVE_10_12_338.bin`
  starts with gzip/tar-like bytes). So OADM **never judges the content**: no magic-byte or header checks. It only checks
  the file: `.bin` extension, 1 MB to 2 GB, and product and version from the official download name when it follows the
  pattern (`P3265-V_12_11_77.bin`, dots also accepted, optional `AXIS_` prefix). The device decides on product and
  signature (421/422/415) and refuses before installing; the task compares product and version with the device before
  the upload.

### Recorded on hardware (read-only)

AXIS P3265-V, AXIS OS 12.11.77, fixtures in `tests/Oadm.Plugins.Firmware.Tests/Fixtures`:

- `apidiscovery`: `fwmgr 1.10` "Firmware Management" official; `getSupportedVersions`: `["1.10"]`.
- `status`: `activeFirmwareVersion 12.11.77`, `activeFirmwarePart 7454651131`, `resetSource crash`; no
  `inactiveFirmwareVersion` / `isCommited`, i.e. no rollback image on this camera.
- Anonymous `getAllUnrestrictedProperties` returns `Version` and `ProdNbr`.
- Unknown method -> `405`, apiVersion 2.0 -> `417`; anonymous fwmgr -> HTTP 401.
- AXIS OS 12 answers a commit right after the restart with "The system hasn't finished booting yet." for minutes.

## Decisions

Evaluated per device in the dialog (preview) and again on the server right before the upload
(`FirmwareCompatibility.Evaluate`), against a fresh `basicdeviceinfo` (ProdNbr, Version).

| File / device | Factory default | Allow downgrade | Result |
|---|---|---|---|
| Not a `.bin` file, smaller than 1 MB or larger than 2 GB (the content is never inspected) | any | any | **Refused**, nothing changed |
| Several files, none for the device's product and none without a product in its name | any | any | **Skipped** "No file for this model." (`FirmwareCompatibility.Choose`) |
| Product from file name != device ProdNbr | any | any | **Refused** "The file is for <product>, the device is <model>." |
| Same version | any | any | **Done with warning** "Already up to date", nothing uploaded |
| Newer version | none | any | **Upgrade**, settings kept, OADM commits after verifying |
| Newer version | soft / hard | any | **Upgrade + factory default**, device commits itself |
| Older version (same or older major line) | any | off | **Refused** "Downgrade X → Y is not allowed. Turn on "Allow downgrade to an older AXIS OS version"." |
| Older version | none | on | **Refused** "requires a factory default" (the device would answer 409) |
| Older version | soft / hard | on | **Downgrade + factory default**, device commits itself |
| Version or product unknown (other file name, device version unknown) | none | any | **Upload**, the device validates the image |
| Version unknown | soft / hard | off | **Refused**: a downgrade cannot be ruled out |
| Version unknown | soft / hard | on | **Upload**, device validates |

Several files: each device gets the file for its product, else a file whose product the name does not tell (the device
checks it). Two files for one product are refused in the dialog. Refused devices fail with a message ending in "Nothing
was changed."; the other selected devices are not affected.

### Upgrade path (advice, never refused)

Axis recommends an upgrade path (AXIS OS knowledge base "Upgrade path"), and AXIS OS 12.6.85, 11.11.169 and later refuse
upgrades off it; a factory default lifts it. `Shared/AxisOsUpgradePath` knows the LTS tracks (6.50, 8.40, 9.80, 10.12,
11.11, 12.11), not their latest versions (no online lookup). Only what it knows for certain is a warning:

- `Advice`: the upgrade jumps over an LTS track. It names every LTS track in between (and first the device's own track
  when it is an LTS track), each "the latest". `FirmwareCheck.PathAdvice`: the row shows "Not recommended" (warning, still
  installed) with the path in Details and one line below the list; the task logs it as a warning.
- `Note`: the upgrade leaves the device's LTS track for the next one (12.11.147 -> 13.0.88). It is on the path only from the
  latest version of that track, which OADM cannot tell. `FirmwareCheck.PathNote`: the row stays "Upgrade", Details adds
  "Major upgrade. Make sure the device runs the latest AXIS OS 12.11 first."; the task logs it as info.

Examples (`UpgradePathTests`): 11.8.61 -> 12.6.85 "the latest AXIS OS 11.11, then 12.6.85"; 9.80.49 -> 11.11.169 "the
latest AXIS OS 9.80, then the latest 10.12, then 11.11.169"; 11.11.109 -> 12.6.85 a note; nothing within a track, from an
active version to the LTS of its major line, with a factory default or for downgrades. When the device refuses the upload
(nothing installed), the task error ends with the advice or the note.

### Major versions with breaking changes

`FirmwareDialogViewModel.BreakingChangesGuides` maps a major version to the Axis guide of its breaking changes (13:
axis.com "AXIS OS 13 breaking changes"). When a device that installs moves up to such a major, the dialog shows one line
below the list, whatever the number of devices: "AXIS OS 13 has breaking changes." with the link "Read the Axis guide before
upgrading" (opened in the browser). Models without AXIS OS 13 get no file.

### Installed applications (AXIS OS 13 and later)

From AXIS OS 13 the device reinstalls every application during the upgrade and refuses it (fwmgr 402, nothing installed)
when one is unsigned or does not declare the new version (manifest `compatibleOsVersions`, mandatory from 13; old
manifests and `package.conf` declare none). `Shared/InstalledAppRules` applies the same rule before the upload, from
`applications/list.cgi` (`InstalledAppsReader`, read-only, the call of the Applications plugin):

- an application blocks when it is not bundled and its `SignatureStatus` is not `Signed` ("not signed"), it lists no
  `CompatibleOsVersions` ("declares no AXIS OS versions") or none of its ranges holds the target ("supports AXIS OS 12.10 to
  12"); range bounds compare at their own precision (max `13` = any 13.x);
- a field is judged only when the device reports it for some application (older AXIS OS lists neither); bundled
  applications come with AXIS OS and are replaced by the upgrade;
- verdict `AppsBlock` ("Apps block it", error, no task): "12.11.147 → 13.0.88. Face Verifier: not signed. Update or remove it
  with Applications (ACAP) first.";
- the dialog gets the list with the status query (`FirmwareStatusInfo.Applications`, rows read like the status); the task
  reads it again in Validate file when a file is AXIS OS 13 or later and refuses before the upload ("Nothing was
  changed."). An unreadable list is no refusal: the task logs a warning and the device decides.

Recorded on AXIS P3265-V 12.11.77 (`Fixtures/applications-list-p3265v-12.11.xml`): applications installed while unsigned
ones were allowed report `SignatureStatus="Unknown"`; Axis applications `Signed`.

### Gates before the first write

1. Payload valid (file ids, known mode); never logged or persisted by the host.
2. `device.Status == Ok` (CanRun and again in ExecuteAsync). Unknown, Unreachable, Credentials required, Password not set
   and Certificate changed are never upgraded.
3. `CanRun`: cached `device.Apis` supports `fwmgr` 1.x. `ExecuteAsync`: fresh `GetApiListAsync()` +
   `Require("fwmgr", "1.0")` -> `DeviceNotCompatibleException`.
4. The uploaded files exist on the server (`IUploadedFiles.FindAsync`).
5. fwmgr `status` shows no uncommitted previous upgrade (`isCommited:false`, `pendingCommit`, `timeToRollback`);
   otherwise the device would answer 412.

## Dialog

Choose one local `.bin` or several (one per model); the dialog shows name, size and, when recognized, product and AXIS OS
version (several: "3 files" and each product with its version). A device without a file for its model shows "No file"
("No file for this model."). Two files for one product are refused at the file row ("Two files are for P3265-V. Choose
one file per model.").

Table of the selected devices: Address, Model, Firmware, Result (with tooltip), Details. The Firmware column is refreshed
by the status query: read for the first 50 devices when the dialog opens, for other rows when the grid shows them, at
most 4 queries at a time; a row shows the chip "Reading" until its query answered.

Options: Factory default none (default) / soft / hard with warnings, "Allow downgrade". **Upgrade** is enabled when at
least one device would install. The dialog narrows the devices (`ShowForDevicesAsync`): tasks start only for the devices
that install a file, none for devices already up to date, without a file for their model or refused (Wrong product,
Downgrade blocked). It uploads the files with `ITaskDialogContext.UploadAsync` (only those a device installs, one after
the other, one progress bar, cancellable) and returns `{"fileId","fileName","factoryDefaultMode","allowDowngrade"}`,
with several files also `"files":[{"fileId","fileName"}]`.

## Firmware online (spec, not built)

Researched with the maintainer 2026-10-29, parked: a built version (sign-in through the user's MyAxis session cookie) was
dropped as too fragile. What stays is how to find the firmware of a model on the Axis download site.

- **Where.** The public listings `https://www.axis.com/ftp/pub_soft/MPQT/` (cameras) and `.../PACS/` (audio, access
  control, intercoms, I/O, switches; a product in both is taken from MPQT): one folder per product, in it one folder per
  version (`12_11_147/`) and `latest/` (the newest .bin as `<product>.bin` plus `ver.txt` with its version). Each listing
  is an HTML table, one row per entry: icon, name, date, size ("120.7M") and SHA-256. Version folders are two to five
  numbers (`10_12_356_2`); folders with a suffix (`10_12_212_TTA`, special builds) are skipped.
- **Model match.** The device's model matches a product folder only exactly after normalizing both: lower case, only
  a-z and 0-9, without "AXIS " (`P1455-LE` never takes `P1455-LE-3` or `P3265-LVE_9_mm`). Not every model is listed
  (P12-MkIII is not).
- **Versions.** Per model the newest version of each major (`OS12`, `OS11`, ...). The model's newest major is its active
  track (its newest version); an older major counts only with its last minor (10.11 next to 10.12 is dropped), marked LTS
  only when that minor is one (`AxisOsUpgradePath.LtsTracks`: 6.50, 8.40, 9.80, 10.12, 11.11, 12.11). A model whose
  newest major ends on an LTS minor has no active track (C1410 on 11.11). A version only in `latest/` (named by
  `ver.txt`, P1455-LE 11.11.212) counts with the `latest/` file. Latest is what `ver.txt` names, else the version with
  the .bin of `latest/`, else the highest. Versions without a .bin, a SHA-256 or a size of at least 1 MB are left out.
- **Lookup.** Only the models of the selected devices, never the whole site: both root listings, then per model its
  folder, `latest/`, `ver.txt` and the newest folder of each track; at most 8 models at a time, no cache (the site
  changes during the day). Each page 0.1 to 0.15 s; measured 2026-10-29: 5 models 1 s, 50 models 3 s, 100 models 5 s.
- **Downloads need a login.** Every .bin below `/ftp/pub_soft/` answers `302` to `/my-axis/login`; the listings and
  `ver.txt` are public. A download by OADM would need the user's MyAxis session (cookie `axis_connect_session_sid`, not
  a public Axis API), which is why the built version was dropped.

## Task steps

Every device request and every wait is a named step (`FirmwareTaskPlugin.Steps`, all planned up front and shown as
Pending).

| Step | What happens |
|---|---|
| Check compatibility | status OK, fresh `getApiList` + `Require("fwmgr", "1.0")`; detail "fwmgr 1.10" |
| Read device info | `basicdeviceinfo`; detail "AXIS P3265-V, AXIS OS 11.11.160" |
| Validate file | uploaded files present, the file for the device (`Choose`), name and size checks (never the content), decision table. No file for this model or already up to date: Warning and all later steps Skipped |
| Read firmware status | fwmgr `status`: no uncommitted previous upgrade |
| Upload firmware | `ctx.Files.OpenReadAsync` streamed in 80 KB chunks (`FirmwareStreamContent`), Content-Length set, never buffered; reopened if the HTTP stack resends after a Digest challenge; `Expect: 100-continue` so the 401 comes before the body. Progress in bytes ("12 of 80 MB"). Request timeout 20 min (`VapixRequestOptions.Timeout`). Settings kept: `autoCommit=never`, `autoRollback=30` (minutes, longer than the 15 min wait), so the device reverts by itself if OADM never verifies. Factory default: `autoCommit=started`, `autoRollback=never`, because the reset removes OADM's credentials |
| Install firmware | anonymous `basicdeviceinfo getAllUnrestrictedProperties` every 5 s (works after a factory default) until the device goes offline (or already answers with another version) |
| Wait for the device to come back | same probe until it answers again. Both waits share one timeout of **15 min** |
| Verify version | version after restart == `firmwareVersion` from the upgrade answer (or the file name). Old version again = the device rolled back: Failed |
| Read commit state / Commit firmware | settings kept only: `status`, then `commit` unless already committed (Commit Skipped); 3 attempts with "Wait before retrying the commit" (10 s) between them, retries named "... (attempt 2)". While the device answers "The system hasn't finished booting yet." Commit firmware asks again every 10 s for up to **5 min** (`BootWait`), not counted as an attempt. Factory default: both Skipped ("The device commits a factory default upgrade by itself.") and Done with warning (set a password, check the address) |

Failure messages say what the device runs now and what happens next: upload refused or interrupted ("still runs X"),
did not restart, did not come back (with the auto-rollback time or the hard-reset address hint), rolled back, not
committed ("rolls back to X by itself 30 minutes after booting unless the firmware is committed"). If the upload answer
is lost after the whole body was sent (the device may already reboot), the task goes on and decides by the version after
the restart.

`MaxParallelDevices = 4`: the engine uses the smaller of 4 and `Tasks.MaxParallelPerPlugin` (default 16). Each upgrade
streams a 100-250 MB image from the server; more parallel uploads share the server's uplink, get slower and run into the
20 min upload timeout.

SDK gap: a plugin cannot mark the device for a full refresh with a new status after a factory default (the device becomes
Password not set); the full refresh after the task covers it partly.

## Query

`ITaskPluginQuery` method `status` (read-only): fresh API list; when fwmgr 1.x is offered, fwmgr `status` +
`applications/list.cgi` (null when not readable) + `basicdeviceinfo`, returned as `FirmwareStatusInfo` JSON (`supported`, `fwmgrVersion`, `activeVersion`, `activePart`,
`inactiveVersion`, `isCommitted`, `pendingCommit`, `timeToRollback`, `lastUpgradeAt`, `resetSource`, `model`,
`architecture`). Unsupported devices return `{"supported":false,...}` without calling fwmgr.

## Permissions

Keys `task:oadm.firmware:upgrade` (every run), `:factoryDefault` (a soft or hard factory default) and `:downgrade`
("Allow downgrade"). The dialog offers the factory default choice and "Allow downgrade" only with their permission.
Operator default: upgrade only.

## Device groups

The tasks run only on devices of the user's view and touch only their own device; the query `status` reads only the
queried device; uploads are read by their id for the run ([device groups](../../docs/spec/device-scope.md)). Tests:
server `DeviceGroups/CanaryTests`.

## Tests

`tests/Oadm.Plugins.Firmware.Tests`: decision table, file checks, upgrade path, the fwmgr client from recorded fixtures,
the task with a fake device (steps, rollback, commit retries, the boot wait on a fake clock, several files), the dialog
view model (files per model, a device without a file, two files for one model) and scale tests.

Never run against hardware: upload, commit, any factory default. Still to verify on a test device: numeric
`autoRollback` as a JSON string (`"30"`), the exact answer timing of `upgrade` (before or after flashing), anonymous
`basicdeviceinfo` right after a factory default.
