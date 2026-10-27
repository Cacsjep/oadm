# Load test (`oadm.load-test`)

Core plugin with a rail page in group Monitoring (icon `gauge`), with `.Client`, off by default. A load test of the chosen
cameras and the network between them and the server, like AXIS Installation Verifier: more and more streams at once,
then all of them in low light, measured per stream and second. The exposure value during Low light is the only write.
Spec: [Load test](../../docs/spec/load-test.md),
[Bundled plugins](../../docs/spec/bundled-plugins.md#load-test-core-plugin).

## Decisions

| Topic | Decision |
|---|---|
| Cameras | Only the chosen cameras (`PageDeviceChoice`); `allDevices` = every video device of the caller's devices. Each video source is one stream, in the chosen order, in batches of `batchSize` (1 to 200, default 50); a batch may split a device's sources. At most 1,000 streams per check (`MaxStreams`): more chosen video devices are refused at once without a request, more streams after reading the sources; the plan carries the refusal ("A check takes at most 1,000 cameras. Choose fewer."), Start throws it. |
| Phases | Per batch: Ramp25, Ramp50, Ramp75 (the first 25 / 50 / 75 % of the batch's streams, rounded up), Normal (all), Low light (all, exposure at its maximum); each `phaseMinutes` (1, 2 or 5). A stream stays open from the step it joins to the end of its batch. |
| Stream | The largest resolution at the maximum frame rate, H.265 when listed, else H.264; RTP over UDP where the server reaches the camera (`PreferUdp`), TCP at sites. |
| Sampling | Once per second of the plugin's `TimeProvider` (`PeriodicTimer`): each open probe is read (`IDeviceStreamProbe.Read`, cumulative) and the difference to the last read is the second's value. |
| Interrupted | No first packet within `StartTimeout` (10 s) after the open, or no packet for `StallTimeout` (5 s); for the rest of that phase. A refused open or a broken stream is opened again in the next phase. |
| Pass | Per stream and phase: packet loss (lost / (received + lost), UDP only), frame loss (missing against the expected frames) and late frames each below 1 %, and not Interrupted. Jitter never fails. |
| Verdict | "Loss starts at N cameras" (the fewest streams at once of the failing steps of the first ramp step or Normal with a failure, over all batches), else "N cameras fail in Low light", else "The network and cameras carry this load"; "The check was cancelled", "The check stopped when the plugin stopped", "The server stopped during the check" (found at the next start). |
| Headroom | "100 %" when every ramp step and Normal passed in every batch, else the last ramp step that passed ("75 %", ... "0 %"). |
| UDP blocked | A probe that fell back to TCP because UDP was blocked (`UdpBlocked`): Transport TCP and the row's `note` "UDP is blocked between the camera and the server, packet loss not measured"; no failure. |
| Firewall | "OADM Server (Load test, UDP 50000-50799)" through `FirewallRuleKeeper`, open only while a check runs, closed at plugin start (a rule left by a crash). `LoadTestInfo.UdpFirstPort/UdpLastPort` must equal the core's `RtpUdpPorts`. |
| Expected rate | `Checking/BitrateEstimates.cs`: H.264 Mbit/s at 30 fps per pixel count (1 VGA, 2 for 1.2 MP, 4 for 1080p, 6 for 4 MP, 8 for 6 MP, 12 for 4K, 16 above), x fps / 30, H.265 x 0.6, Low light x 2.5. An estimate only. |

## Exposure (the only write)

`Checking/ExposureKeeper.cs`, CLAUDE.md "device safety": before Low light, per stream (`ImageSource.I<camera - 1>.Sensor.ExposureValue`,
16 at a time) a `param.cgi` list read; a missing value or one that is not a number from 0 to 100 means Low light N/A
(the stream still streams, nothing is written). The original is kept in memory only (before the write, so a write
whose answer is lost is still written back), then 100 is written (`action=update` in the POST body; "# Error" answers
count as refused: N/A). Written back after Low light, at once when the stream drops out (open refused or stream
broken), on Cancel, on the plugin's stop and when the run breaks off; each write and restore has its own 10 s timeout.
A failed restore becomes a run warning "<camera>: exposure <original> was not written back". Every write and restore
is logged (camera, parameter, values). The parameter (0..100, default 50) is confirmed on AXIS P3265-V and M3215-LVE (AXIS OS 12.11).

## How it works

| File | What |
|---|---|
| `LoadTestPlugin.cs` | `ICorePlugin`, methods, permissions |
| `Checking/CheckService.cs` | One check at a time, device checks, the kept runs, events, the firewall rule |
| `Checking/CheckPlanner.cs` | Sources (`ListSourcesAsync`, 16 at a time, 15 s each, kept 10 minutes for Start), left-out reasons, batches, time, rate |
| `Checking/CheckRunner.cs` | The phases and the per-second sampling, phase results, steps |
| `Checking/CheckRun.cs` | The run's state, rows, summary, verdict and headroom texts (`Verdicts`) |
| `Checking/ExposureKeeper.cs` | Low light exposure: read, raise, write back |
| `Checking/RunStore.cs` | Kept runs |
| `Checking/BitrateEstimates.cs` | The estimate table |
| `Shared/LoadTestModels.cs` | Contract shared with the page |

Left-out reasons: "No video" (no video device, no source), the refusal texts of `DeviceMessages` for a device that is
not OK (no request), the device's own error of `ListSourcesAsync`, "No H.264 or H.265 stream", a timeout.

Storage: the summaries as plugin records (collection `runs`, with the owner scope), the graphs and rows as
`runs/<id>/detail.json.gz` and the per-second values of each stream as `runs/<id>/batch-<n>.json.gz` in the plugin's data
folder (in memory without one). The values of a batch are saved and freed at its end, so 30,000 streams never sit in
memory at once. The last 5 runs are kept (`MaxRuns`); a start that would make 6 deletes the oldest. A run is saved as
Running at its start and after every batch: after a crash it is loaded as Failed.

## Methods

| Method | Permission | Payload | Answer |
|---|---|---|---|
| `plan` | show | `PlanRequest` {deviceIds (1 to 30,000), batchSize, phaseMinutes, allDevices} | `CheckPlan` (read only, nothing streams) |
| `start` | run | `StartRequest` (as plan) | `RunSummary` at once (`phase` null while the first streams connect); refused while a check runs: "A check by anna is running (40 % done). Start again when it ends." |
| `cancel` | run | none | `RunSummary` of the cancelled run once the exposure is back and the probes are closed, or null when none of the caller's runs |
| `listRuns` | show | none | `RunList` (newest first, the running one included) |
| `getRun` | show | `RunRequest` {runId, skip, take} | `RunDetail`: one page of rows worst first (take 0 = 5,000, at most 5,000), `totalStreams`, the graphs only with skip 0 |
| `getStream` | show | `StreamRequest` | `StreamDetail`: the stream's values per second over its batch |
| `deleteRun` | run | `RunRequest` | {}; the running run: "Cancel the check first." |

`run` ("Run load tests") is on for Operator, off for Viewer; `show` is on for Viewer (Monitoring page). No
method is audited. Events: `live` once per second while a check runs (`LiveEvent`: the summary, the new seconds, the
full step list, the changed rows in chunks of 200), `runs` (`RunList`) when a run starts, ends or is deleted.

## Device groups

A group user's chosen ids must all be in the group, else the whole request is refused (NOT_FOUND "1 device is no longer
available.", nothing read); a user with all devices gets the chosen devices that still exist. A run belongs to the device
scope that started it: its users list and open only their scope's runs (a camera that left the group is no longer in
their rows), users with all devices every run; another scope's run answers like a deleted one. `live` and `runs` go to
the owner scope and to users with all devices (`PluginEventAudience.DeviceScope`). A Start of another group while a check
runs names the running check's user and progress. The calls have no stream probes: the ids are checked against the call,
then the streams and the exposure writes run as background work over the context (`Detached`).

## Tests

`tests/Oadm.Plugins.LoadTest.Tests/Server` with `FakeTimeProvider` and `FakeStreamProbes` (`Rig` steps the clock
only once the run waits for its next second): `PlannerTests` (batches, sources, left-out reasons, estimate, limits, the
cached sources, All devices), `RunnerTests` (ramp counts, each limit, verdicts and headroom, stall, start timeout, late
start, refused and broken streams, TCP, UDP blocked, batches, live events, chunks, one at a time, Cancel, Start while the
cameras connect, firewall), `ExposureTests` (Low light only, Cancel, drop-out, failed restore, plugin stop, no usable
value, refused write), `RunStoreTests` (5 runs, restart, server stop -> Failed, delete, no data folder),
`DeviceGroupTests` (the plugin canary, refusal whole, run ownership, permissions), `ScaleTests` (30,000 chosen devices
refused at once, 1,000 streams planned and started, the largest run below 4 MB, getRun pages; Perf: plan, summary and
table of 1,000), `FakeStreamProbesTests`. The page's tests are in `Client/`.

Manual test (no hardware test yet; it writes the exposure):

1. Turn the plugin on on the Plugins page, open **Load test** and choose two cameras.
2. Start: the confirmation shows cameras, batches, time and the expected rate; Start.
3. Watch the graphs grow each second; in Low light the image of the cameras is brighter.
4. After the check, the exposure on the cameras is as before (web interface, Image settings).
5. Start again and Cancel in Low light: the exposure is as before.
