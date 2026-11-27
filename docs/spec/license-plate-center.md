# License Plate Center

Status: **server part built (2026-11-27); the client part is not built**. The client builds against "Client contract" of
the plugin README (`plugins/Oadm.Plugins.LicensePlates/README.md`).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

One place for the plates of every camera that runs AXIS License Plate Verifier (ALPV). ALPV works per camera: its
lists live on one device and are copied to others by hand. License Plate Center keeps **one plate list with tags** on
the server, listens to every camera, decides per read whether the plate is allowed there, shows a live log and gives
the event rules their events. It also sets up the reading part of ALPV on many cameras. It is not a parking
management: no tickets, no payment, no occupancy, no barrier logic of its own.

Core plugin `plugins/Oadm.Plugins.LicensePlates` (+ `.Client`), id `oadm.license-plates`, rail page **License Plate
Center** in the rail group Integrations, bundled in the installers, free (Apache-2.0), **off by default**. Decisions of 2026-11-27, asked one by one,
checked read only on an AXIS P3265-V (AXIS OS 12.11.147) with ALPV 3.0.13 and 3.1.7, against the VAPIX library
("License Plate Verifier API") and the ALPV user manual (February 2026).

## Not part of it

- **The cameras' own lists** (Allow, Block, Custom) and their synchronization: never read, never written. The server
  decides (decision 2026-11-27). Writing the allowed plates into each camera's allowlist, so a barrier on the camera's
  relay opens without the server, is a planned second step with its own decisions and write tests.
- **Access control in ALPV** (relay, I/O, door controller, Secure Entry), the 2N and Genetec integrations, push
  profiles, ALPV's own storage, retention and text overlay: set on ALPV's own page. Many of their values are not
  documented, and a wrong one opens or blocks a barrier.
