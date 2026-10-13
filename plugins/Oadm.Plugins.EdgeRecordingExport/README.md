# Edge Recording Export (`oadm.edge-recording-export`)

Core plugin (+ `.Client`), rail page **Edge Recording Export** (group Maintenance, icon `hardDriveDownload`), **off by
default**, switchable. Finds the recordings on the SD card or network share of chosen cameras in a time range and
downloads them to the client machine, one folder per camera.

## Decisions

| Topic | Decision |
|---|---|
| Cameras | The shared device picker (`DevicePickerWindow.ShowChoiceAsync`, All, chosen devices and tags), listing video devices only; a tag means its video devices at the time of the search. |
| Range | "All recordings" or From / To in this computer's time (Avalonia's `DatePicker` + `TimePicker`, 24 hours, styled in the theme; the row's error below both pickers through `FormField.Error`), sent as UTC; the same moment for cameras in other time zones. |
| Search | One `search` call per camera from the page, 4 cameras at a time; the server keeps no state. Each row: recordings found, estimated size, first and last recording, status. A total line "12 cameras · 340 recordings · about 48 GB · 1 not searched". |
| Cut | The camera cuts the listed start and stop of every recording to the range; the export uses exactly those times (from the key frame before the start). "All recordings" exports whole recordings; one that still records ends at the moment of the request. |
| Size | `properties.cgi` per recording estimates its export in kB. With many recordings (10 s clips make 8,640 a day) only 40 spread over the list are asked and the rest is extrapolated by duration ("about"). No estimate at all = "Unknown" (progress then by file count). |
| Format | MP4 when the camera offers it (`capabilities.cgi`, plays everywhere), else Matroska (every camera with the export API). No encryption (the camera's ZIP with password is a later option). |
| Transfer | Through the server, never client to camera: the SDK file stream `PluginService.Download` (`ICorePluginDownloads`) passes the camera's export on in 256 KB chunks, uncompressed; nothing is stored on the server. One recording after the other: the camera's link is the limit, parallel downloads would not finish sooner. |
| Files | A folder per camera "10.0.0.48 P3265-V B8A44F631339" (address first, the MAC address keeps it unique); the camera's file names ("20261012_200927_20261012_200933.mp4", start_stop in the camera's time); written as `.part` and renamed once complete. A file that exists is skipped ("already there"), so a stopped run is simply started again; two recordings with the same name in one run get "_2". Names are safe on Windows, Linux and macOS. |
| Errors | A recording that fails (deleted meanwhile, camera gone) counts in its row ("18 downloaded, 2 failed", the last reason as detail) and the next one runs. A folder that cannot be written (disk full) stops the download with the reason below the table. |
| Lifetime | The view model lives as long as the rail page: switching pages does not stop a download; logging out or turning the plugin off stops it. Stop ends the current file (its `.part` is deleted) and keeps the finished ones. |
| Roles | "Show the page" (Operator and Viewer: search), **Export recordings** (`export`: Operator yes, Viewer no). Every downloaded recording is audited ("Plugin action", "Exported recording: 10.0.0.48, 2026-10-12 18:09:25 to 2026-10-12 18:09:35 UTC"). |
| Refusals | Certificate changed, Credentials required and Password not set are not contacted (shared `DeviceMessages`). Not a video device, no edge storage (`Properties.LocalStorage.LocalStorage`), no export (`Properties.LocalStorage.ExportRecording`, AXIS OS 5.60+) are the row's problem. Every search also reads the disk list (`disks/list.cgi`): a card that failed, is encrypted and cannot be opened, is not in use, read-only or locked is named next to the recordings found ("12 recordings found" as warning, "The SD card failed"), or is the row's status when nothing was found; no disk present: "No SD card or network share". A full card is no problem (the camera removes the oldest recordings). |
| Scale | At most 50,000 recordings per camera and search (pages of 1,000); the grid is virtualized; 5,000 cameras tested. |

## Device API (read-only; Viewer and above)

Verified on an AXIS P3265-V (AXIS OS 12.11, export schema 1.4) and an AXIS camera without SD card (fixtures in
`tests/Oadm.Plugins.EdgeRecordingExport.Tests/Fixtures`):

| Request | Use |
|---|---|
| `param.cgi` group `Properties.LocalStorage` | `LocalStorage`, `ExportRecording` = yes |
| `record/export/capabilities.cgi?schemaversion=1` | export formats (`matroska`, `mp4`); failing = Matroska |
| `record/list.cgi?recordingid=all&sortorder=ascending&startatresultnumber=N&maxnumberofresults=1000&starttime&stoptime` | the recordings that overlap the range, their times cut to it; `startatresultnumber` counts from 0, `numberofrecordings` = matches; a recording that still records has an empty stop |
| `record/export/properties.cgi?schemaversion=1&recordingid&diskid&exportformat&starttime&stoptime` | `EstimatedFileSize` in kB (-1 unknown); errors as HTTP 200 `GeneralError` (110 recording not found) |
| `record/export/exportrecording.cgi` (same parameters) | the file, chunked, `Content-Disposition` with the name; HTTP 400 for a recording that is gone; streamed (`VapixRequestOptions.StreamResponse`, 60 s until the headers) |
| `disks/list.cgi?diskid=all` | with every search: the state of the SD card and network share (status, read-only, locked; "disconnected" = not there) |

## Methods

| Method | Kind | Payload | Answer | Permission |
|---|---|---|---|---|
| `search` | Invoke | `SearchRequest` {deviceId, startUtc?, stopUtc?} | `SearchResult` {deviceId, recordings [{id, diskId, start, stop?, source, type}], estimatedBytes?, format, problem?, truncated, noDisk} | show |
| `export` | Download | `ExportRequest` {deviceId, recordingId, diskId, startUtc?, stopUtc?, format} | the file (name from the camera, length when sent) | export, audited |

## Tests

`tests/Oadm.Plugins.EdgeRecordingExport.Tests`: parsing of the recorded answers, the server part against a fake camera
that answers like the P3265-V (paging, cut times, estimates, refusals, export), the page (choice, range errors, search
summary, download into a folder per camera, skip on the second run, "_2", a failed recording, Stop, the export right),
a headless screenshot (`edge-recording-export.png`, `-download.png`), Perf (5,000 cameras), and the read-only hardware
test through the in-process server and the real gRPC client (searches the last two minutes of every dev camera and
downloads them). Server: `tests/Oadm.Server.Tests/PluginDownloadTests` (the SDK file stream). Fake mode
(`FakeOadmApi.EdgeRecording.cs`): a 10 s clip every 40 minutes, continuous hours on the Q6135-LE, no edge storage on
the M3106-L Mk II, a read-only card on the Q1656-LE.
