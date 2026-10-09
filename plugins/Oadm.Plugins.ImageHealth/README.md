# Image Health Dashboard

Core plugin `oadm.image-health`, rail page **Image Health Dashboard** (group Monitoring). One table with the status of
AXIS Image Health Analytics (AIHA) on every camera that has the app. Off by default (`plugin.json`
`enabledByDefault: false`); an administrator turns it on under Settings > Plugins. Read-only for devices.

## Device API

`GET /local/AXISImageHealthAnalytics/v1/status` with the stored credentials. Verified on an AXIS Q3548-LVE (AXIS OS
12.11.118, AIHA 3.2.2) on 2026-10-09:

| Answer | Meaning |
|---|---|
| 200, JSON | The app runs: `{"block":"normal","blur":"detected","redirect":"pending","status":"none","status_criticity":"info","under-exposure":"normal","unsuitability":"suitable"}` |
| 503, HTML | The app is installed but stopped |
| 404 | The app is not installed |
| 401 | No or wrong credentials |

Detection values: `normal`, `pending`, `detected`, `disabled`; `unsuitability`: `suitable`, `unsuitable`. The page shows
OK, Pending, Detected and Off; another value as the camera sent it. `status` and `status_criticity` are not shown.
Recorded answers: `tests/Oadm.Plugins.ImageHealth.Tests/Fixtures/q3548-status-*.json`.

An earlier version read the RTSP event stream (`tnsaxis:CameraApplicationPlatform/AXISImageHealthAnalytics/<Blur|Block|
Redirect|Under-Exposure|Unsuitability|Any-Active-Events>`, data `active` 0/1). The status request is simpler, needs no
open connection and also reports Pending and disabled detections.

## Decisions (user, 2026-10-09)

- Only on request: one check when the page opens, **Refresh**, and **Auto refresh** (off by default) every 10 s while
  the page is open. No background polling.
- 24 cameras at a time, 3 s per request.
- A camera with the app stopped stays in the table as **Not running**; cameras without the app are only counted.

## Code

| File | What |
|---|---|
| `ImageHealthPlugin.cs` | `ICorePlugin`; methods `check` and `getState` (Operator, not audited) |
| `Monitoring/ImageHealthMonitor.cs` | One check at a time over every video device, rows kept between checks, events `rows` and `state` |
| `Monitoring/AihaParsing.cs` | The status JSON |
| `Shared/ImageHealthModels.cs` | Contract shared with the page |
| `../Oadm.Plugins.ImageHealth.Client/` | Page, view model, rows |

## Manual test

1. Turn the plugin on under Settings > Plugins.
2. Open the page with a camera running AIHA: one row, five detections, App "Running".
3. Cover the lens (or point the camera elsewhere) and press Refresh: Pending, then Detected; Last change is set.
4. Stop the app on the camera, Refresh: App "Not running".
5. Turn on Auto refresh: the summary's "checked" time moves every 10 s; leave the page: no more requests (server log).