- **ALPV 2.** Only ALPV 3 (decision 2026-11-27). ALPV 3.1.7 needs AXIS OS 12.11.77 or newer.
- **Push events** (ALPV's TCP, HTTP and FTP profiles): not used. They need a listener, a firewall rule, a relay per
  site and a URL and login on every camera, and they take one of the camera's three profiles a VMS may need.
- Installing ALPV and its license: the Applications tasks (`oadm.acap`). Focus and zoom: Live View and Autofocus.

## Rules that never break

1. **The server decides, the cameras' lists stay untouched.** A read becomes Allowed, Not allowed or Unknown on the
   server from the one plate list. Nothing of the list is written to a camera.
2. **Our own connection.** One permanent event connection per camera (the VAPIX event WebSocket, as Device triggers
   does), through the host on the device's route. No listener on the server, nothing for a firewall.
3. **One list, tags instead of lists.** A plate exists once on the server. Plate tags say where it is allowed: each
   camera names the plate tags allowed there. A camera without allowed tags allows no plate.
4. **A known plate is never held back.** An exact match fires at once; only reads that match nothing exactly wait for
   the best read ("From events to a read").
5. **ALPV 3 only, checked before every write.** The application list is read fresh before the first write: `fflprapp`,
   major version 3, running, license valid. Anything else fails with `DeviceNotCompatibleException` ("Nothing was
   changed"). ALPV has no API list of its own, so the version of the app is the API version: another major version is a
   different API.
6. **Only fields the camera has.** A write sends only the values the user changed, only fields the camera's
   configuration returned in the read right before, and reads back after the write. Unknown fields are never sent and
   never removed.
7. **Every change on a camera is a task** of the plugin's task part (one task per device, named steps), with the HARD
   RULE of `CLAUDE.md` "device safety". All values are validated before the first write.
8. **First sensor only** (decision 2026-11-27, `CLAUDE.md` "ask about multi-sensor cameras"). ALPV reads one video
   channel (`camera.videoChannelId`). A camera whose ALPV reads another channel than the first shows "ALPV reads
   another sensor" and takes part in nothing; OADM never changes the channel. Extra view areas and group views are not
   used.
9. **Secrets of ALPV never leave the camera call.** ALPV's configuration answer carries logins and passwords (`login`,
   `password`, `ipc_login`, `ipc_password`). They are dropped when the answer is parsed: never kept, logged, sent to
   the client or written back.
10. **Plates are personal data.** The log has a retention in days and a size limit, lives in a folder the
    administrator chooses, is never part of a backup and is only shown to users with the log permission and only for
    cameras of their device group.
11. **Scale.** 100,000 plates, 1,000 cameras, 200 plate tags, a log of 10 million reads. Matching a read is O(1) for
    an exact match and bounded for a near match; every grid virtualized (`RangeObservableCollection`); the log is
    paged from the server and searched by index. Tests at these sizes (`Perf`).

## ALPV on the camera

Checked on the test camera; the VAPIX documentation and the device disagree in places, the device wins.

| Use | Call | Notes |
| --- | --- | --- |
| Installed, version, running, license | `/axis-cgi/applications/list.cgi` | `Name="fflprapp"`, `Version`, `Status`, `License` |
| Read the configuration | `GET /local/fflprapp/config.cgi` | XML `<configuration>`; carries secrets (rule 9) |
| Write settings | `POST /local/fflprapp/config.cgi` | XML with only the changed elements |
| Areas | `GET api.cgi?api=getconfig`, `POST config_json.cgi` | see "Areas" below |
| Image settings | `GET config.cgi?config=camera`, `GET api.cgi?api=camera&sensor=setoptimalparams` | the second one **writes** although it is a GET |
| Event switches | `GET vapix_events.cgi`, `POST vapix_events.cgi` | see "Event switches" below |
| Events | `tnsaxis:CameraApplicationPlatform/ALPV.New`, `.Update`, `.Lost` | fields below |

- **Events declared by ALPV 3.0.13** (the camera's event list): `ALPV.AllPlates`, `New`, `Update`, `Lost`, `RealTime`,
  `PlateInView`, `PlateIn`, `PlateOut`, `Roi1`, `Roi2`, `Allow_list`, `Block_list`, `Custom_list`, `NotInList`. The
  documentation writes some of these names in another case; the names of the device are used.
- **Fields of New, Update and Lost**: `text`, `lpConfidence` (0 to 1, not in the documentation), `carID`, `carState`,
  `carMoveDirection` (`in`, `out`, `unknown`), `roiID`, `country` (alpha-3), `region`, `regionCode`, `iso3166-2`,
  `view` (`front`, `rear`), `vehicleType`, `vehicleColor`, `vehicleMake`, `vehicleModel`, `vehicleConfidence`,
  `lpImage` (the plate crop, base64), `left`, `top`, `width`, `height`, `capture_timestamp` (ms UTC),
  `frame_timestamp`, `consumedTime`, `MACAddress`, and ALPV's own list result (`listName`, `listMode`,
  `listDescription`, `action`), which OADM ignores.
- **The events are switches in ALPV** (ALPV's page "Vapix events": New, Update, Lost and the others). OADM needs New,
  Update and Lost. A camera with one of them off shows "Events are off in ALPV"; Set up turns the three on.
- **Configuration values read on the camera** (ALPV 3.1.7): areas `roi`, `roitwo`, `active_roi`, `direction.angle`,
  `direction2.angle` (0 to 360000); `plate.plate_min_size`, `plate.plate_max_size` (plate width in pixels, 50 and
  300), `plate_min_size_symb`, `plate_max_size_symb` (number of characters, 4 and 10), `plate.threshold` (confidence
  threshold, 0.25), `regional_settings`, `CountryPriority` (camera location, `AUT`), `bw_ldist` (ALPV's own character
  deviation, not used by OADM), `ttl` (2000), `timeToReliable` (1000); image settings `camera.wdr`, `contrast`,
  `localcontrast`, `tonemapping`, `maxgain`, `maxexposuretime`, the same under `camera.recommended`, and
  `camera.isparamsoptimal`.
- **Areas** (write test 2026-11-27, ALPV 3.1.7): `api.cgi?api=getconfig` returns
  `{"roi":[{"id":1,"name":"roi1","type":"polygon","active":true,"angle":180000,"coordinates":[[50,50],...]},{"id":2,...}]}`;
  the corners are relative to the picture, 0 to 1000 on each axis; `angle` is the driving direction in thousandths of
  a degree; area 2 exists always and is used when `active`. A write is `POST config_json.cgi` with
  `{"api":"setconfig","config":"roi","roi":[<both areas>]}` (both always sent, as ALPV's page does); five corners were
  accepted. The `roi` and `roitwo` boxes of `config.cgi` do not follow the areas and are ignored; `getrroi` shows only
  the active area in pixels.
- **Event switches** (write test 2026-11-27): `GET vapix_events.cgi` returns
  `{"events_list":[{"New":"true"},{"Update":"true"},...]}` with the keys `AllPlates`, `New`, `Update`, `Lost`,
  `RealTime`, `PlateInView`, `NotInList`, `PlateIn`, `PlateOut`, `Roi1`, `Roi2`, `Allowlist`, `Blocklist`,
  `Customlist`. `POST vapix_events.cgi` with a list of only the changed keys changes only those; Set up sends
  `[{"New":"true"},{"Update":"true"},{"Lost":"true"}]` and never turns anything off.
- **Recommended image settings** (write test 2026-11-27): `api.cgi?api=camera&sensor=setoptimalparams` changed only
  `root.ImageSource.I0.Sensor.Contrast`, `LocalContrast`, `MaxExposureTime`, `MaxGain` and `ToneMapping` (WDR was
  already as recommended) and set `isparamsoptimal` to 1; nothing else of the 1,517 parameters. ALPV shows Max gain
  and Shutter speed in its own units (`maxgain` 51 is the parameter 100; `maxexposuretime` 1/100 is 10000 µs): the
  column compares ALPV's own `camera` and `camera.recommended` values and `isparamsoptimal`, never the parameters.
  The task reads the five parameters before, calls ALPV, reads them back and logs old and new values.
- ALPV has no version or discovery call. The fields differ between 3.0.13 and 3.1.7 (3.1.7 adds `night_mode`,
  `sensitivity` and others), which is why rule 6 exists.

## Plates

- **Plate**: the plate text (letters and digits, stored in upper case without spaces, dashes and dots; unique; 2 to
  12 characters), **Name** (up to 100 characters), **Tags** (plate tags), **Allowed from** and **Allowed to** (both
  optional, date and time in the server's time zone; Allowed to must be after Allowed from), **Delete when it ends**
  (on by default for a new plate; only with an Allowed to).
- **Delete when it ends**: the server deletes the plate within a minute after its Allowed to passed, with an audit
  entry. Off: the plate stays and is Not allowed from then on.
- **Plate tags** are the plugin's own (device tags exist only for devices): a name (up to 64 characters, unique) and
  one of the 8 tag colours, shown like device tags. At most 200. A tag in use by plates or cameras asks before it is
  deleted and is removed from them.
- **Where a plate is allowed**: on every camera that has one of the plate's tags among its allowed tags, between
  Allowed from and Allowed to. A plate without tags is allowed nowhere.
- **Import and export**: CSV. Import takes ALPV's export (`plate,date,description`: the description becomes the
  name, the date is ignored because ALPV does not say what it means) and OADM's own columns (plate, name, tags,
  allowed from, allowed to, delete when it ends). The import dialog shows new, changed and refused rows (row errors in
  the Status column) before anything is saved; tags to give every imported plate can be chosen. One transaction.
- At most 100,000 plates. Stored in the plugin's records, part of the backup. Every change is audited.

## Matching

- A read is compared only with the plates **allowed at that camera at that moment** (rule 3 and the dates). A car of
  another garage never opens this one because its plate is close to an allowed one.
- **Allowed character deviation**: Exact, 1 or 2, one setting for the server, default 1 (as ALPV). A wrong, a missing
  and an extra character each count as 1. With 2, plates shorter than 6 characters are matched with at most 1.
- **Exact match**: Allowed, at once.
- **Near match** (within the deviation): waits for the best read like an unknown plate; then Allowed with the
  plate of the list as Plate and the camera's text as Read as. Equally close to two allowed plates: Not allowed
  ("Matches more than one plate").
- **Not allowed**: the read matches a plate of the list exactly, but the plate is not allowed at this camera (no tag
  allowed here, before Allowed from, after Allowed to). Near matches to such plates do not count: Unknown plate.
- **Unknown plate**: everything else.
- The index: exact matches by a dictionary per camera tag set; near matches by a deletion index of the allowed
  plates, rebuilt per changed tag set, never a scan of the list per read.

## From events to a read

ALPV sends several events per car: New at the first detection, Update when the text, direction or area changes, Lost
after the car left. One car has one `carID`. A read is OADM's one decision per `carID` and camera.

1. New or Update with an **exact match**: the read is decided and fired at once (Allowed or Not allowed). Later
   events of the same `carID` only update the log row (direction, a better picture), they fire nothing.
2. Otherwise the events are collected. The read is decided at **Lost** or when the **wait for the best read** ends
   (from the first event; default 3 seconds, 1 to 10, a setting), whichever comes first, from the event with the
   highest `lpConfidence`. An Update with an exact match during the wait decides at once (step 1).
3. The entry of a `carID` is dropped at Lost or 60 seconds after its last event. Events never depend on Lost: a
   camera that never sends it loses nothing.
4. Events of ALPV below its own confidence threshold never arrive; OADM adds no threshold of its own.

Time in this code is a `TimeProvider`; tests drive it with a `FakeTimeProvider` and recorded event sequences.

## Events for the rules

Device events of the plugin (`IDynamicEventSource`, one set per camera that takes part, group "License Plate
Center"), all stateless:

| Event | When |
| --- | --- |
| **Plate allowed** | a read was decided Allowed |
| **Plate not allowed** | a read was decided Not allowed |
| **Unknown plate** | a read was decided Unknown plate |
| **Plate read** | every read, with the field Result (Allowed, Not allowed, Unknown plate) |

Fields of all four: Plate, Read as (empty when the same), Name, Tags (the names, comma separated, for "contains"),
Direction (In, Out, Unknown), Area (1, 2), Country, Confidence (number, 0 to 100), Vehicle type, Vehicle colour.
Plus **Camera problem** (stateful, per camera: the connection is lost for 30 s, ALPV stopped, events off) and the
server event **Any camera problem**. Opening a gate is an ordinary event rule on Plate allowed.

## The page

Rail page **License Plate Center**. The page header has the subtitle and, as the live status of the whole page, "41
of 42 cameras connected". Tabs: **Log**, **Plates**, **Cameras**, **Stats**. No card repeats the page title.

### Log

Live, newest first, virtualized, paged from the server.

- Toolbar: the device choice (the cameras of the page, default all cameras of the plugin), **Pause** (the list stops
  moving, reads are still stored), **Add to plates** (for a selected Unknown plate: the Add plate dialog with the
  plate filled in), **Export** (a menu: **PDF report**, **CSV**); right: search by plate or name.
- **Export** (decision 2026-11-27) writes exactly the rows the filters and the search show, newest first, at most
  **10,000** (`LicensePlateLimits.MaxExportRows`); with more it says "The newest 10,000 of 48,213 reads" in the file's
  head and in the save notice. Made on the server from the log store, streamed to the client's save dialog, one
  audit entry "Exported plate log" each, only reads of the caller's device group.
  - **CSV**: UTF-8 with BOM, the grid's columns plus Read as, Area, Country, Vehicle and "Time (ISO 8601)", times in
    the user's time zone.
  - **PDF report**: the look of the Logs page export (PDFsharp + MigraDoc, A4 landscape, Roboto embedded through the
    shared `LogPdfFonts`, never a system font); the head with the filters, the time range, the server name, who
    exported it when and the counts by result; the table with the plate crop when the log kept it (small, only in
    the PDF), Time, Camera, Plate (with "read as" below when it differs), Name, Tags, Result (the word, not only the
    icon), Direction, Confidence; "Page X of Y". Pictures make the file large: without pictures when more than 2,000
    rows.
- Filter row, like ALPV's Search: time (Last hour, Last 24 hours, Last 7 days, or From and To), result, tags, area,
  direction, country, vehicle (view, type, colour, make, model), **Clear filters**.
- Columns: Time, Camera, Picture (the plate crop, loaded only for visible rows), **Plate**, Name, Tags, **Result**,
  Direction, **Confidence**.
  - **Plate** is one column: the plate of the list, or the text the camera read when no plate matched. A read that
    matched with a deviation shows a warning triangle left of it; its tooltip says "Read as BN4I7HA".
  - **Result** is only the status icon (check green, no entry red, question mark grey); the tooltip names it and the
    reason ("Not allowed, ended 31 Oct 2026", "Not allowed, no tag allowed here").
  - **Confidence** is `lpConfidence` times 100 as a whole number, right-aligned; below 75 in the warning colour,
    below 60 in the error colour.
- Footer: Live or Paused, the reads of today, and the log's size ("Kept for 30 days, 3.1 of 20 GB used").

### Plates

- Toolbar: **Add plate**, **Import**, **Export**, **Change tags** (the selected plates: add and remove tags),
  **Manage tags** (names and colours), **Delete** (red, asks; names the count); right: a tag filter and the search by
  plate or name.
- Columns: Plate, Name, Tags, Allowed from, Allowed to. No status and no last read column (decision 2026-11-27). A
  plate that ended and is still there shows a warning triangle in Allowed to.
- Add and Edit dialog (one form, no card): Plate, Name, Tags, Allowed from, Allowed to, **Delete when it ends**.
  Errors below the field ("This plate is already in the list.", "Choose a time after Allowed from.").
- Double click or Enter edits; the footer shows the count and the selection.

### Cameras

A camera takes part once it was added here (**Add cameras**: the device picker; only devices with ALPV installed are
offered). Nothing is read from a camera before that.

- Toolbar: **Add cameras**, **Set up**, **Allowed tags** (the selected cameras: which plate tags are allowed there),
  **Copy settings** (the reading setup of one camera to others, see below), **Use recommended image settings**,
  **Settings**, **Remove** (red; the camera leaves the plugin, nothing on the camera changes); right: search.
- Columns: Camera, Address, Site, ALPV (version), **Status**, **Allowed tags**, **Image settings**. No last read and
  no reads today column: that is the Stats tab (decision 2026-11-27).
- **Status** (`ui:StatusChip`): Connected, Connection lost (with the time), Events are off in ALPV, ALPV is stopped,
  ALPV is not installed, ALPV has no license, ALPV 3 is needed, ALPV reads another sensor, Site offline.
- **Image settings**: ALPV compares the camera's image settings with the ones it recommends for reading plates (WDR,
  Contrast, Local contrast, Tone mapping, Max gain, Shutter speed). The column shows a check when they match and a
  warning triangle when not; the rich tooltip lists each value with the recommended one next to it. **Use
  recommended image settings** (toolbar and context menu, the selected cameras) applies them with ALPV's own call: a
  task per camera, asks first and names the count. Read with the camera's configuration, refreshed after a task and
  with **Refresh**.

### Set up (a camera)

A dialog with several sections; Save starts the task "Set up License Plate Verifier" for the camera.

- The live picture (the host's live video, first sensor) with the two **areas**: polygons of 3 to 8 corners, drawn
  and changed on the picture with the shared picture controls of the vision plugins (`plugins/Shared/Vision.Client`);
  each area has an arrow for the driving direction that counts as In, turned on the picture. Area 2 can be added and
  removed.
- **Recognition**: Region, Camera location, Plate width in pixels (a range), Number of characters (a range),
  Confidence threshold. The choices and ranges come from what the camera returned.
- **Plates**: the allowed tags of this camera.
- **Events**: whether New, Update and Lost are on (Save turns them on), and the last read.
- Corners are stored as ALPV keeps them (0 to 1000 relative to the picture), so the drawing is independent of the
  stream resolution.
- Footer: a link to ALPV's own page for barrier, relay and storage (Web View), Cancel, Save.

**Copy settings**: Recognition and the event switches of one camera to the chosen cameras, one task each; areas only
when asked, since they belong to the picture of one camera.

### Stats

For the chosen cameras and time (Today, Last 7 days, Last 30 days), from the log:

- Reads in total and by result (Allowed, Not allowed, Unknown plate), and the same by direction.
- Reads per hour (a chart).
- Per camera: reads, share Allowed, average confidence, share below 60, last read. Sorted by any column.
- Most read plates and most read unknown plates (10 each).

Counted by the log's indexes on the server, never in the client.

### Settings (dialog, Administrators)

- **Matching**: Allowed character deviation (Exact, 1, 2); Wait for the best read (seconds).
- **Log**: Keep reads for (days, default 30); Maximum size (GB, default 20); **Folder on the server**, chosen with a
  folder picker that browses the server's folders (a new shared control with its server call, Administrators only,
  folder names only; the chosen folder must pass the service's folder checks of `security.md`); Keep plate pictures
  (on by default); Keep unknown plates in the log (on by default).

## The log store

- A SQLite database of the plugin in the log folder (WAL), one row per read: time, camera, plate (the list's or the
  read text), read as, result and reason, name and tags at the time of the read, direction, area, country, confidence,
  vehicle values, picture file. Indexes on time, on plate and time, on camera and time, on result and time.
- Search by plate is from the start of the plate and uses the index; it must stay under 100 ms at 10 million reads
  (`Perf` test). Search by name runs inside the chosen time.
- Plate pictures are JPEG files in a folder per day next to the database, never in the database.
- Cleanup every hour: reads older than the days, then the oldest days until the size is under the maximum. A full
  disk or an unreachable folder stops storing, shows a problem on the page and raises Any camera problem; reads still
  fire their events.
- Writes are batched (one transaction per burst). The log is not part of a backup; the folder setting is.
- A removed camera's reads stay until the retention removes them.

## Server

- `ICorePlugin`, `IDynamicEventSource`, a task part; `siteSupport: full`. One WebSocket per camera with a filter on
  the three topics; connect 16 at once, reconnect after 1, 2, 4, 8, then 10 s, a ping every 10 s; devices that are
  Unreachable, Credentials required or Certificate changed are not contacted (the pattern of Device triggers; its
  connection code moves to `plugins/Shared` instead of a copy).
- The application list and the configuration of a camera are read when it is added, on Refresh, after a task and
  every 5 minutes for cameras with a problem; never in polling for healthy cameras.
- Reads during a lost connection are lost (only ALPV's push has a buffer). The log shows the gap as a row "Connection
  lost from 13:58 to 14:06".
- **Device groups** (decision 2026-11-27): one plate list and one tag set for the server. Editing them needs the
  permission **Manage plates**. Cameras, reads, stats and events are limited to the caller's device group
  (`call.Devices`, fail closed); the settings dialog is for Administrators.
- **Permissions**: View License Plate Center, View plate log, Export plate log, Manage plates, Set up cameras.
- **MCP and Assistant** (`ICorePlugin.Tools`): list and search plates, add, change and delete plates and tags
  (preview then run), list cameras with status, search the log (pages, never pictures), stats.
- At sites: the WebSocket and every call go through the site node; no extra limit, no pictures are streamed.

## Tests

- Matching: exact, deviation 1 and 2, short plates, two equal near matches, tags and dates, at 100,000 plates
  (`Perf`).
- Reads: recorded event sequences (New only; New and Updates; a late exact match; no Lost; two cars at once) with a
  `FakeTimeProvider`.
- Configuration parsing and writing from recorded answers of ALPV 3.0.13 and 3.1.7 (secrets removed from the
  fixtures), including a field the other version does not have.
- The page with 1,000 cameras and the plate grid with 100,000 plates; the log store at 10 million reads (`Perf`).
- Hardware writes (Set up, Use recommended image settings) are `Category=HardwareWrite`, opt-in, on the test camera
  only when asked. Test reads come from the "LPV Simulator" app on the test camera (it sends New and Update with the
  topics of ALPV and never Lost).

## Settled by the build (server, 2026-11-27)

Details the spec left open, decided while building; none changes a rule above.

- **Contract**: the methods are `LicensePlatesMethods`, the DTOs `LicensePlatesContract.cs`, the events of the page
  `reads`, `cameras`, `plates`, `status` (plugin README "Client contract").
- **Exact matches** are one lookup in the plate list, then the tag and date check (O(1)); the near-match index is per
  camera tag set as above. With 2, the plate of the list decides whether a plate is shorter than 6 characters.
- **Stats** come from two count tables of the log store written with every read (per hour; plates per day of the server's
  zone), never from a scan of the reads.
- **The log folder**: none until an Administrator chooses one (nothing is stored before, events fire); a folder inside the
  plugins' data of OADM (`plugin-data`, part of every backup) is refused, so is one OADM cannot write; a new folder starts
  a new log and the old one keeps its files. Plate crops: JPEG only, at most 512 KB.
- **Plate tag names** hold no comma (the events carry them comma separated, so does the CSV).
- **Delete when it ends** without an Allowed to is saved as off.
- **Camera problem** is also on for a camera OADM cannot contact (site offline, credentials) once that lasted 30 s; Any
  camera problem is also on for a problem of the log store.

## Open

- The order and timing of ALPV's real events (when Lost comes, how often Update, whether `carState` of an Update is
  `update` or `tracked`) are not seen on hardware yet; `ttl` 2000 suggests Lost after about 2 s. Built and tested with
  the LPV Simulator only (decision 2026-11-27); real timing waits for a site test.
- A camera with a rotated picture: ALPV's page maps the areas differently when the picture is rotated (portrait);
  OADM shows "Rotated pictures are set up on the ALPV page" until it is tested.
- The second step: writing the allowed plates to the cameras (list size limit and the meaning of ALPV's date are not
  documented).
- Audit entry of the automatic deletion (Delete when it ends): the SDK has no audit entry without a user's call, so the
  server log has it; a host member for it is needed.
- Add cameras offers "only devices with ALPV installed": the host's device picker cannot know without reading every
  device; until a cached application list exists, every camera can be added and shows "ALPV is not installed".
- The choices of Region and Camera location: ALPV's configuration returns the values, not the lists; the server checks the
  form only (Region letters, digits, `_` and `-`; Camera location alpha-3 or empty).
- The folder picker's check against the service's folder checks of `security.md` (`FolderGuard`) belongs to the host's
  picker call; the plugin checks that the folder can be written.
- The event topics (`tnsaxis:CameraApplicationPlatform/ALPV.New` and the others) and the WebSocket messages of ALPV are not
  recorded yet: the read checks used GET only.
