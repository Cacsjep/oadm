# Image Health Dashboard

Core plugin `oadm.image-health`, rail page **Image Health Dashboard** (group Monitoring). One table with the status of
AXIS Image Health Analytics (AIHA) on every camera that has the app. Off by default (`plugin.json`
`enabledByDefault: false`); an administrator turns it on on the Plugins page. Read-only for devices.

## Device API

`GET /local/AXISImageHealthAnalytics/v1/status` with the stored credentials. Verified on an AXIS Q3548-LVE (AXIS OS
12.11.118, AIHA 3.2.2):

| Answer | Meaning |
|---|---|
| 200, JSON | The app runs: `{"block":"normal","blur":"detected","redirect":"pending","status":"none","status_criticity":"info","under-exposure":"normal","unsuitability":"suitable"}` |
| 503, HTML | The app is installed but stopped |
| 404 | The app is not installed |
| 401 | No or wrong credentials |

Detection values: `normal`, `pending`, `detected`, `disabled`; `unsuitability`: `suitable`, `unsuitable`. The page shows
OK, Pending, Detected and Off; another value as the camera sent it (`DetectionStates.FromRaw`). `status` and `status_criticity` are not shown.
Recorded answers: `tests/Oadm.Plugins.ImageHealth.Tests/Fixtures/q3548-status-*.json`.

## Decisions

- Only on request: one check when the page opens, **Refresh**, and **Auto refresh** (off by default) every 10 s while
  the page is open. No background polling.
- 24 cameras at a time, 3 s per request.
- A camera with the app stopped stays in the table as **Not running**; cameras without the app are only counted.
- Five count cards above the table (`ImageHealthCounts`, shared `ui:StatCard`): Good, Detected, Pending,
  Detection off, Errors. Each camera with the app counts in one of Good, Detected, Pending, Errors, the worst state wins
  (Errors > Detected > Pending > Good); a detection turned off is no problem, such a camera is Good, and
  Detection off counts the checked cameras with a detection off on top; Not running and could not be checked count as Errors (App column red for both). The cards count every
  camera, not the search; the summary line keeps only "without the app", "N shown" and the check time.
- A live display only: no background check and no events. Event rules use the app's own events through Device
  triggers (`Image Health Analytics - Blur` ... `Any-Active-Events`, on/off). An old stored plugin setting `config` is
  ignored.

## How it works

- **Server** (`Monitoring/ImageHealthMonitor`): `check` starts a check unless one runs and returns the state at once;
  `getState` returns it without a request. A check lists the video devices (`HasVideo`), asks each once
  (`Parallel.ForEachAsync`, `CheckParallelism` 24, `RequestTimeout` 3 s), keeps rows in memory between checks and
  updates them in place: running (five detections), Not running (503, detections cleared), error (refused status
  without a request: Credentials required, Password not set, Certificate changed, Unreachable; or the HTTP / timeout /
  network text of `DeviceMessages`); 404 = without the app (counted, row removed); devices removed from OADM leave.
  `ChangedUtc` = when a detection changed between two answers of the running app. Events (500 ms): `rows`
  ({rows, removed}) and `state` (checking, checked, total, running, notRunning, withoutApp, failed, checkedUtc).
- **Page** (`HasOwnCards`, subtitle "AXIS Image Health Analytics on every camera."): a row of five count cards
  (`ui:StatCard`): **Good** (ok, "of 12 cameras"), **Detected** (error), **Pending** (warning),
  **Detection off** (neutral), each with "Blur 2 · Block 1", and **Errors** (error, "1 not running · 2 could not be
  checked"); every camera with the app counts in exactly one of Good, Detected, Pending and Errors, the worst state wins
  (Errors > Detected > Pending > Good; a detection turned off in the app is no problem, such a
  camera is Good, and **Detection off** counts the checked cameras with a detection off on top, `ImageHealthCounts`, O(n)), colored only while the count is above 0, all cameras regardless of the search. Below
  them the table card: Refresh, Auto refresh, SearchBox; progress row "Checking cameras 120 of 200"; summary "2 cameras
  without the app · 5 shown · checked 19:34:14"; virtualized DataGrid (`wrapRows`)
  Address, Model, Blur, Block, Redirect, Under-exposure, Unsuitability (status chips, sortable by severity), Last change,
  App (Running ok, Not running red, Checking accent, error text red). Empty: "No camera has AXIS Image Health
  Analytics." Rows updated in place by device id; search, new and removed rows one reset (O(n), 5,000 tested).
- Not in fake mode yet.
- Tests: `tests/Oadm.Plugins.ImageHealth.Tests` (the three recorded answers, disabled / unsuitable / other values,
  check lifecycle: no request without a check, Not running, removed app, changes timed, one check at a time, 24 at a
  time with 200 cameras, failures; view model: one check on show, Auto refresh off by default and only while shown,
  5,000 rows; headless `image-health-dashboard.png`; read-only hardware test `ImageHealthHardwareTests` through the
  in-process server and the real page over gRPC, using a development camera that runs AIHA, renders
  `image-health-dashboard-live.png`).

## Code

| File | What |
|---|---|
| `ImageHealthPlugin.cs` | `ICorePlugin`; methods `check` and `getState` (Operator, not audited) |
| `Monitoring/ImageHealthMonitor.cs` | One check at a time over every video device, rows kept between checks, events `rows` and `state` |
| `Monitoring/AihaParsing.cs` | The status JSON |
| `Shared/ImageHealthModels.cs` | Contract shared with the page |
| `../Oadm.Plugins.ImageHealth.Client/` | Page, view model, rows |

## Manual test

1. Turn the plugin on on the Plugins page.
2. Open the page with a camera running AIHA: one row, five detections, App "Running".
3. Cover the lens (or point the camera elsewhere) and press Refresh: Pending, then Detected; Last change is set.
4. Stop the app on the camera, Refresh: App "Not running".
5. Turn on Auto refresh: the summary's "checked" time moves every 10 s; leave the page: no more requests (server log).
