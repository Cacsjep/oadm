# Snapshot report (`oadm.snapshot-report`)

`plugins/Oadm.Plugins.SnapshotReport` (+ `.Client`), id `oadm.snapshot-report`, rail page **Snapshot
report** (icon `snapshot`). Purpose: the yearly maintenance check. The technician sees current
snapshots of every video source of the managed video devices, checks them fast and exports a PDF report.
Read-only for devices (param.cgi reads and image.cgi snapshots).

- Sources: the live view discovery, exposed to plugins as `IVapixClient.GetVideoSourcesAsync()` (SDK
  `VideoSource(Camera, Name, Sensor, Resolutions)`; Core implements it with `GetImageCapabilitiesAsync`).
  Only `IDeviceInfo.HasVideo` devices. One source = one tile titled with the address; several = one tile
  per source "<address> - <label>": the view area name ("View Area 2", "Quad view"), or "Sensor n"
  (encoders "Channel n") for empty or generic "Camera n" names. Devices in IPv4 order. A device whose
  sources cannot be read (or whose status is CredentialsRequired / PasswordNotSet / CertificateChanged,
  checked without a request) is one error tile. Sources are cached 5 minutes for the snapshots.
- Snapshot: `GET /axis-cgi/jpg/image.cgi?camera=N&resolution=WxH` (JPEG API) with the stored
  credentials, server side. Resolution: the largest of the source that fits the box with the sensor
  aspect (`VideoResolutions.Choose`, shared with the live view): grid 1280x720, report 1920x1080
  (`SnapshotReportPluginInfo`). Verified on the development camera (AXIS OS 12.11): 1280x720 about 125 KB in
  0.1-0.3 s, camera=1/2 are the two view areas, a missing camera answers HTTP 400 with an HTML
  "400 Bad Request" page. At most 4 device requests at a time (all callers), 3 s timeout each.
  Errors are short texts (shared with every plugin: SDK `DeviceMessages`): "Timeout after 3 s", "Unauthorized - HTTP 401 (check the credentials)", "Forbidden - HTTP 403 (administrator rights are required)",
  "Bad Request - HTTP 400", "Unreachable - <socket error>", "The device has no video source 3", or the
  VAPIX text the device sent ("Error: ...").
- `InvokeAsync` methods (`SnapshotReportMethods`, JSON camelCase, models in `Shared/`): `listSources`
  ({deviceIds}, empty = all) -> tiles with device facts; `snapshot` ({deviceId, camera, maxWidth,
  maxHeight}) -> {jpegBase64, width, height, capturedUtc, error}; `generateReport` ({site, technician,
  date, items[{deviceId, camera}]}) starts a background job (fresh report-size snapshots, then the
  PDF) and returns its status; `reportStatus` ({jobId}) -> state running/done/failed, done/total,
  message, size, pages, failed; `readReport` ({jobId, offset}) -> 2 MB base64 chunks (a report is larger
  than one gRPC message); `deleteReport`. Jobs live in server memory, 30 minutes after last use.
- Page: toolbar **Create snapshots** (opening the page contacts no device; the first load starts only
  with this button, the page shows "No snapshots yet" until then; disabled afterwards), Refresh all (enabled after the first load), "Select all" check box, **Export PDF** (primary); picture size slider labeled "Size"
  with its px value (200-720 px) and `ui:SearchBox` on the right; status line "14 pictures from 9 cameras · 13 selected
  · 3 failed"; `ui:ProgressRow` while loading ("Loading snapshots 5 of 14", 4 at a time). Tiles
  (`Border.tile`, accent outline on hover or keyboard focus; selection is the check box only) in a wrap panel: picture (`Button.picture` on
  `Border.liveViewSurface`, Stretch Uniform; click = large preview window with facts and "Take new
  snapshot"), check box + title + refresh icon button, facts line (model · firmware · MAC), then the
  capture time and size, or a `ui:StatusChip` (Loading accent, error red with the message). A failed
  snapshot keeps the older picture. All tiles start selected.
- Export dialog: Site / customer (required), Technician, Date (yyyy-MM-dd, today), file name preview;
  site and technician (and the last folder) remembered per client in
  `LocalApplicationData/Oadm/plugins/oadm.snapshot-report/export.json`. Export asks for the file first
  (save dialog), then shows the job progress and downloads the PDF; failed snapshots keep the dialog
  open with a warning.
- PDF (PDFsharp + MigraDoc 6, MIT; QuestPDF rejected for its license; Roboto embedded): A4 portrait.
  Cover: "Maintenance report", site, technician, date, creation time and OADM version, summary (cameras,
  video sources, online, offline or not OK, snapshots taken / failed), firmware versions with counts,
  certificates expired or expiring within 30 days. Then two snapshots per page: heading "<title> ·
  <model>", the JPEG as sent by the device (embedded unchanged, DCTDecode) in a 17 x 8.5 cm box, facts
  table (model, MAC, address/host name, firmware, status, certificate, snapshot time and size, source);
  a failed snapshot is a grey box "No snapshot: <error>". Footer "<site> · <date> · Maintenance report"
  and "Page n of m". About 250 KB per full-HD snapshot.
- Fake mode (`FakeOadmApi.SnapshotReport.cs`): the core plugin is listed, tiles come from the fake live
  view sources (P3265-V two view areas, P3727-PLE four sensors + quad view, error tiles for the devices
  with a bad status), pictures are generated JPEGs, the PDF is a one-page placeholder.
- Tests: `tests/Oadm.Plugins.SnapshotReport.Tests` (source expansion, request and error mapping, PDF parsed
  back with PDFsharp: pages, cover text via ToUnicode, DCT images; view models; headless screenshots
  `snapshot-report-page.png`, `-preview.png`, `-export.png`; hardware test through the in-process server
  writing `snapshot-report-<camera>.pdf` to `OADM_SCREENSHOT_DIR`).
