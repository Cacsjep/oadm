# System report (`oadm.system-report`)

`plugins/Oadm.Plugins.SystemReport` (+ `.Client`), id `oadm.system-report`, like ADM's "Get system report" (a toolbar button, no rail page). The server downloads the server report of every selected device and the
client saves one ZIP for Axis support. Read-only for devices.
- Device request: `GET /axis-cgi/serverreport.cgi?mode=zip_with_image` (report + snapshot, what Axis support asks for;
  video devices) else `mode=zip`; zip_with_image answering 400/404/500/501 or 200 without a ZIP is retried as zip. Stored
  credentials, the account must be an administrator (403 = "Forbidden - HTTP 403 (administrator rights are
  required)"). Answer streamed to disk (`VapixRequestOptions.StreamResponse`), must start with a ZIP signature, at most 256
  MB, 5 minutes per device, at most 4 devices at a time (all jobs together). Verified read-only on the development camera (P3265-V, AXIS
  OS 12.11): `application/zip`, chunked, zip_with_image about 0.3 MB in 34-37 s (`serverreport_cgi.txt` 1.3 MB unpacked +
  `serverreport_image.jpg`), zip about 0.16 MB in 31 s; `Content-Disposition: attachment; filename=Axis_SR_<date>_<time>_<MAC>.zip`.
  Devices with CertificateChanged / CredentialsRequired / PasswordNotSet are refused without a request (snapshot report
  texts); unknown ids "1 device is no longer available." (the shared text of a device the user cannot reach).
- Jobs (`SystemReportJobs`, server memory, files in `ICorePluginContext.DataDirectory/jobs/<job id>/`, leftovers deleted at
  start): one file per device, then `bundle.zip` = every report stored unchanged (no compression) as
  `<address>_<MAC>_<model without "AXIS ">_<yyyyMMdd-HHmmss UTC>.zip` (other characters than letters, digits, '.', '-' become
  '-', a name used twice gets "-2") plus `summary.txt` (created time, OADM version, counts, one line per report with
  address, MAC, model, size and mode, then the failures with their reason). Dropped 30 minutes after the last use (checked
  every minute and on each call), on `delete` and when the plugin stops. 1..50 devices per job (`MaxDevices`; the toolbar button is disabled above 50 with "Choose at most 50 devices.": each report is up to 256 MB on the server disk).
- Methods (`SystemReportMethods`, camelCase, models in `Shared/`): `start` ({deviceIds}) -> status with every device;
  `status` ({jobId, sinceVersion}) -> state running/packing/done/failed, total, finished, failed, size, version and only the
  devices changed after `sinceVersion` (5,000 devices: no full list per poll); `read` ({jobId, offset}) -> 2 MB base64
  chunks; `delete`. All Operator; `start` is audited ("Plugin action", "System report", "start").
- Device groups (SDK 0.4.0): like backups and the audit log, system reports stay with users who have all devices. Every
  method answers true from `RequiresAllDevices` (the host refuses users with a device group before the plugin runs) and
  the toolbar button is hidden for them (`IToolbarContext.HasAllDevices`, updated on `PermissionsChanged`). A report is
  a job of its user and session (`ctx.Jobs.Start`, the job id is the report id): `status`, `read` and `delete` look it up
  with `ctx.Jobs.Find(caller, id)`, so another user's or session's id answers like an unknown one ("The system report job
  no longer exists; start it again.", `delete` does nothing). The job lasts until the report is deleted (`delete`, 30
  minutes unused, the plugin stopping); when the host ends it (the session ended) the report and its files are deleted.
  The devices are looked up in the caller's view.
- Client (`SystemReportToolbarPlugin`, group Tasks, order 100, `ui:ToolbarButton` "System report" with text, icon `file`,
  enabled with a selection, tooltip "Download the system reports of the selected devices as one ZIP"): save dialog first (`oadm-system-reports-<yyyy-MM-dd>.zip`), then `SystemReportWindow` (title bar, card
  "Devices" with the summary "6 devices · 2 done · 1 failed", `ui:ProgressRow` "Downloading system reports 4 of 6" /
  "Putting the reports into one file" / "Saving the file 40 %" / "Saved <path>", result chip "N system reports saved, M
  failed. summary.txt in the file lists the failures.", virtualized DataGrid Address, MAC address, Model, Status chip
  (Waiting, Downloading accent, Done ok + size, Failed red + reason)); polls every 500 ms, rows updated in place by id;
  Cancel (or closing the window) deletes the server job ("Cancelled. The file is incomplete; delete it."), then Close.
  Fake mode (`FakeOadmApi.SystemReport.cs`): 600 ms per device, four at a time, bad statuses fail, small generated ZIPs.
- Tests: `tests/Oadm.Plugins.SystemReport.Tests` (downloader: modes, fallback, 401/403 not retried, refused statuses,
  timeout, unreachable, a 20 MB answer streamed past the 16 MB limit, the 256 MB cap; jobs: bundle content and summary,
  failures, 4 at a time, status deltas, chunked read, delete, 30 min expiry, stop and crash leftovers, 5,000 devices;
  client view model against the real plugin and fake mode, cancel, toolbar flow, hidden for a device group; the
  device-group canary `CanaryTests`; headless `system-report-dialog.png`;
  read-only hardware test `SystemReportHardwareTests` through the in-process server), `tests/Oadm.Server.Tests/
  SystemReportServerTests` (no_page, operator call, audit, data folder).

## Permissions

No page, so no "show": one key `oadm.system-report:download` for every method; the toolbar button
(`SystemReportToolbarPlugin.RequiredPermission`) hides without it. Operator default: on.
