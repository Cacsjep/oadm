# License Plate Center

Status: **built (2026-11-27)**: the server part and the client part (`plugins/Oadm.Plugins.LicensePlates.Client`, the rail
page). The client builds against "Client contract" of the plugin README (`plugins/Oadm.Plugins.LicensePlates/README.md`).

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
  threshold, 0.25), `lprUseCase` (the region, `5` Europe; checked in ALPV's page code), `CountryPriority` (camera location,
  `AUT`; `0` none), `StatePriority` (state, empty none), `regional_settings` (`metric`, the unit system, not used by OADM),
  `bw_ldist` (ALPV's own character
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

### ALPV's lists

ALPV's page has its lists built in; the configuration holds only the codes. OADM ships the lists of ALPV 3.1.7
(`Alpv/alpv-lists-3.1.7.json` of the server part, read from ALPV's page on the test camera, 2026-11-27) and hands them to
the client part (`alpvLists`); the client keeps no copy.

- **Regions** (`lprUseCase`): name and number (Europe 5, North America 21, USA 16, ...).
- **Countries** (`CountryPriority`, the `country` of the events): name, code (alpha-3, or two letters where ALPV uses them:
  US, CA, MX, PY) and country groups. A region offers the countries with one of its groups (`regionCountryGroups`; Europe
  5 = EU and CIS). "0" is ALPV's "Select" (none).
- **States** (`StatePriority`): only for the regions 16, 17, 19 and 21, the states of the chosen country. The list of 3.1.7
  holds the states of Mexico only.
- **Vehicle types and colours**: ALPV's values ("CAR", "Gray") with OADM's names ("Car"); the log compares without case.
- **Makes and models** are not in the page: `GET /local/fflprapp/api.cgi?api=listbrands` and `api=listmodels&brand=<make>`
  (read only), read by the server on demand from a connected camera of the user's camera choice (else any connected camera
  the user sees), kept per ALPV version; the page never waits for them.

## Plates

- **Plate**: the plate text (letters and digits, stored in upper case without spaces, dashes and dots; unique; 2 to
  12 characters), **Name** (up to 100 characters), **Tags** (plate tags), **Allowed from** and **Allowed to** (both
  optional, date and time in the server's time zone; Allowed to must be after Allowed from), **Delete when it ends**
  (on by default for a new plate; only with an Allowed to).
- **Wildcard plates** (company cars, decisions of 2026-11-27): `*` in a plate stands for exactly one letter or digit
  (`AXIS12**` matches AXIS1234 and AXIS12AB, not AXIS123 or AXIS12345); no other wildcard. An ordinary plate entry (name,
  tags, Allowed from and to, Delete when it ends), shown as entered; at least 3 letters or digits besides `*` ("Use at least
  3 letters or digits besides *." below the field); unique like plates; at most 1,000 (`LicensePlateLimits.MaxWildcardPlates`).
  The Add plate dialog says below the field "* stands for one letter or digit." The Plates search finds one by its fixed
  part and by a plate it fits ("AXIS", "AXIS1234"). The CSV import and export take `*` (ALPV's export has none).
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
- **The order** (decisions of 2026-11-27): a plate entered on its own read exactly, then a wildcard plate the read fits,
  then a near match. The plate on its own decides alone (AXIS1234 ended yesterday: Not allowed, although AXIS12** would
  allow it), and a wildcard plate the read fits beats a near match to a plate on its own.
- **Exact match**: Allowed, at once.
- **Wildcard plate**: never with a deviation (its fixed characters are read exactly, the length must match); decided at
  once like an exact match. Of several, the one with more fixed characters decides; on a tie the read is Allowed when any
  of the tied plates allows it at this camera, else Not allowed with the reason of the first. The Log's Plate is what the
  camera read (the real car), the Plate cell's tooltip says "Matches AXIS12**"; Name and Tags come from the wildcard plate.
  The log column List entry (optional) and the events' field List entry name the plate or wildcard plate that decided a
  read (empty for Unknown plate). A wildcard plate is never a near match.
- **Near match** (within the deviation): waits for the best read like an unknown plate; then Allowed with the
  plate of the list as Plate and the camera's text as Read as. Equally close to two allowed plates: Not allowed
  ("Matches more than one plate").
- **Not allowed**: the read matches a plate of the list exactly, but the plate is not allowed at this camera (no tag
  allowed here, before Allowed from, after Allowed to). Near matches to such plates do not count: Unknown plate.
- **Unknown plate**: everything else.
- The index: exact matches by a dictionary per camera tag set; near matches by a deletion index of the allowed
  plates, rebuilt per changed tag set; wildcard plates by length and the positions of their first three fixed
  characters, one lookup per shape of the read's length (`WildcardIndex`); never a scan of the list per read (Perf test:
  1,000 wildcard plates among 100,000 plates).

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
  head. After a saved export the page shows no status line, only an error (decision 2026-11-27). Made on the server from the log store, streamed to the client's save dialog, one
  audit entry "Exported plate log" each, only reads of the caller's device group.
  - Both files have the columns the user chose on the Log in their order (decision 2026-11-27); times with the date
    ("27 Nov 2026 14:04:14") in the user's time zone.
  - **CSV**: UTF-8 with BOM, the chosen columns without Picture.
  - **PDF report**: the look of the Logs page export (PDFsharp + MigraDoc, A4 landscape, Roboto embedded through the
    shared `LogPdfFonts`, never a system font); the head with the filters, the time range, the server name, who
    exported it when and the counts by result; the table of the chosen columns with the plate crop when the log kept
    it (small, only in the PDF), Plate with "read as" below when it differs and Read as is not chosen, Result as the
    word; "Page X of Y". Picture is left out when there are more than 2,000 rows or more than 11 other columns (A4
    landscape has no room), and the head says so ("Without pictures: more than 2,000 reads.", "Without pictures: too
    many columns.").
- Filters, like ALPV's Search, behind the command button **Filter** of the toolbar (decision 2026-11-27): its menu lists
  time (Last hour, Last 24 hours, Last 7 days, or From and To), result, tags, area, direction, country, vehicle (view,
  type, colour, make, model). A chosen kind becomes a chip ("Country: Austria", "Last 24 hours") in one row below the
  toolbar, which exists only while a filter is set; a chip's text opens its editor in a flyout, its x removes it,
  **Clear filters** ends the row. No row with every filter. Country, vehicle type and colour are
  selects of ALPV's lists (see "ALPV's lists"); make and model are selects of what a connected camera lists.
- Columns (decision 2026-11-27): the user chooses them like on the device grid, with the shared column chooser (the
  icon-only button "Choose columns" left of the search; headers dragged to reorder); kept per user on this computer
  like the device grid's. Available: Time, Camera, Picture (the plate crop, loaded only for visible rows), **Plate**,
  Read as, List entry, Name, Tags, **Result**, Direction, Area, Country, State, Vehicle type, Vehicle colour, Vehicle make, Vehicle
  model, View (Front, Rear), **Confidence**. Shown by default: Time, Camera, Picture, Plate, Name, Tags, Result,
  Direction, Confidence.
  - **Time** has the date on every row: "27 Nov 2026 14:04:14", this computer's zone (no group header per day: a row
    stays readable alone, in a selection and in the files).
  - **Camera** is the camera's name in the plugin when the read was stored.
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
- Columns: **Name**, Address, Site, ALPV (version), **Status**, **Allowed tags**, **Image settings**.
- **Name** (decision 2026-11-27): each camera has its own name in the plugin, by default the device's name in OADM (host
  name, else address). Changed in Set up (the first field): required, at most 64 characters, unique among the plugin's
  cameras (any case; "Another camera has this name."). Kept with the camera in the plugin's records (part of the
  backup), audited ("Renamed a camera", Set up cameras), never renames the device in OADM. The Log, Stats, the events'
  Camera field, the exports and the MCP tools use it. No last read and
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

A dialog with several sections, a fixed size like every dialog of the plugin (decision 2026-11-27: `CanResize` false;
Set up, Copy settings, Settings, Plate editor, Import, Tags). Save first saves a changed **Name** (a refusal shows below
the field and nothing else is saved), then starts the task "Set up License Plate Verifier" for the camera.

- The live picture (the host's live video, first sensor) with the two **areas**: polygons of 3 to 8 corners, drawn
  and changed on the picture with the shared picture controls of the vision plugins (`plugins/Shared/Vision.Client`);
  each area has an arrow for the driving direction that counts as In, turned on the picture. Area 2 can be added and
  removed.
- **Name**: the camera's name in the plugin (see Cameras).
- **Recognition**: Region, Camera location, State (only for the regions with states), Plate width in pixels (a range),
  Number of characters (a range), Confidence threshold (the shared number with a slider, 20 to 100 %, steps of 5;
  ALPV keeps 0.2 to 1.0: 0.25 shows as 25 %; "Choose 20 to 100 %." below it). Region, Camera location and State are selects of ALPV's lists:
  the locations of the chosen region, the states of the chosen location; a new region clears a location or state it does
  not offer. A value the camera has that the lists do not know shows as its code and stays until the user picks another.
  Only the fields the camera returned show; the ranges come from what it returned.
- **Plates**: the allowed tags of this camera.
- No Events section (decision 2026-11-27): Save turns New, Update and Lost on without saying so; the Cameras tab's
  Status still says "Events are off in ALPV".
- Corners are stored as ALPV keeps them (0 to 1000 relative to the picture), so the drawing is independent of the
  stream resolution.
- Footer: a link to ALPV's own page for barrier, relay and storage (Web View), Cancel, Save.

**Copy settings**: Recognition and the event switches of one camera to the chosen cameras, one task each; areas only
when asked, since they belong to the picture of one camera.

### Stats

Built like the dashboard installers know from their plate systems (decisions 2026-11-27). Toolbar: the camera choice and
the time (Today, Last 24 hours, Last 7 days, Last 30 days), Refresh.

- Four cards for the chosen cameras, whatever the time: **Last hour**, **Last 24 hours**, **Last 7 days**, **Total**
  (all the log holds); each a count with the bar chart icon, compact ("4,812", "49.6k", "123k", "4.4M"). Allowed, Not
  allowed and Unknown plate of the chosen time are one line above the reads chart ("Allowed 1,204 · Not allowed 86 ·
  Unknown plate 211 · Average confidence 86 %"), not cards.
- **Plate confidence per camera**: one bar per camera (by name), its average plate confidence of the chosen time, 0 to
  100 %; the accent colour, below 75 the warning and below 60 the error colour (the Log's); the camera names turned
  below the bars; the tooltip names the camera, the average and the reads. With more cameras than fit the bars keep
  their width and scroll sideways (up to 1,000; only the bars in view are drawn).
- **Vehicle confidence per camera**: the same for ALPV's `vehicleConfidence`; a camera that sends none (no vehicle
  detection) has no bar, not 0; "No camera sends a vehicle confidence." when none does.
- **Reads over time** and **Confidence over time** (0 to 100 %): a line chart each, by the hour for Today and Last 24
  hours, by the day (from midnight in the user's zone) for Last 7 and 30 days; a **Total** line in the text colour and a
  line per camera in the tag colours (eight, then again). Beside each chart a legend (the shared `ChartLegend`): Total,
  then the cameras busiest first with their reads; a click shows or hides a line in both charts. Total and the 10
  busiest cameras show at first; a camera's choice stays across a reload. A step without reads has no confidence (a gap).

No reads per hour of the day, no confidence bands and no per camera table: the charts carry reads and confidence per
camera and over time (decision 2026-11-27; the share Allowed, the shares below 60 and 75 and the last read per camera are
no longer shown). No directions and no most read plates. Counted on the server from the log's hourly counts, never in
the client; the MCP stats tool (`ByDay`, `TimeZone`) returns the same series.

### Settings (dialog, Administrators)

- **Matching**: Allowed character deviation (Exact, 1, 2); Wait for the best read (seconds).
- **Log**: Keep reads for (days, default 30); Maximum size (GB, default 20); **Folder on the server**, chosen with a
  folder picker that browses the server's folders (a new shared control with its server call, Administrators only,
  folder names only; the chosen folder must pass the service's folder checks of `security.md`); Keep plate pictures
  (on by default); Keep unknown plates in the log (on by default).

## The log store

- A SQLite database of the plugin in the log folder (WAL), one row per read: time, camera, plate (the list's or the
  read text), read as, result and reason, the camera's name, name and tags at the time of the read, direction, area,
  country, state (`regionCode`), confidence, vehicle values and the vehicle confidence (null without one), picture file. Indexes on time, on plate and time, on camera and time, on result and time.
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
- **Stats** come from the hourly count table of the log store, written with every read (per hour, camera, result and
  direction: reads, confidence sum, the reads below 60, 60 to 74 and 75 to 89, the last read, the vehicle confidence sum
  and the reads with one), never from a scan of the reads. Days are the hours grouped by midnight of the caller's zone (no
  table of days: a day depends on the zone); the cards Last hour, 24 hours and 7 days are the whole hours of the counts
  plus the reads of the first, partial hour (exact). Stores of an older build get the new columns once; their hours have
  no vehicle confidence. The daily plate counts of the first build (only the top lists used them) are dropped from older stores.
- **The log folder**: none until an Administrator chooses one (nothing is stored before, events fire); a folder inside the
  plugins' data of OADM (`plugin-data`, part of every backup) is refused, so is one OADM cannot write; then every save with
  a folder runs the host's folder checks of `security.md` "Folders a plugin writes to" (`ICorePluginContext.FolderCheck`,
  the picker's rules, also for an unchanged folder) and refuses with their text below the field; a new folder starts a new
  log and the old one keeps its files. Plate crops: JPEG only, at most 512 KB.
- **Audit of Delete when it ends**: one entry per sweep, "Deleted 3 plates whose time ended", user `system`
  (`ICorePluginContext.Audit`, SDK 0.17.0); never the plates, the server log names them.
- **Plate tag names** hold no comma (the events carry them comma separated, so does the CSV).
- **Delete when it ends** without an Allowed to is saved as off.
- **Camera problem** is also on for a camera OADM cannot contact (site offline, credentials) once that lasted 30 s; Any
  camera problem is also on for a problem of the log store.

## Settled by the build (client, 2026-11-27)

Details the spec left open, decided while building the page; none changes a rule above.

- **Shared controls**: the tag chips, the tag colour picker and the palette moved from the client host into the client SDK
  (`TagChipRow`, `TagChips`, `TagColorPicker`, `TagColors`, SDK 0.17.0), so plate tags look like device tags; the tags
  dialog is laid out like the device tags dialog. Change tags (plates), Allowed tags (cameras) and Manage tags are one
  dialog: a check box per tag for all, some or none of the selected; OK sends only the clicked tags as add and remove.
- **Log**: the camera choice is the shared page choice (default All cameras of the plugin, kept on this computer) and is
  shared with Stats. The filters are the shared filter chips (`ui:FilterButton`, `ui:FilterBar`, `FilterSet`): the time
  editor is the date range panel of the Logs page (Last hour, Today, Last 24 hours, Last 7 days, All, Custom with From and
  To); Result and Tags are check boxes; Area, Direction and Country one choice each (a choice closes the editor; type to
  jump to a country); the Vehicle editor has View, Type, Colour, Make and Model, its first opening reads the makes, a make
  its models. A chip whose editor closes without a value goes away; a filter set elsewhere gets its chip. The search
  reads 300 ms after the last key; Clear filters leaves it. Pages of 200 rows; the next older page is read when a row
  20 from the end shows; newer rows on every `reads` event of a shown camera; crops read four at a time.
- **Log columns**: a gap row shows the warning icon in Plate and "Connection lost from 13:58 to 14:06" in Name. The deviation
  triangle and the ended triangle of Plates are the theme's warning icon left of the text. Result's icons: the OK check,
  the error colour with the no-entry icon, the neutral question mark.
- **Export** is the shared Export button of the Logs page (its menu: CSV, PDF); no notice after a saved file, only an
  error. The request carries the chosen columns in their order (`LogExportRequest.Columns`, `LogColumns` keys; unknown
  keys dropped, none: the defaults). Times in this computer's time zone.
- **Columns** of the Log are the shared `GridColumnLayout` of the device grid (extracted into the client SDK with
  `GridColumnBinder` and `ColumnChooserButton`), kept under the local setting `logColumns`; Picture keeps its width. Like
  the device grid, every column is a star column with a minimum that fits its header and content (Picture a fixed 136 px):
  the stars share only the room left over, and with more than fit (all 18 at 1600 px) the grid scrolls sideways.
- **Plates** are read in pages of 5,000 (the first shows at once) and searched and filtered in memory: the plate from its
  start with spaces, dashes and dots ignored, or the name; the tag filter "All tags" or one tag. Ended means Allowed to
  before this computer's clock (the server's zone is not known to the client). A day without a time is 00:00 for Allowed
  from and 23:59 for Allowed to. The editor runs the server's `PlateRules`, so its errors match the server's.
- **Import**: the file is uploaded once; changing the tags for every plate makes a new preview; refused rows come first.
- **Cameras**: Status chips: Connected ok; Connecting running; Connection lost ("since 13:58"), Events are off in ALPV and
  Site offline warnings; the ALPV problems and the device problems errors. Add cameras offers every camera of the device
  list that is not in the plugin (see "Open"). Refresh reads the selected cameras, all without a selection. Use recommended
  image settings and Remove ask with the count; Set up and Copy settings need one selected camera; a double click sets up.
  The Image settings tooltip is the wide tooltip: the setting, a warning icon when it differs, the camera's value, the
  recommended one.
- **Set up**: the live video without channel switch, fullscreen, quality switch or controls, channel 1. The areas are
  drawn on a 16:9 picture (the stream size is not in the Set up read), each with its tag and an arrow from its middle; the
  direction is degrees clockwise from up (0 up, 180 down), ALPV's `angle` divided by 1000. Area 1 is the accent colour,
  area 2 teal; a new area 2 takes ALPV's inactive area 2 or a box in the right half. Save sends the areas only when they
  changed, only the changed Recognition values, the allowed tags only when changed.
- **Stats**: the cards are `ui:StatCard` with `IconKey` barChart; the bars are the shared `BarChart` with `MinSlotWidth`
  28 and turned labels, sorted by camera name; the lines are the shared `LineChart` with `Start` and the shared
  `ChartLegend` (one legend list, the same items for both charts). Last 7 and 30 days start at midnight 6 and 29 days
  ago. The reply grows with the cameras (at most 1,000, a value per step each), never with the reads.
- **Plate crops**: the magenta line at the right edge of some crops (W19925L, BN805RT, read on .48 from the LPV
  Simulator) is in the JPEG ALPV sends: the last pixel column of every stored crop checked is about RGB 255, 123, 255;
  OADM shows the picture as it is.
- **Selection**: a grid that gets new rows (after Remove, a reload, a filter) keeps the selection it shows itself and hands
  it to the page; the page never puts back a selection the grid no longer shows (the toolbar stayed off after Remove).
- **Settings**: the folder is optional (without one nothing is stored); the number limits of the dialog are Wait 1 to 10 s,
  Keep reads 1 to 3,650 days, Maximum size 1 to 10,000 GB; the server's refusal shows below its field.

## Open

- The order and timing of ALPV's real events (when Lost comes, how often Update, whether `carState` of an Update is
  `update` or `tracked`) are not seen on hardware yet; `ttl` 2000 suggests Lost after about 2 s. Built and tested with
  the LPV Simulator only (decision 2026-11-27); real timing waits for a site test.
- A camera with a rotated picture: ALPV's page maps the areas differently when the picture is rotated (portrait);
  OADM shows "Rotated pictures are set up on the ALPV page" until it is tested.
- The second step: writing the allowed plates to the cameras (list size limit and the meaning of ALPV's date are not
  documented).
- Add cameras offers "only devices with ALPV installed": the host's device picker cannot know without reading every
  device; until a cached application list exists, every camera can be added and shows "ALPV is not installed".
- The event topics (`tnsaxis:CameraApplicationPlatform/ALPV.New` and the others) and the WebSocket messages of ALPV are not
  recorded yet: the read checks used GET only.
- The Export menu says CSV and PDF (the shared Export button of the Logs page), not "PDF report" and "CSV" as decided on
  2026-11-27: a different text on this page only would be two phrases for one concept; to be decided by the maintainer.
- The direction of an area (`angle`): 0 as up and clockwise is assumed, not checked against ALPV's page on hardware; the
  picture's shape is taken as 16:9 (a 4:3 stream would shift the drawn areas against the video).
- A read that changes after it was shown (a better picture, the direction) keeps its row until the log is read again: the
  live read asks only for rows newer than the newest shown.
