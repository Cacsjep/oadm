# Edge storage (core plugin)

Id `oadm.edge-storage`, rail page **Edge storage** (group Maintenance, icon `hardDrive`), **off by default**, switchable.
The SD card of the chosen cameras in one table, and the tools of the camera's web interface for many cameras at once: check,
repair, format, encryption on or off, retention. SD cards only (network shares are not listed or changed).

## Decisions

| Topic | Decision |
|---|---|
| Scope | SD cards (`SD_DISK`). Network shares, the storage limit and the wear / temperature alert levels are not changed. The internal `EMMC` disk of newer cameras is ignored. |
| Table | Read only on request and only for the cameras of the page's device choice (`PageDeviceChoice`: the Devices page selection, else the last choice on this computer; nothing before a choice): when the page opens, after a new choice and on Refresh (no background polling). One row per device that lists the API `disk-management` (an SD card slot), read with the shared `DeviceSweep` (24 at a time for the plugin, the sweeps of every device group together; 6 s per device). |
| Reads per camera | `disks/list.cgi?diskid=all`, `disks/gethealth.cgi`, `disks/properties/getdiskalertlevels.cgi` (the last two optional: a failure leaves the wear empty). Certificate changed, Credentials required, Password not set and Unreachable devices are not contacted. |
| Row states (the page's count cards) | OK; Worn (wear at or past the camera's wear alert level, default 90 %); Problems (failed, not in use, read-only, locked, encrypted and cannot be opened, the shared `EdgeDisk.ProblemText`); No SD card; Not read (with the reason). In the code: `CardStates.Ok`, `Warning`, `Problem`, `NoCard`, `Error`. |
| Tools | Started only from the page, on the selected rows that have a card; one task per camera (`oadm.edge-storage.task`, `ShowInMenus` false), progress in the tasks pane below the page. The table reads the camera again when its task ends. |
| Rights | "show" (Operator, Viewer); "Change storage settings" (`manage`: check, retention; Operator yes); "Format and encrypt storage" (`format`: format, repair, encryption on / off; deletes recordings, full device control, Operator no). The page hides the tools the role lacks; the server checks the method's right, and an action sent through the other method is refused. Both methods are audited. |
| Confirmation | Check: "Recording stops until the check is finished." Format and Turn off encryption: "Every recording ... is deleted." (red confirm "Format"). Repair: "Damaged recordings can be lost." Encrypt: a dialog with the passphrase twice and a red "Encrypt and format". Format is the only red toolbar button, last after a separator. |
| Passphrase | 1 to 512 bytes, no control characters (checked on the page, in the plugin method and in the task). Kept in memory only: the payload of the run and the POST body to the camera; never stored, logged or in a URL (`TaskPayload.ToString` leaves it out). |
| Retention | 0 to 7,000 days; 0 = no age limit ("No age limit, keep them until the card is full"). The task skips a camera that has the value already. |
| Cancel | Before the card is unmounted the task stops; from the unmount on it finishes its steps (a card is never left unmounted, the camera finishes its job anyway). |
| Failures | The card is mounted again after any failure, also when the unmount answer was lost (the mount step reads the card first); the first failure stays the task's error, a failed mount after it is logged. Up to 5 lost job status answers in a row are asked again. Encrypt on an encrypted card: "The SD card is encrypted already. Nothing was changed." |
| Device groups | SDK 0.4.0 (`minSdkVersion` 0.4.0). One table per device scope (`DeviceSweeps<StorageRow>`): the users of a device group share the table of their group's cameras, `rows` and `state` reach only them (`PluginEventAudience.DeviceScope`). `read` with ids from a user limited to a device group and the runs (`change`, `format`) refuse the whole request when any camera is outside the caller's devices (NOT_FOUND "1 device is no longer available.", like an unknown id; nothing read, no task); a user with all devices gets the cameras that still exist (`CallDevices.KnownAsync`: removed ones are skipped); `read` without ids = the caller's cameras; runs start through the caller's task runner. After a task the camera is read again in every table that has its row. A deleted group has no table. `read` and `getState` answer only rows of cameras the caller sees now (`DeviceSweep.SnapshotAsync`): a camera that left the group (tag or site change, before the group's rows are pruned) drops out of the table at once and its removal goes out with the next batch; reading a camera again after a task removes the row of a camera no longer in the scope. When the caller's cameras cannot be listed the state comes without rows. |
| Page | The read's end comes as an event; the page also asks for the state every second while a read runs (an event sent before the watch connected is lost). |

## Task steps

| Action | Steps |
|---|---|
| Check | Check compatibility, Read the SD card, Unmount the SD card, Check the SD card, Mount the SD card |
| Repair | ... Unmount the SD card, Repair the SD card, Mount the SD card |
| Format | ... Unmount the SD card, Format the SD card, Mount the SD card |
| Encrypt | ... Unmount the SD card, Turn on encryption, Format the SD card, Mount the SD card |
| Turn off encryption | ... Unmount the SD card, Turn off encryption, Format the SD card, Mount the SD card |
| Retention | Check compatibility, Read the SD card, Set the retention (read again to verify; Skipped "Already 7 days.") |

Task names: "Check SD card", "Repair SD card", "Format SD card", "Encrypt SD card", "Turn off SD card encryption", "Set SD
card retention to 30 days", "Remove SD card retention limit". A failed step fails the task; "Mount the SD card" still runs
when the card was unmounted. Without a card: "No SD card. Nothing was changed."; turning off encryption on a card that is
not encrypted: "The SD card is not encrypted. Nothing was changed." Before the first write the task reads a fresh API list
and requires `disk-management` 1.x (and `disk-properties` 1.x for retention and encryption).

## Device API (VAPIX Edge storage)

Verified on an AXIS Q1615 Mk III (AXIS OS 12.11.118, 64 GB card, loan camera) on 2026-10-13, read-only on an AXIS P3265-V
(12.11.77) and an AXIS M3215-LVE (12.11.77, no card):

- `disks/list.cgi?diskid=all`: sizes in kB of 1,024 bytes (57,922,892 kB = 59.3 GB), `cleanupmaxage`, `status` (OK =
  mounted, connected = present but unmounted (size 0), disconnected = empty slot), `encryptionenabled`, `diskencrypted`.
- `disks/gethealth.cgi`: `wear` in percent; -1 not supported (P3265-V), -3 not present. `EMMC` is listed with
  `overallhealth` on newer cameras.
- `format.cgi`, `checkdisk.cgi`, `repair.cgi` answer **HTTP 403 while the card is mounted**. Unmounted, each starts a job
  (`<job action result="OK" jobid/>`); `job.cgi?jobid&diskid` gives `progress` (0 until done, then 100) and `result`.
  Format took 15 to 21 s, check and repair a few seconds on the empty 64 GB card; format leaves the card unmounted. An
  unknown job answers `result="ERROR" description="no such job"`.
- `mount.cgi action=mount|unmount` is a job too (done within 3 s).
- `properties/enablediskencryption.cgi` (passphrase) and `disablediskencryption.cgi` need the card unmounted and take
  effect with the next format (`encryptionenabled` changes at once, `diskencrypted` after the format). Check works on an
  encrypted card.
- `properties/setcleanupmaxage.cgi maxage=0..7000` (0 accepted: no age limit); 7001 is `GeneralError` 170 "Invalid max
  age.". The disk properties CGIs and mount accept their arguments as a POST form; every write goes as POST.
- Errors: HTTP 403 also for a login without administrator rights ("administrator rights are required, or the SD card is in
  use"); disk properties errors are `GeneralError` codes (100 no encryption, 110 passphrase, 140 already encrypted, 150 not
  mounted, 160 mounted, 170 max age) with plain texts in `StorageTexts`.

Research answers: `tests/Oadm.Plugins.EdgeStorage.Tests/Fixtures/`.

## Shared code

- `Disks/EdgeDisks.cs` (`EdgeDisk`, `EdgeDisks.Parse`, `FirstProblem`): the disk list for every plugin; Edge Recording
  Export and Hardening scan link it as source.
- `plugins/Shared/DeviceSweep.cs` (server) and `plugins/Shared/SweepRows.cs` (page): the on-request table, shared with the
  Image Health Dashboard (one sweep at a time, ordered events, a late read never overwrites a newer row, a failed sweep is
  logged). `DeviceMessages.Refusal` (SDK) and `PluginEventWatch.RunAsync` (client SDK) are used by Edge storage and Image
  Health; older plugins still have private copies.

## Tests

`tests/Oadm.Plugins.EdgeStorage.Tests`: `DeviceGroupTests` (the plugin canary, one table per group, runs through the
caller), `DeviceSweepsTests` (the shared sweep per scope), parsing of the recorded answers, every task against `SdCamera` (a stateful fake
that answers like the Q1615 Mk III, 403 while mounted), the plugin's table and rights, the page view model against the
plugin in process, the dialogs, headless screenshots (`edge-storage-*.png`), Perf (5,000 cameras read, searched and
selected). Opt-in `StorageHardwareWriteTests` (`OADM_STORAGE_WRITE_URL` / `_USER` / `_PASSWORD`; **formats the card**):
every tool on one camera, the card ends as it started; passed on the Q1615 Mk III (1 min 34 s).

## Compliance check "SD card"

`SdCardCheck` (type `oadm.edge-storage.sd-card`, group Storage; see `docs/spec/compliance.md`): the device has an SD card
that works (`EdgeDisk.ProblemText` is none), optionally at least N GB and / or N % free. A device without a card fails
"No SD card": the user chose the devices the check is for. Read only: one `disks/list.cgi` per device and run (shared
key `oadm.edge-storage:disks`). Available while Edge storage is on (off by default). Tests `SdCardCheckTests` (recorded
disk lists).
