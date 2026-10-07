# Oadm.Plugins.Firmware

Task plugin `oadm.firmware`, **Upgrade firmware** (toolbar and context menu group Maintenance, dialog first).
Task names (`GetTaskName`, from the payload): "Upgrade firmware to 12.11.77", "Downgrade firmware to
10.12.236" (every installing device downgrades, payload `direction` from the dialog preview), "Install
firmware 12.11.77" (mixed), "Install firmware" (version not in the file name), plus " (factory default)"
for a soft or hard factory default.
Installs an AXIS OS image on the selected devices through the VAPIX firmware management API
(`fwmgr`), waits for the restart, verifies the new version and commits it.

| Part | Project | Assembly |
|---|---|---|
| Server task + read-only query | `plugins/Oadm.Plugins.Firmware` | `Oadm.Plugins.Firmware.Server.dll` |
| Avalonia dialog | `plugins/Oadm.Plugins.Firmware.Client` | `Oadm.Plugins.Firmware.Client.dll` |
| Tests | `tests/Oadm.Plugins.Firmware.Tests` | |

Both builds deploy to `artifacts/plugins/oadm.firmware/` (with `plugin.json`). The decision logic
(`Shared/`: version compare, file inspection, compatibility, payload) is compiled into both
assemblies so the dialog preview and the server enforcement can never disagree.

## VAPIX firmware management (fwmgr)

`POST /axis-cgi/firmwaremanagement.cgi`, JSON, administrator only. API id in `apidiscovery` is
`fwmgr`; AXIS OS 12.11 lists **fwmgr 1.10**. The public reference documents the 1.x method set
without a per-minor changelog; everything this plugin uses (`status`, `upgrade`, `commit`) is part
of 1.0, so the plugin requires `fwmgr` >= 1.0 within major 1 and sends `"apiVersion":"1.0"`
(accepted by 1.10, verified on the camera: a 2.0 request answers error 417).

| Method | Used | Notes |
|---|---|---|
| `status` | yes (query + pre-check + commit check) | `activeFirmwareVersion`, `activeFirmwarePart`, `inactiveFirmwareVersion` (rollback image), `isCommited` (sic), `pendingCommit`, `timeToRollback`, `lastUpgradeAt`, `resetSource`. Only fields that apply are returned |
| `upgrade` | yes | multipart/form-data: part `json` (`application/json`) then part `file` (`application/octet-stream`). Params `factoryDefaultMode` none/soft/hard, `autoCommit` never/boot/started/default, `autoRollback` never/`<minutes>`/default. Purges the inactive image when it starts, answers `firmwareVersion`, then reboots |
| `commit` | yes | stops the auto-rollback timer, idempotent |
| `rollback` | no | reverts firmware **and** settings of the most recent upgrade (not ACAPs); 404 when no image |
| `purge` | no | removes the rollback image; needs committed firmware |
| `factoryDefault` | no | soft keeps IP/boot protocol/time/802.1X, hard resets all |
| `stopAuto` | no | stops the rollback timer without committing |
| `reboot` | no | reboots with the active image, never triggers a rollback |
| `getSupportedVersions` | no | `["1.10"]` on the camera |

Errors: `{"error":{"code":N,"message":...}}`. 400 bad request, 405 unknown method, 417 API version,
423 busy, 500 internal; upgrade: 409 downgrade without factory default, 410 revoked version,
412 previous upgrade not committed, 415 invalid image, 421 image does not match the device,
422 missing mandatory signature, 424 unknown custom firmware certificate. All upgrade errors are
returned before installation, so the device still runs its old firmware (the plugin says so).

### AXIS OS background

- Tracks: active (only the latest release supported), LTS every two years supported ~5 years
  (2022 LTS 10.12, 2024 LTS 11.11, 2026 LTS 12.11), product-specific support for old products
  (9.80, 8.40, 6.50). AXIS OS 13 is the active track since autumn 2026; the recommended path to 13
  goes through 12 LTS. AXIS OS 13 refuses unsigned ACAPs and rolls back by itself when an upgrade
  finds unsigned applications installed.
