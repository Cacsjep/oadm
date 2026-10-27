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
internal; upgrade: 409 downgrade without factory default, 410 revoked version, 412 previous upgrade not committed, 415
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
11.11, 12.11), not their latest versions (no online lookup), and names what to install first:

- the device runs an LTS track and the target leaves it: the latest version of that track;
- every LTS track between the device's version and the target: its latest version.

Examples (`UpgradePathTests`): 11.8.61 -> 12.6.85 "the latest AXIS OS 11.11, then 12.6.85"; 9.80.49 -> 11.11.169 "the
latest AXIS OS 9.80, then the latest 10.12, then 11.11.169"; no advice within a track, from an active version to the LTS
of its major line, with a factory default or for downgrades. `FirmwareCheck.PathAdvice` carries it: the dialog shows the
row as "Not recommended" (warning, still installed) with the path in Details and one line below the list; the task logs
it as a warning and, when the device refuses the upload (nothing installed), ends the error with it.

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
`basicdeviceinfo`, returned as `FirmwareStatusInfo` JSON (`supported`, `fwmgrVersion`, `activeVersion`, `activePart`,
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
