# License Plate Center

One plate list with plate tags for every camera with AXIS License Plate Verifier (ALPV) 3. OADM listens to each camera,
decides every read (Allowed, Not allowed, Unknown plate), keeps a log and gives the event rules their events. The
cameras' own lists stay untouched. Off by default; turn it on on the Plugins page.

The rules and decisions are in [docs/spec/license-plate-center.md](../../docs/spec/license-plate-center.md). This file holds
the build: where things are, what was verified, and the contract of the client part.

## Status

Built (2026-11-27): the server part and the client part (`Oadm.Plugins.LicensePlates.Client`, the rail page with the tabs
Log, Plates, Cameras and Stats and their dialogs). The client builds against "Client contract" below; the folder of the
Settings dialog is the shared `ui:ServerFolderPicker`.

## Use

- **Cameras**: Add cameras, then Set up (areas, recognition, events), Allowed tags, Use recommended image settings.
- **Plates**: add, import (ALPV's export or OADM's CSV), give plate tags. A camera allows the plates with its allowed tags.
- **Log**: live reads, filters, the columns the user chooses, Export (PDF, CSV) in those columns. **Stats**: the reads of
  the last hour, 24 hours, 7 days and in all; the plate and vehicle confidence per camera; reads and confidence over time.
- **Settings** (Administrators): character deviation, wait for the best read, the log's days, size, folder, pictures,
  unknown plates. Without a folder nothing is stored; events still fire.

## Parts