- Images are signed by Axis; the device verifies the signature before it installs (422/424).
- Downgrade restriction: since AXIS OS 11.6 / 10.12 LTS (CVE-2023-21414) ARTPEC-8 products only
  accept a downgrade to the latest supported 10.12 LTS release; anything older is refused by the
  device. Downgrades also require a factory default (409).
- The signed image format is not publicly documented, and real images start with very different
  bytes (older AXIS OS images such as `M3206-LVE_10_12_338.bin`, AXIS OS 10.12, start with gzip/tar-like
  bytes). OADM therefore **never judges the content** (no magic-byte or header checks; an earlier version
  wrongly refused such images as "compressed archive"). It only makes non-destructive checks of the file:
  `.bin` extension, size between 1 MB and 2 GB, and product and version read from the official download
  name when it follows the pattern (`P3265-V_12_11_77.bin`, dots also accepted, optional `AXIS_` prefix).
  The device is the final authority for product and signature (421/422/415) and refuses before
  installing; the task checks product and version against the device before the upload.

## Decision table

Evaluated per device in the dialog (preview) and again on the server right before the upload
(`FirmwareCompatibility.Evaluate`), against a fresh `basicdeviceinfo` (ProdNbr, Version).

| File / device | Factory default | Allow downgrade | Result |
|---|---|---|---|
| Not a `.bin` file, smaller than 1 MB or larger than 2 GB (the content is never inspected) | any | any | **Refused**, nothing changed |
| Product from file name != device ProdNbr | any | any | **Refused** "wrong product" |
| Same version | any | any | **Done with warning** "Already up to date", nothing uploaded |
| Newer version | none | any | **Upgrade**, settings kept, OADM commits after verifying |
| Newer version | soft / hard | any | **Upgrade + factory default**, device commits itself |
| Older version (same or older major line) | any | off | **Refused** "downgrade not allowed" |
| Older version | none | on | **Refused** "requires a factory default" (the device would answer 409) |
| Older version | soft / hard | on | **Downgrade + factory default**, device commits itself |
| Version or product unknown (other file name, device version unknown) | none | any | **Upload**, the device validates the image |
| Version unknown | soft / hard | off | **Refused**: a downgrade cannot be ruled out |
| Version unknown | soft / hard | on | **Upload**, device validates |

Refused devices fail with a message ending in "Nothing was changed."; other selected devices are
not affected.

Additional gates, all before the first write:

1. Payload valid (file id, known mode); never logged or persisted by the host.
2. `device.Status == Ok` (CanRun and again in ExecuteAsync). Unknown, Unreachable,
   CredentialsRequired, PasswordNotSet and CertificateChanged are never upgraded.
3. `CanRun`: cached `device.Apis` supports `fwmgr` 1.x. `ExecuteAsync`: fresh
   `GetApiListAsync()` + `Require("fwmgr", "1.0")` -> `DeviceNotCompatibleException`.
4. The uploaded file exists on the server (`IUploadedFiles.FindAsync`).
5. fwmgr `status` shows no uncommitted previous upgrade (`isCommited:false`, `pendingCommit`,
   `timeToRollback`); otherwise the device would answer 412.

## Execution

Every device request and every wait is a named task step (`FirmwareTaskPlugin.Steps`, all planned up
front, so the user sees them as Pending); the task progress is derived from the steps.

| Step | Details |
|---|---|
| Check compatibility | status OK, fresh `getApiList` + `Require("fwmgr", "1.0")`; detail "fwmgr 1.10" |
| Read device info | `basicdeviceinfo`; detail "AXIS P3265-V, AXIS OS 11.11.160" |
| Validate file | uploaded file present, name/size inspection (never the content), decision table; already up to date -> Warning and all later steps Skipped ("Already up to date.") |
| Read firmware status | fwmgr `status`: no uncommitted previous upgrade |
| Upload firmware | `ctx.Files.OpenReadAsync` streamed in 80 KB chunks (`FirmwareStreamContent`), Content-Length set, never buffered; reopened if the HTTP stack resends after a Digest challenge; `Expect: 100-continue` so the 401 comes before the body. Step progress in bytes ("12 of 80 MB"). Request timeout 20 min via `VapixRequestOptions.Timeout`. Params: settings kept: `autoCommit=never`, `autoRollback=30` (minutes, longer than the 15 min wait) so the device reverts by itself if OADM never verifies. Factory default: `autoCommit=started`, `autoRollback=never`, because the reset removes OADM's credentials |
| Install firmware | poll anonymous `basicdeviceinfo getAllUnrestrictedProperties` every 5 s (works after a factory default) until the device goes offline (or already answers with another version) |
| Wait for device to come back | same probe until it answers again. Both waits share one timeout of **15 min** |
| Verify version | version after restart == `firmwareVersion` from the upgrade answer (or the file name). Old version again = the device rolled back -> Failed |
| Read commit state / Commit firmware | settings kept only: `status`, then `commit` unless already committed (Commit Skipped); 3 attempts, "Wait before retrying the commit" (10 s) between them, retries named "... (attempt 2)". Factory default: both Skipped ("The device commits a factory default upgrade by itself.") and Done with warning (set a password / check the address) |

