# 30,000 devices

Status: draft for approval. Nothing below is implemented yet.

OADM supports at most **30,000 devices** per server. This spec lists what changes so that 30,000 devices work without
lag, freezes, refused runs or running out of memory, also when none of them answers, and the numbers it is based on.
Measured on 2026-10-15 with OADM 3.1.0 (24 cores, Windows, SQLite on a local disk); the measuring tests are listed at
the end.

## Decisions

| Topic | Decision |
|---|---|
| Device limit | 30,000 devices per server, a hard limit, in the docs and as a popup when an add goes over it |
| Status poll timeout | 3 s (today 15 s), status poll only |
| Status polls at once | a fixed cap of 200 (today 16), no ladder, no setting |
| Unreachable | a poll that times out is asked once more in the same round, except in an outage (more than 10 % fail) |
| Certificate | unchanged: a changed certificate shows on the status poll; expiry and trust come from the full refresh |
| Full refresh interval | stays the setting `Polling.FullRefreshMinutes` (default 10; 10 or 30 minutes both fit 30,000) |
| Full refresh at once | spread over the interval like the polls, cap 64 (today 8) |
| Full refresh skips | devices that are not OK |
| Full refresh extra | at once when the status poll sees a new firmware version or model |
| Server memory | the device list is read in pages; identical API lists are one object in memory |
| Pages that read every camera | a device selection first (the shared device picker) |
| Per-run limits | the device limit (30,000), except System report: 50 |
| Device triggers | at most 10,000 devices for all triggers together (one permanent connection per device) |
| Loading | a loading indicator wherever something can take longer than 1 s |

## 1. Device limit

- One constant, `DeviceLimits.MaxDevices` = 30,000 (SDK, so plugins can use it).
- The server refuses an add that would take it over the limit, in one place (`DeviceAdder`), so every way in is covered:
  Discovery, Network range, Add manually, Import from file, Import from ADM (`AddDevicesService.Commit`) and the DHCP
  server's automatic add (`IDeviceAutoAdd.AddAsync`). Nothing of that add is stored.
- `Commit` answers RESOURCE_EXHAUSTED with the shared text "OADM manages at most 30,000 devices. Remove devices first, or
  add fewer." The add page shows it as a popup (`ui:MessageWindow`) and stays open, so the user can uncheck devices.
- The automatic add logs it as a warning (at most once an hour), no audit entry.
- User docs: the limit on the requirements page and on the add pages.
- Tests: Commit at and over the limit, the automatic add over the limit, the popup.

## 2. Status poll

- Timeout of the status poll request 3 s (`VapixRequestOptions.Timeout` on that request). Every other request keeps its
  timeout (15 s; uploads minutes).
- At most 200 polls at once (`MaxParallelism`). The round still spreads its starts over 90 % of the interval, so the polls
  really running are about polls per second x time per poll (about 40 at 30,000 devices answering in 70 ms); 200 only
  matters while many devices are slow or do not answer.
- A poll that fails with a timeout or a refused connection is polled once more at the end of the round; only a second
  failure sets Unreachable. Other results (wrong password, certificate changed, factory default) count at once.
- **Outage**: when more than 10 % of the round's polls failed that way, the second tries are skipped and those devices go
  Unreachable at once (a second try would double the round, see section 9).
- Unchanged: the row is read right before its poll, LastSeen is written, an unchanged poll is not published, a failed poll
  is logged at Debug only.
- Tests: the timeout, the second try (one miss stays Ok, two misses Unreachable), no second try in an outage, the cap.

## 3. Full refresh

- Interval: the setting stays (default 10 minutes, 1..1440).
- Spread over the interval like the status poll (each device's start fixed by its place in the list, as the first
  refresh after a server start already is), at most 64 at once (today `MaxParallelism / 2` = 8).
- Devices that are not OK are skipped by the schedule; the status poll queues a refresh when a device is Ok again.
- The status poll queues a refresh at once when it sees a firmware version or model different from the stored one.
- The refresh after a finished task stays, through the same queue (deduplicated, capped).
- Tests: the spread, the cap, skipping devices that are not OK, the firmware trigger.

## 4. Server memory