| Where | What |
|---|---|
| `LicensePlatesContract.cs` | Ids, limits, permissions, methods, the DTOs of every call (shared with the client part) |
| `LicensePlateTexts.cs` | Every status, result and refusal text, one phrase per concept |
| `LicensePlatesPlugin.cs`, `.Methods.cs` | The core plugin: start, reads to events and log, timers, page methods, rights, downloads |
| `Plates/` | `PlateStore` (records `plates`, `tags`, `cameras`), `PlateRules`, `PlateText`, `PlateCsv` (import and export) |
| `Matching/` | `PlateMatcher` (exact: one dictionary lookup; wildcard plates: `WildcardIndex`; near: `DeletionIndex` per camera tag set), `EditDistance` |
| `Reads/` | `AlpvEvent` (ALPV's fields), `ReadAggregator` (one read per car and camera, on a `TimeProvider`) |
| `Cameras/` | `CameraRuntime`: one event WebSocket per camera, ALPV checks, status, gaps |
| `Alpv/` | `AlpvClient` (every ALPV call, also the make and model lists), `AlpvConfig` (the configuration, secrets dropped while parsed), `AlpvLists` with `alpv-lists-3.1.7.json` (ALPV's regions, countries, states, vehicle types and colours, embedded) |
| `Log/` | `PlateLogStore` (SQLite), `PlateLog` (folder, problems, hourly cleanup), `PlateLogExport` (CSV, PDF) |
| `Events/` | The rule events and the problem reporter |
| `Tasks/` | The page tasks Set up License Plate Verifier and Use recommended image settings, `SetupRules` |
| `Tools/` | The MCP tools (`oadm_license_plates_*`) |
| `../Oadm.Plugins.LicensePlates.Client` | The page: `LicensePlatesViewModel` (tabs, the header's connected count, the live events, the plate tags and camera choice the tabs share), `LogViewModel`, `PlatesViewModel`, `CamerasViewModel`, `StatsViewModel`; the dialogs `PlateEditorViewModel`, `ImportViewModel`, `TagsDialogViewModel` (Change tags, Allowed tags, Manage tags), `SetupViewModel` with `AreaPicture` (the areas over the live video), `CopySettingsViewModel`, `SettingsViewModel`; `PageClient` (the calls) and `IPageDialogs` (the view shows the dialogs; tests replace it); `PageTexts` |

The client part links the shape rules and the overlay painter of the vision plugins (`plugins/Shared/Vision.Client`:
`ShapeGeometry.cs`, `PictureOverlay.cs`) and uses the server part's `PlateRules`, so the editor's checks are the server's.

Linked sources: `plugins/Shared/EventStream` (the event connection, shared with Device triggers), `ContextCall.cs`, the
Applications client (`Oadm.Plugins.Acap/ApplicationApiClient.cs` and `Shared/`), the Logs page export's `LogTable.cs`
(table, CSV, the user's clock) and `LogPdfFonts.cs` (Roboto, embedded from Snapshot report's files under the server's
resource names), OADM's CSV writer and reader.

Packages: `Microsoft.Data.Sqlite.Core` 10.0.13 and `PDFsharp-MigraDoc` 6.2.4. SQLite is compiled against only
(`ExcludeAssets="runtime"`): the server ships the same version with EF Core, the plugin load context falls back to it and
its native SQLite. PDFsharp is the plugin's own copy, as in Snapshot report.

## Server decisions of the build

- **Matching**: an exact match is one lookup in the plate store, then the tag and date check (O(1)); near matches use a
  symmetric deletion index per camera tag set (each plate under its texts with up to the deviation of characters deleted,
  64-bit hashes in one sorted array), built on the first read of a set and rebuilt in the background when a plate with
  one of its tags changes; every candidate is checked against the current list with the bounded edit distance. Plates
  shorter than 6 characters deviate at most 1 with the setting 2 (the list's plate counts).
- **Wildcard plates** (`AXIS12**`, `*` = one letter or digit): the plate store keeps them in a `WildcardIndex` too (by length
  and the positions of the first three fixed characters, keyed by those characters); the matcher asks it after the exact
  lookup and before the near match, never with a deviation; more fixed characters win, a tie allows when any allows. They
  are left out of the deletion index. At most 1,000. The read keeps the camera's text as `Plate` and the pattern as
  `ListEntry` (log column `list_entry`, added once to older stores; event field `listEntry`).
- **Log search by name**: `Named` resolves the search through the plate list (`PlateStore.PlatesNamed`, at most 1,000, else
  `LogPage.TooManyNames`) into `LogQuery.NamedPlates`; the store unions the plate prefix with `plate IN (...)` and, for
  wildcard plates, `list_entry IN (...)`, each on its index. `BulkFillAsync` fills a log fast for the 10-million Perf test.
- **Reads**: the read's time is ALPV's `capture_timestamp`. A Lost of a car never seen with a text is a read of its own.
- **Log store**: `<folder>/plate-log.db` (WAL, `auto_vacuum=INCREMENTAL`), crops in `<folder>/pictures/<yyyy-MM-dd>/<id>.jpg`
  (the server's day; only JPEG, at most 512 KB, never converted). Writes: one writer, up to 2,000 rows per transaction.
  The Stats tab counts from the hourly table written with every read: per hour, camera, result and direction the reads,
  the confidence sum and the reads below 60, 60 to 74 and 75 to 89 (`low`, `mid`, `good`; `mid` and `good` are added to
  older stores, their hours count as 90 and above), the vehicle confidence sum and the reads with one (`vehicle`,
  `vehicles`, added once to older stores). `StatsSeries` builds the steps (hours, or days from midnight of the caller's
  zone) from it. The reads table has `state` and `vehicle_confidence` (added once). Gap rows ("Connection lost from 13:58 to 14:06") are rows with `gap_until`.
  A full disk or a folder that cannot be written stops storing (the problem is on the page and in Any camera problem);
  the folder is tried again every minute; the cleanup runs every hour.
- **Folder**: no folder by default (nothing stored until an Administrator chooses one). Refused: a relative path, a folder
  OADM cannot write, a folder inside the plugins' data of OADM (`plugin-data`, part of every backup), and then, on every
  save with a folder, one the host's folder checks refuse (`ICorePluginContext.FolderCheck`, the folder picker's rules:
  no system or OADM folder, as the service only administrators can change it); its text is the `folder` error. A new
  folder starts a new log; the old folder keeps its files.
- **Device groups**: one plate list and tag set; cameras, reads, pictures, stats and events by `call.Devices` (fail
  closed). The log of a group user holds the reads of every camera of the group, also of cameras removed from the plugin.
- **Audit**: every change and every export; the texts never hold a plate (personal data). The automatic deletion of
  Delete when it ends is one entry per sweep with the count ("Deleted 3 plates whose time ended", user `system`,
  `ICorePluginContext.Audit`); the server log names the plates.
- **ALPV checks**: the application list, the configuration and the event switches are read when a camera is added, on
  Refresh, after a task of the plugin, every 5 minutes for a camera with a problem (and while its events are off). A
  camera whose `camera.videoChannelId` is not 1 takes part in nothing. The Set up task refuses areas on a rotated picture
  (`frame_rotate` not 0).
- **Events**: Plate allowed, Plate not allowed, Unknown plate and Plate read are one type each with `DeviceIds` = the
  cameras of the plugin (changed with the cameras); Camera problem is stateful per camera (connection lost for 30 s,
  ALPV stopped, events off); Any camera problem is a server event (also on for a problem of the log store).

## Verified on

AXIS P3265-V, AXIS OS 12.11.147, ALPV 3.1.7, read only on 2026-11-27: `applications/list.cgi`, `config.cgi`,
`config.cgi?config=camera`, `api.cgi?api=getconfig`, `vapix_events.cgi` (the fixtures of the tests, secrets replaced). The
event topics and fields follow the spec; the stream itself and every write were not run on hardware by this build
(writes are tested against fakes; hardware writes only as `HardwareWrite`, on request).

## Client contract

The client part calls `InvokeAsync(method, json)` and `OpenDownloadAsync(method, json)` of this plugin and watches its
events. Names are the constants of `LicensePlatesMethods`; the JSON is `LicensePlatesJson.Options` (camelCase, enums as
camelCase names); the DTOs are the records of `LicensePlatesContract.cs`. Permissions: **show** = View License Plate
Center, **log** = View plate log, **export** = Export plate log, **plates** = Manage plates, **cameras** = Set up cameras,
**Admin** = Administrators only (also all devices).

| Method | Request -> reply | Right |
|---|---|---|
| `log` | `LogPageRequest` (query, `before` or `after` cursor, limit up to 500) -> `LogPage` (rows newest first, `next`) | log |
| `logCount` | `LogQuery` -> `CountReply` | log |
| `logStatus` | none -> `LogStatus` (reads today, used and max bytes, days, folder, problem) | log |
| `picture` | `ReadIdRequest` -> `PictureReply` (JPEG base64, null without one) | log |
| `stats` | `StatsRequest` (cameras, from, to, step hour or day, time zone; at most 31 days) -> `StatsReply` (the cards `Totals`, results, average confidence, `Start`, `Steps`, the Total line, a `CameraSeries` per camera) | log |
| `exportLog` (download) | `LogExportRequest` (query, `csv` or `pdf`, time zone, offset, range name, the chosen `Columns` in order, `LogColumns` keys) -> the file | export (and log) |
| `plates` | `PlatesRequest` (after, limit up to 5,000, search, tag) -> `PlatesPage` (plates, next, total) | show |
| `savePlate` | `SavePlateRequest` (plate, original) -> `SavePlateReply` (plate, or errors by field) | plates |
| `deletePlates` | `PlateTextsRequest` -> `CountReply` | plates |
| `changePlateTags` | `ChangePlateTagsRequest` (plates, add, remove) -> `CountReply` | plates |
| `tags` | none -> `TagsReply` (each tag with its plate and camera counts) | show |
| `saveTag` | `SaveTagRequest` -> `SaveTagReply` (tag, or errors by field) | plates |
| `deleteTag` | `DeleteTagRequest` (id, confirmed) -> `DeleteTagReply` (deleted false: in use, ask, send confirmed) | plates |
| `importPreview` | `ImportRequest` (upload id of the caller's upload, tags for every row) -> `ImportPreview` (id, format, counts) | plates |
| `importRows` | `ImportRowsRequest` (preview id, after, limit) -> `ImportRowsPage` (rows with status and error, next) | plates |
| `importSave` | `ImportSaveRequest` -> `ImportResult` (added, changed; one transaction) | plates |
| `exportPlates` (download) | none -> `plates.csv` | show |
| `cameras` | none -> `CamerasReply` (rows by address, connected and total, the log's problem) | show |
| `addCameras`, `removeCameras`, `refreshCameras` | `DevicesRequest` -> `CamerasReply` | cameras |
| `setAllowedTags` | `AllowedTagsRequest` (devices, `set`, or `add` and `remove`) -> `CamerasReply` | cameras |
| `readSetup` | `DeviceRequest` -> `SetupRead` (areas, recognition, events, rotated, last read, allowed tags, ALPV's page) | show |
| `saveSetup` | `SaveSetupRequest` (device, `SetupPayload`, allowed tags) -> `TasksReply` | cameras |
| `renameCamera` | `RenameCameraRequest` (device, name) -> `RenameCameraReply` (the refusal below the field, or none) | cameras |
| `copySettings` | `CopySettingsRequest` (source, targets, areas) -> `TasksReply` | cameras |
| `useRecommendedImageSettings` | `DevicesRequest` -> `TasksReply` | cameras |
| `alpvLists` | none -> `AlpvLists` (regions, countries, states, vehicle types and colours of ALPV 3.1.7; `CountriesOf`, `StatesOf`) | show |
| `vehicleMakes`, `vehicleModels` | `VehicleNamesRequest` (cameras, make) -> `VehicleNamesReply` (names, or the problem) | log |
| `settings` | none -> `LicensePlateSettings` | Admin |
| `saveSettings` | `LicensePlateSettings` -> `SaveSettingsReply` (settings, or errors by field) | Admin |

Live events (`WatchEventsAsync`): `reads` (a JSON array of camera ids with new or changed reads, once a second, only to
users who see the camera: read `log` with `after` = the newest row shown; a full page means more: read the newest page
again), `cameras` (a JSON array of camera ids whose row changed), `plates` (`{}`: plates or tags changed), `status`
(`{}`: the log's status or the settings changed). Field errors use the camelCase names of the DTO (`text`, `allowedTo`,
`name`, `folder`, `waitSeconds`, `areas.1`, `recognition.plateWidthMax`, ...); other refusals are the call's error text.

Notes for the page: the Picture column reads `picture` only for visible rows; the Result tooltip is
`LicensePlateTexts.Reason`; the read-as tooltip `LicensePlateTexts.ReadAs`; the header status
`LicensePlateTexts.ConnectedCount`; times of plates are wall-clock times of the server's zone (no offset).

## Tests

`tests/Oadm.Plugins.LicensePlates.Tests`: matching and wildcard plates (`WildcardTests`), the aggregator on a fake clock, ALPV's configuration of 3.0.13 and
3.1.7, the tasks against a fake camera, the log store, the export, the plugin through its methods with a fake event
stream, the device group canary and the tools. `PerfTests` (`Category=Perf`): 100,000 plates (also with 1,000 wildcard plates), 1,000 cameras among
100,000 devices, a log of 10 million reads (plate search under 100 ms), the export at 10,000 rows.

`Client/`: the page's view models against a sample server (`ClientSupport.cs`: Austrian plates, garages as tags, a camera
of every status, a log with every result): `LogTabTests` (rows, the deviation triangle, the result's tooltip, the
Confidence colours, filters, Pause, Add to plates, Export, pictures for shown rows only), `FilterChipsTests` (the Filter
button's chips), `GridSelectionTests` (a row clicked in the real grid reaches the toolbar, also after Remove and a reload),
`AlpvListsClientTests` (the selects of ALPV's lists), `PlatesTabTests` (search, tag
filter, rights, Delete, Change and Manage tags, the editor's errors and Delete when it ends, Import), `CamerasTabTests`
(statuses, image settings, toolbar rights, Set up, Copy settings, Settings, Stats: cards, bars, lines and legend),
`SetupAndColumnsTests` (fixed dialogs, the camera's name, the threshold in percent, the Log's chosen columns and the date), `WildcardClientTests` (the editor, the search, the Log's tooltip and List entry); `ScreenshotTests` (headless, PNGs with
`OADM_SCREENSHOT_DIR`, `OADM_TEST_APPEARANCE=Light` for the light theme); `ClientPerfTests` (`Category=Perf`: 100,000
plates, 1,000 cameras).