Failure messages always say what the device runs now and what happens next: upload refused or
interrupted ("still runs X"), did not restart, did not come back (with the auto-rollback time or
the hard-reset IP hint), rolled back, not committed ("rolls back to X by itself 30 minutes after
booting unless committed"). If the upload answer is lost after the whole body was sent (the device
may already reboot), the task continues and decides by the version after the restart.

Bounded concurrency across devices is the task engine's job.

## Query

`ITaskPluginQuery` method `status` (read-only): fresh API list; when fwmgr 1.x is offered,
fwmgr `status` + `basicdeviceinfo`, returned as `FirmwareStatusInfo` JSON
(`supported`, `fwmgrVersion`, `activeVersion`, `activePart`, `inactiveVersion`, `isCommitted`,
`pendingCommit`, `timeToRollback`, `lastUpgradeAt`, `resetSource`, `model`, `architecture`).
Unsupported devices return `{"supported":false,...}` without calling fwmgr.

## Dialog

Choose a local `.bin`; shows name, size and, when recognized, product and AXIS OS version. Table
of the selected devices (address, model, firmware, result pill with tooltip, details, rollback
state from the status query). Options: factory default none (default) / soft / hard with warnings,
"Allow downgrade". **Upgrade** uploads the file with `ITaskDialogContext.UploadAsync` (progress bar,
cancellable) and returns `{"fileId","fileName","factoryDefaultMode","allowDowngrade"}`. It is
enabled only when at least one device would be upgraded.

## Recorded on hardware (read-only)

AXIS P3265-V, AXIS OS 12.11.77 (10.0.0.48), fixtures in `tests/Oadm.Plugins.Firmware.Tests/Fixtures`:

- `apidiscovery`: `fwmgr 1.10` "Firmware Management" official.
- `getSupportedVersions`: `["1.10"]`.
- `status`: `activeFirmwareVersion 12.11.77`, `activeFirmwarePart 7454651131`, `resetSource crash`;
  no `inactiveFirmwareVersion` / `isCommited`, i.e. no rollback image on this camera.
- Anonymous `getAllUnrestrictedProperties` returns `Version` and `ProdNbr`.
- Unknown method -> `405`, apiVersion 2.0 -> `417`; anonymous fwmgr -> HTTP 401.

Never run against hardware: upload, commit, any factory default. Open points to verify on a test
device: numeric `autoRollback` as a JSON string (`"30"`), the exact answer timing of `upgrade`
(before or after flashing), anonymous `basicdeviceinfo` right after a factory default.

## SDK gaps

- **Request timeout.** A 100-250 MB upload plus device-side verification takes minutes; the plugin
  sets the SDK option `VapixRequestOptions.Timeout` (20 min) on the upload request. A timeout after
  the full body was sent is treated as "answer lost" and verified by version.
- `ITaskDialogContext.UploadAsync` does not document the unit of `IProgress<double>`; the dialog
  accepts 0..1 or 0..100.
- The engine has no per-plugin concurrency hint: firmware uploads of 8 devices at once share one
  link; a plugin-declared limit (e.g. 2-4 parallel uploads) would help.
- No way for a plugin to mark the device for a full refresh with a new status after a factory
  default (the device becomes PasswordNotSet); the post-task full refresh covers it partially.