The real server process with 30,000 devices uses 1.2 GB right after its start and 5 GB after five clients loaded the
device list; with its heap limited to 1 GB it runs out of memory on the first client's device list. Cause: the device
list (`DeviceGrpcService.Watch`), the event rules engine and the task menu cache load every device at once, and every
device carries its own copy of its API list (about 100 entries): 3 million small objects for 30,000 devices.

- **API lists shared**: devices with the same list (same model and firmware) share one list object. The database value
  converter keeps the lists it read by their JSON text (at most 2,000 different lists) and hands out the same object.
- **Device list in pages**: the Watch snapshot reads the devices 1,000 at a time (ordered by serial, as today) and writes
  each page before it reads the next. The legacy `DeviceService.List` does the same into its one message.
- **The other full reads** (event rules engine, task menu cache, re-find) use the shared API lists; no further change.
- Acceptance: the standalone test at 30,000 with the server heap limited to 1 GB serves five clients at once.

## 5. Pages that read every camera: a device selection

Today these pages read every managed camera when they open or on Refresh:

| Page | Today |
|---|---|
| Action Rule Editor | rules of every device, on open and Refresh |
| Edge storage | the SD card of every device with a card slot, on open and Refresh |
| Image Health Dashboard | every camera with the app, on open, Refresh and Auto refresh |
| Hardening scan | scans every managed device |
| Snapshot report | snapshots of every video device on Create snapshots |

Change, the same on each page:

- The purple "Selected devices" button at the top of the page (the one of the event rules, VAPIX Commander and Device
  triggers), opening the shared device picker with All devices, tags and chosen devices.
- Starting choice: the Devices page selection when there is one, else the page's last choice (kept per client and page),
  else nothing ("Choose devices"; the page reads nothing and says so in one line).
- The page reads only the chosen devices; the picker always lists every device, so the user can choose more.
- A tag means its devices at the moment the page reads.
- Devices a page cannot use (e.g. Edge storage without a card slot) are greyed in the picker with the reason.
- Measured at 30,000: the picker opens in 0.1 s, renders in 0.1 s, the slowest search keystroke takes 44 ms, select all
  5 ms, checking a tag 25 ms.

**Single-camera pages**: Metadata Monitor and I/O Monitor choose their camera in a drop-down with every device (no
search; 30,000 entries cannot be scrolled). Both get the shared picker in single-device mode
(`DevicePickerWindow.ShowOneAsync`, with search) behind a button that names the chosen camera.

**Every existing device selector at 30,000**, each with a test at 30,000 (opening, search, select all, tags):

| Where | Selector |
|---|---|
| Event rules (conditions, actions) | shared device picker |
| VAPIX Commander (target devices) | shared device picker |
| Device triggers (trigger devices) | shared device picker |
| Edge Recording Export (cameras) | shared device picker |
| Action Rule Editor (New rule, Copy to devices) | shared device picker |
| Configure devices (source device) | shared device picker |
| Web View, Live View (camera list) | shared device tree |
| Metadata Monitor, I/O Monitor | drop-down, becomes the picker (above) |
| Task dialogs with a row per selected device (Assign IP address, Network settings, Upgrade firmware, Users, Date and time, Applications) | their grids, today tested at 5,000 |

## 6. Per-run limits

