# System report (`oadm.system-report`)

Core plugin (+ `.Client`) without a page, always on: the toolbar button **System report**, like ADM's "Get system
report". The server downloads the server report of every selected device and the client saves one ZIP for Axis support.
Read-only for devices. Rules: [Bundled plugins](../../docs/spec/bundled-plugins.md#system-report-core-plugin-without-a-page).

## Device API

- `GET /axis-cgi/serverreport.cgi?mode=zip_with_image` (report + snapshot, what Axis support asks for; video devices),
  else `mode=zip`; `zip_with_image` answering 400/404/500/501, or 200 without a ZIP, is retried as `zip`.
- Stored credentials; the account must be an administrator (403 = `DeviceMessages.Forbidden`). Devices with Certificate
  changed, Credentials required or Password not set are refused without a request; unknown ids "1 device is no longer
  available.".
- The answer is streamed to disk (`VapixRequestOptions.StreamResponse`), must start with a ZIP signature, at most 256 MB
  (`MaxReportBytes`) and 5 minutes (`DeviceTimeout`) per device, at most 4 devices at a time (all jobs together).
- Recorded on the development camera (P3265-V, AXIS OS 12.11): `application/zip`, chunked; `zip_with_image` about 0.3 MB
  in 34-37 s (`serverreport_cgi.txt` 1.3 MB unpacked + `serverreport_image.jpg`), `zip` about 0.16 MB in 31 s;
  `Content-Disposition: attachment; filename=Axis_SR_<date>_<time>_<MAC>.zip`.

## Jobs

`SystemReportJobs`, server memory, files in `ICorePluginContext.DataDirectory/jobs/<job id>/` (leftovers deleted at start).

- 1..50 devices per job (`MaxDevices`; each report is up to 256 MB on the server disk).
- One file per device, then `bundle.zip`: every report stored unchanged (no compression) as `<address>_<MAC>_<model
  without "AXIS ">_<yyyyMMdd-HHmmss UTC>.zip` (characters other than letters, digits, '.', '-' become '-', a name used
  twice gets "-2"), plus `summary.txt` (created time, OADM version, counts, one line per report with address, MAC, model,
  size and mode, then the failures with their reason).
- Dropped 30 minutes after the last use (checked every minute and on each call), on `delete` and when the plugin stops.

## Dialog

- `SystemReportToolbarPlugin`: group Tasks, order 100, icon `file`, enabled with a selection of 1..50 devices, tooltip
  "Download the system reports of the selected devices as one ZIP" (above 50: "Choose at most 50 devices.").
- A save dialog first (`oadm-system-reports-<yyyy-MM-dd>.zip`), then `SystemReportWindow`: card "Devices" with the
  summary "6 devices · 2 done · 1 failed", `ui:ProgressRow` "Downloading system reports 4 of 6" / "Putting the reports
  into one file" / "Saving the file 40 %" / "Saved <path>", the result "N system reports saved, M failed. summary.txt in
  the file lists the failures.", a virtualized grid Address, MAC address, Model, Status (Waiting, Downloading, Done +
  size, Failed + reason).
- Polls every 500 ms, rows updated in place by id. Cancel (or closing the window) deletes the server job ("Cancelled. The
  file is incomplete; delete it."), then Close.

## Methods

`SystemReportMethods`, camelCase, models in `Shared/`.

| Method | Payload | Answer |
|---|---|---|
| `start` | {deviceIds} | the status with every device; audited ("Plugin action", "System report", "start") |
| `status` | {jobId, sinceVersion} | state running/packing/done/failed, total, finished, failed, size, version, only the devices changed after `sinceVersion` |
| `read` | {jobId, offset} | 2 MB base64 chunks |
| `delete` | {jobId} | none |

## Permissions

No page, so no "show": one key `oadm.system-report:download` for every method; the toolbar button
(`SystemReportToolbarPlugin.RequiredPermission`) hides without it. Operator default: on.

## Device groups

Like backups and the audit log, system reports stay with users who have all devices: every method answers true from
`RequiresAllDevices` (the host refuses group users before the plugin runs), and the toolbar button is hidden for them
(`IToolbarContext.HasAllDevices`, updated on `PermissionsChanged`). A report is a job of its user and session
(`ctx.Jobs.Start`, the job id is the report id); `status`, `read` and `delete` look it up with `ctx.Jobs.Find(caller,
id)`, so another user's or session's id answers like an unknown one ("The system report job no longer exists; start it
again.", `delete` does nothing). When the host ends the job (the session ended) the report and its files are deleted.
See [device groups](../../docs/spec/device-scope.md).

## Tests

- `tests/Oadm.Plugins.SystemReport.Tests`: downloader (modes, fallback, 401/403 not retried, refused statuses, timeout,
  unreachable, a 20 MB answer streamed past the 16 MB limit, the 256 MB cap); jobs (bundle and summary, failures, 4 at a
  time, status deltas, chunked read, delete, 30 min expiry, stop and crash leftovers, 5,000 devices); the client view
  model against the real plugin and fake mode, cancel, toolbar, hidden for a device group; `CanaryTests`; headless
  `system-report-dialog.png`.
- `tests/Oadm.Server.Tests/SystemReportServerTests`: no page, Operator call, audit, data folder.
- Fake mode (`FakeOadmApi.SystemReport.cs`): 600 ms per device, four at a time, bad statuses fail, small generated ZIPs.
- Read-only hardware test `SystemReportHardwareTests` through the in-process server.
