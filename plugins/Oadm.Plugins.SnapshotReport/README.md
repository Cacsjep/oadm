# Snapshot report (`oadm.snapshot-report`)

Core plugin (+ `.Client`) with the rail page **Snapshot report** (group Reporting, icon `snapshot`), on by default. The
yearly maintenance check: current snapshots of every video source of the chosen cameras, checked fast and exported as a
PDF report. Read-only for devices. Rules: [Bundled plugins](../../docs/spec/bundled-plugins.md#snapshot-report-core-plugin).

## Device API

- Sources: `IVapixClient.GetVideoSourcesAsync()` (SDK `VideoSource(Camera, Name, Sensor, Resolutions)`, the live view
  discovery). Only `IDeviceInfo.HasVideo` devices, in IPv4 order. One source = one tile titled with the address; several
  = one tile per source "<address> - <label>": the view area name ("View Area 2", "Quad view"), or "Sensor n" (encoders
  "Channel n") for empty or generic "Camera n" names. Sources are cached 5 minutes for the snapshots.
- Snapshot: `GET /axis-cgi/jpg/image.cgi?camera=N&resolution=WxH` with the stored credentials, server side. Resolution:
  the largest of the source that fits the box with the sensor aspect (`VideoResolutions.Choose`, shared with the live
  view): grid 1280x720, report 1920x1080 (`SnapshotReportPluginInfo`); reports with more than 200 snapshots take them at
  1280 wide at most, above 1,000 at 960 (`ReportJobs`, memory bound; at most 5,000 per report).
- At most 4 device requests at a time (all callers), 3 s each. A device whose sources cannot be read, or whose status is
  Credentials required, Password not set or Certificate changed (checked without a request), is one error tile.
- Errors are the shared texts of SDK `DeviceMessages` ("Timeout after 3 s", "Unauthorized - HTTP 401 (check the
  credentials)", "Bad Request - HTTP 400", ...), "The device has no video source 3", or the VAPIX text of the device.
  A report item of a device without video gets its facts and "The device has no video" (`SnapshotService.NoVideo`).
- Recorded on the development camera (AXIS OS 12.11): 1280x720 about 125 KB in 0.1-0.3 s, camera=1/2 are the two view
  areas, a missing camera answers HTTP 400 with an HTML page.

## Page

- Device choice: `PageDeviceChoice` (noun "cameras", only devices with video) first in the toolbar. The page starts with
  the Devices page selection, else the last choice on this computer (`GetLocalSetting`), else nothing ("Choose cameras,
  then press Create snapshots."). All devices sends `listSources` with no ids; a tag means its cameras at the moment of
  the load. A new choice after the first load reads at once; before it, choosing reads nothing.
- Toolbar: **Create snapshots** (the first load; opening the page contacts no device and shows "No snapshots yet"),
  **Refresh all**, **Select all**, **Export PDF**; on the right "Size" (200-720 px) and `ui:SearchBox`. Status line "14
  pictures from 9 cameras · 13 selected · 3 failed"; `ui:ProgressRow` "Loading snapshots 5 of 14".
- Tiles (`Border.tile`) in virtualized rows: picture (click = large preview with facts and "Take new snapshot"), check
  box + title + refresh button, facts line (model · firmware · MAC), then capture time and size or a `ui:StatusChip`
  (Loading, error with the message). A failed snapshot keeps the older picture. All tiles start selected.
- Export dialog "Export maintenance report": Site / customer (required), Technician, Date (yyyy-MM-dd, today), the file
  name; site, technician and the last folder are kept in
  `LocalApplicationData/Oadm/plugins/oadm.snapshot-report/export.json`. It asks for the file first, then shows the job's
  progress and downloads the PDF; failed snapshots keep the dialog open with a warning.
- PDF (PDFsharp + MigraDoc 6, MIT; QuestPDF rejected for its license; Roboto embedded), A4 portrait. Cover: "Maintenance report", site, technician, date,
  creation time, OADM version, summary (cameras, video sources, online, offline or not OK, snapshots taken / failed),
  firmware versions with counts, certificates expired or expiring within 30 days. Then two snapshots per page: "<title> ·
  <model>", the JPEG embedded unchanged (DCTDecode) in a 17 x 8.5 cm box, a facts table; a failed snapshot is a grey box
  "No snapshot: <error>". Footer "<site> · <date> · Maintenance report" and "Page n of m". About 250 KB per full-HD snapshot.

## Methods

`SnapshotReportMethods`, JSON camelCase, models in `Shared/`.

| Method | Payload | Answer |
|---|---|---|
| `listSources` | {deviceIds} (empty = the user's view) | tiles with device facts |
| `snapshot` | {deviceId, camera, maxWidth, maxHeight} | {jpegBase64, width, height, capturedUtc, error} |
| `generateReport` | {site, technician, date, items[{deviceId, camera}]} | the job's status (fresh report-size snapshots, then the PDF) |
| `reportStatus` | {jobId} | state running/done/failed, done/total, message, size, pages, failed |
| `readReport` | {jobId, offset} | 2 MB base64 chunks (a report is larger than one gRPC message) |
| `deleteReport` | {jobId} | none |

Report jobs (`ReportJobs`):

- A finished report stays in server memory only until it was downloaded: the read of the last chunk drops it at once
  (the client sends no delete). One never downloaded is dropped 30 minutes after it ended (`Lifetime`, a timer every
  minute), and with its host job (logout, other devices).
- One user builds at most 2 reports at once (`MaxRunningPerUser`); a further one waits with "Wait for your earlier
  reports to finish". All finished PDFs together stay within 1 GB (`DefaultMaxFinishedBytes`): the oldest finished ones
  of any user go, never the one that just finished.
- A dropped or downloaded report answers "The report no longer exists; create it again.". A report is cancelled before
  its token source is disposed, and a start that fails after its job started ends the job.

## Permissions

`oadm.snapshot-report:show` (`listSources`) and `:report` "Create snapshots and reports" (snapshots and the PDF). Both are
on for Operator and the built-in Viewer (the page only reads cameras). Without "report" the toolbar is hidden.

## Device groups

Every method takes the cameras from the user's call: a camera outside the group answers like an unknown one (NOT_FOUND
"1 device is no longer available.", the whole list or report is refused, nothing is read); an empty list means the
cameras of the user's view. A report is a job of its user (`ctx.Jobs`): it takes the snapshots through the user's view,
and another user's id answers like an unknown one. The overload without a call (in-process callers) uses the plugin's
context, which refuses in a group user's call. See [device groups](../../docs/spec/device-scope.md).

## Tests

`tests/Oadm.Plugins.SnapshotReport.Tests`: the device-group canary `CanaryTests`, source expansion, request and error
mapping, report jobs (`ReportJobLifetimeTests`), the PDF parsed back with PDFsharp (pages, cover text, DCT images), view
models, `ScaleTests`, headless screenshots `snapshot-report-page.png`, `-preview.png`, `-export.png`. Fake mode
(`FakeOadmApi.SnapshotReport.cs`): tiles from the fake live view sources (P3265-V two view areas, P3727-PLE four sensors
+ quad view, error tiles for bad statuses), generated JPEGs, a one-page placeholder PDF. Read-only hardware test through
the in-process server writes `snapshot-report-<camera>.pdf` to `OADM_SCREENSHOT_DIR`.