| Where | Today | New |
|---|---|---|
| Action Rule Editor (`MaxDevicesPerRun`) | 5,000 | 30,000 |
| Device triggers (`MaxDevicesPerTrigger`) | 5,000 per trigger | 10,000 devices for all triggers together (below) |
| Edge storage (`MaxDevicesPerRun`) | 5,000 | 30,000 |
| RTSP port (`MaxPortDevices`) | 5,000 | 30,000 |
| Hardening scan (`MaxStoredDevices`) | 10,000 | 30,000 |
| System report (`MaxDevices`) | 5,000 | **50** (each report is up to 256 MB on the server's disk) |

All but System report and Device triggers use `DeviceLimits.MaxDevices`. Texts: "Choose 1 to 30,000 devices.", System
report "Choose at most 50 devices."

**Device triggers: 10,000 devices.** Device triggers keeps one permanent connection per device. Windows gives a program
16,384 local ports for outgoing connections by default (49152-65535), and polls, tasks, live views and the Web View need
ports too, so 30,000 permanent connections do not fit. The limit counts the devices of all triggers together (a device
used by several triggers counts once, it has one connection):
- Saving a trigger that would take the total over 10,000 is refused below the device choice: "Device triggers watch at
  most 10,000 devices. Choose tags or fewer devices." (`DeviceTriggersPluginInfo.MaxWatchedDevices`).
- A trigger with All devices or a tag that grows past the limit later (devices added) keeps the devices it watches and
  shows the problem "More than 10,000 devices: the newest are not watched." on its row; nothing is dropped silently.
- Tests: the save refusal, a tag that grows past the limit, the count across triggers.

## 7. Server speed

- **Tags**: `DeviceTagStore.RewriteAsync` keeps every saved device tracked across its batches of 500, so each
  `SaveChanges` checks all earlier ones. Clear the change tracker after each batch (as `EfTaskStore.AddRangeAsync` does)
  or update only the Tags column.
- **Task runs**: the task engine alone stays flat per task (2.7 ms at 5,000, 2.4 ms at 30,000); the full server grows
  (3.2 ms at 5,000, 4.4 ms at 25,000, 6.9 ms at 50,000). Measure what the server adds per finished task (the queued full
  refresh, the change feed to watchers, event rules) and fix the part that grows; the capped refresh queue of section 3
  is the first candidate.
- **Task menu**: its first call after a device change takes 4.5 s at 30,000 (then 6-12 ms from the cache); the shared API
  lists of section 4 shorten it. To measure again after section 4.

## 8. Client

- **Sort the device grid**: 2.9 s at 30,000 with the window frozen. Sort once in the view model (one comparer, one
  `ReplaceAll`) instead of in the grid's collection view.
- **Group by tag**: select all takes 2.4 s and 1,000 tag changes freeze the window for 13.9 s at 30,000. Rebuild the
  group rows once per burst instead of regrouping per change, and select through the device rows.
- Everything else measured stays fast (see Numbers).
- The scale rule of CLAUDE.md moves from 5,000 to 30,000 devices: every page with a device list gets its test at 30,000.

## 9. Worst case: no device answers

The server's network, a VPN or a site link is down and none of the 30,000 devices answers.

- **Status poll**: every poll ends after 3 s, 200 at once, no second tries (section 2): one round marks all 30,000
  Unreachable in 7.6 minutes (measured: 454 s, 0.06 cores, 4 MB). Today (15 s, 16 at once) the same takes about 7.8 hours.
- **Logs**: a failed poll stays at Debug, so the log files do not fill. New: one warning when more than half of a round
  failed ("Most devices do not answer. Check the server's network.") and one when they answer again.
- **Full refresh**: skips devices that are not OK, so it costs nothing during the outage.
- **Event rules**: "Device unreachable" turns on for every device; rules on it are bounded by their limits (Max per
  minute, default 60; "Rule paused" logged once a minute); the event log gets one entry per device (30,000 of its
  100,000).
- **Client**: the 30,000 status changes arrive in batches (every device changing at once: 0.4 s at 30,000, measured).
- **Re-find**: one 15 s zero-conf session every 5 minutes for all unreachable devices, no per-device work.
- **Device triggers**: every WebSocket drops and reconnects with 1, 2, 4, 8, then 10 s between tries, 16 connecting at
  once. New: the connect uses the 3 s timeout; "connection lost" turns on per trigger after 30 s as today.
- **Back online**: one poll round sets all 30,000 Ok (measured: 23 s) and each queues a full refresh: about 3 minutes
  through the cap of 64 (measured: 169 refreshes per second).
- **Tasks**: tasks on unreachable devices fail after the timeouts of their own steps, as today.

Device triggers watch at most 10,000 devices (section 6), so the reconnect sweep in an outage takes at most
10,000 x 3 s / 16 = 31 minutes.

## 10. Not done

- Storing the certificate from every poll, a growing refresh interval, a refresh after a Web View tab closes.
- Batching the LastSeen writes and sending API lists once per firmware over gRPC: only needed above about 40,000 devices.

## 11. Loading feedback

Rule (into CLAUDE.md, Coding Rules): everything that can take longer than 1 s at 30,000 devices shows that it is working,
from the first moment: a page or dialog that waits for a read `ui:LoadingOverlay` (ring and text, Cancel stays usable), a
list or card that reloads the `ui:ProgressRow` or a `ui:ProgressRing` in place, a button that started the work disabled
with its row showing progress. The window never freezes without one; work that would freeze it moves off the UI thread
first (section 8).

Places found by the measurements (at 30,000; to be checked one by one, the audit adds the rest):

| Place | Measured | Indicator |
|---|---|---|
| Connect, device list arrives | 5.2 s | the splash and the Devices page while the first list loads |
| Sort the device grid | 2.9 s | after section 8 under 1 s; until then a ring over the grid |
| Group by tag on, select all grouped | 0.7 s, 2.4 s | ring over the grid |
| Context menu and toolbar, first task menu after a device change | 4.5 s | the task entries show "Loading" until the menu arrives |
| Tags dialog OK on every device | 10.8 s | progress row in the dialog, OK disabled |
| Remove every device | 5.9 s | progress row in the confirmation, then the grid |
| Run on every device | 2.2 s | the toolbar or dialog button disabled with a ring until the reply |
| Delete all tasks | 1.9 s | ring in the tasks pane |
| Export devices | to measure | progress row until the file is written |
| The five pages of section 5 while they read | minutes | `ui:LoadingOverlay` first, then the table fills with a progress row |
| Device picker, device tree | 0.1 s | none needed |

- Audit: every `InvokeAsync`, gRPC call and long loop of the client and the plugin pages is checked at 30,000 devices;
  each that can pass 1 s gets an indicator and a test that the indicator shows.

## 12. Plan

Each phase ends with the limits tests at 30,000 and its numbers in this file.

1. **Limits**: the device limit with its popup, run limits 30,000, System report 50, Device triggers 10,000, docs.
2. **Polling**: status poll (3 s, 200, second try, outage, warnings), full refresh (spread, 64, skip not OK, firmware).
3. **Server memory**: shared API lists, the device list in pages; the 1 GB acceptance test; the task menu again.
4. **Server speed**: tags; the per-task cost of task runs.
5. **Client**: sort and group by tag; the loading rule into CLAUDE.md and the indicators of section 11, then the audit.
6. **Device selectors**: the selection on the five pages (one page at a time); the picker for Metadata Monitor and
   I/O Monitor; every existing selector and task dialog grid of section 5 tested at 30,000 with search, fixes where one
   is slow.
7. **Wrap-up**: the CLAUDE.md scale rule from 5,000 to 30,000 with the existing scale tests raised, the plugin READMEs and
   user docs, the changelog, the limits tests committed.

## Numbers

### Cameras

Four dev cameras (AXIS P3265-V and three more on AXIS OS 10-12, local network): status poll 28-80 ms; a new connection
with a full TLS handshake 140-350 ms; full refresh 180-520 ms. They close an idle connection after 5-10 s, so at a 60 s
interval every poll opens a new connection (a resumed TLS session answers in about 40 ms).

### Status poll and full refresh

One round of 25,000 simulated devices answering in 70 ms, 1 % never answering, through the real polling code and SQLite:

| Polls at once | Round (3 s timeout) | Polls per second |
|---|---|---|
| 16 (today) | 169 s | 148 |
| 32 | 96 s | 261 |
| 64 | 59 s | 424 |
| 128 | 28 s | 884 |
| 200 | 25 s | 1,009 |
| 256 | 25 s | 989 |

With today's 15 s timeout: 105 s at 64, 43 s at 200, about 6 minutes at 16 (calculated). Above about 128 the database is
the limit (every poll writes LastSeen in its own transaction): 1,100-1,470 polls per second with devices that answer at
once.

30,000 devices with the proposed settings: one round in 36 s (838 polls per second; a 60 s interval needs 556). Full
refresh of the 29,700 OK devices, 5 requests of 70 ms each, 64 at once: 175 s (169 per second; needed 50 per second at 10
minutes, 17 at 30 minutes).

New TLS connection per poll at the rate of 30,000 devices (556 per second, loopback fake cameras, the real VAPIX client
with certificate pinning; server and client ends together): 2.3-2.4 ms CPU per poll with full handshakes, 1.7-2.1 ms
with resumed sessions, about 1.0-1.3 cores of 24, no failures; RSA 2048 and ECDSA P-256 alike.

### Server

| | 5,000 | 25,000 | 30,000 | 50,000 |
|---|---|---|---|---|
| Database file | 43 MB | 213 MB | | 425 MB |
| Device list to a client (before compression) | 23 MB, 1.8 s | 115 MB, 4.5 s | 5.2 s | 229 MB, 8.8 s |
| Task menu (compact), first call | 0.9 s | 3.8 s | 4.5 s | 8.2 s |
| Task menu reply (compact) | 75 KB | 372 KB | | 743 KB |
| Run on every device (reply) | 0.6 s | 2.2 s | | 5.7 s |
| Every task finished (do-nothing plugin) | 16 s | 110 s | | 345 s |
| Tag every device (3 tags) | 2.0 s | 10.8 s | | 30.2 s |
| Remove every device | 1.2 s | 5.9 s | | 10.8 s |

Standalone server process (Oadm.Server.dll with every plugin of the build, no device contacted):

| | 5,000 | 30,000 |
|---|---|---|
| Ready after start | 9.5 s | 5.6 s |
| Idle, 10 s after start | 312 MB | 1,229 MB |
| After one device list | 487 MB | 1,528 MB |
| After five device lists at once | 1,034 MB | 5,028 MB (still 5,056 MB 30 s later) |
| Workstation GC instead of server GC | | 4,316 MB after five lists |
| Heap limited to 1 GB | | out of memory on the first device list |

### Client

| | 5,000 | 25,000 | 30,000 | 50,000 |
|---|---|---|---|---|
| Device list arrives | 88 ms | 475 ms | 483 ms | 594 ms |
| First render of the grid | 1.4 s (first run) | 0.3 s | 1.1 s | 0.3 s |
| Search, slowest keystroke | 77 ms | 59 ms | 77 ms | 58 ms |
| Select all | 10 ms | 32 ms | 54 ms | 37 ms |
| Sort by Model | 0.5 s | 2.2 s | 2.9 s | 9.3 s |
| Every device changes at once | 141 ms | 324 ms | 401 ms | 561 ms |
| Group by tag on | 0.6 s | 0.3 s | 0.7 s | 0.5 s |
| Select all, grouped | 0.4 s | 0.5 s | 2.4 s | 2.1 s |
| 1,000 tag changes while grouped | 1.3 s | 7.1 s | 13.9 s | 27.6 s |
| Memory with the page open | 58 MB | 128 MB | 147 MB | 218 MB |
| Tasks pane, a run on every device | 38 ms | 108 ms | 192 ms | 316 ms |

At 30,000 also: device selector (section 5), device tree of Web View and Live View (builds in 68 ms, 52,511 rows, slowest
search keystroke 62 ms), add page (30,000 found and logged in 275 ms, select all 204 ms, slowest search keystroke 6 ms).

## Measuring tests

Opt-in: they run only with `OADM_LIMITS_DEVICES` set (e.g. `5000,30000`) and write their numbers without a time budget.

- `tests/Oadm.Core.Tests/Perf/LimitsTests.cs`: polling and database per size, the poll ladder, the proposed settings and
  the worst case at 30,000, the task engine alone; read-only on the dev cameras: poll and refresh times, connection
  lifetime.
- `tests/Oadm.Server.Tests/Perf/ServerLimitsTests.cs`: gRPC calls per size.
- `tests/Oadm.Server.Tests/Perf/TlsLimitsTests.cs`: CPU of a new TLS connection per poll.
- `tests/Oadm.Server.Tests/Perf/StandaloneServerLimitsTests.cs`: the real server process, its memory
  (`OADM_LIMITS_SERVER_HEAP` limits its heap).
- `tests/Oadm.Client.Tests/Perf/ClientLimitsTests.cs`: the Devices page, the device selector, the device tree, the add
  page, the tasks pane.
