# Load test

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

A load test of the chosen cameras and the network between them and the OADM server, in the spirit of AXIS Installation
Verifier in AXIS Camera Station Pro, without its server and storage parts (OADM does not record). The user chooses cameras,
starts a check and sees on the plugin page whether the network and the cameras carry the load: a verdict, graphs and one
row per camera. No PDF.

Core plugin `plugins/Oadm.Plugins.LoadTest` (+ `.Client`), id `oadm.load-test`, rail page
**Load test** (group Monitoring), free (Apache-2.0), **off by default**.

## Rules that never break

1. **Choose devices first.** The page starts from `PageDeviceChoice` ("Selected cameras"); nothing streams before
   **Start**. Devices without video are left out with the reason in the table.
2. **The exposure always goes back.** The low-light phase is the only write. The original value is read and kept in
   memory before the write and written back after the phase, on Cancel, when a camera drops out of the check and when the
   plugin stops. A camera whose restore fails stays in the result with a warning and its original value.
3. **One check at a time** per server. A second Start is refused with the running check's owner and progress.
4. **Bounded load.** At most 200 cameras stream at once (one batch); the expected rate of a batch is shown before Start.
5. **Read-only otherwise.** Streams and parameter reads only; no stream profile, no setting other than the exposure value
   is changed. Credentials never leave the server.
6. **Device groups.** A group user chooses, checks and sees only the cameras of the group (chosen ids outside it refused
   whole, NOT_FOUND); stored runs are owned by the device scope that ran them.

## The check

- **Cameras**: at most 1,000 camera streams per check (`LoadTestInfo.MaxStreams`), run in batches one after the
  other; batch size default 50 (the page field is hidden for now), at most 200. Every video source that is not disabled
  (`Image.I<n>`) counts as one camera stream. A plan over the limit says "A check takes at most 1,000 cameras. Choose
  fewer." (more chosen video devices than the limit: at once, without reading a device) and Start is refused with it.
- **Stream**: always the highest: the largest resolution of the source at its maximum frame rate, H.265 when the camera
  lists it, else H.264 (no MJPEG); the camera's own compression and Zipstream settings.
- **Transport**: RTSP with RTP over **UDP** for cameras the server reaches directly, so a sequence gap is real network
  loss. Cameras at sites use RTP interleaved over TCP through the site node (the node relays TCP only); their result
  shows late and missing frames and says "Packet loss is not measured at sites".
- **Phases per batch**, each 2 minutes (1 or 5 possible; the page field is hidden for now):
  1. **Ramp**: 25 %, 50 %, 75 % of the batch streaming at once (the first cameras of the batch).
  2. **Normal**: 100 % of the batch.
  3. **Low light**: 100 % of the batch, with the exposure value of each source at its maximum, like ACS. A camera without
     the parameter streams as in Normal and its low-light result is N/A.
  A batch of 50 with 2 minutes takes about 10 minutes plus connect times. Cancel stops the batch, restores the exposure and
  keeps the measured part as a cancelled run.
- **Exposure**: `param.cgi` `ImageSource.I<n>.Sensor.ExposureValue` (read, written for the phase, written back); checked
  against the camera's parameter list before the first write, refused cameras are N/A (no `DeviceNotCompatibleException`,
  the rest of the check runs). Confirmed on AXIS P3265-V (AXIS OS 12.11.118) and M3215-LVE (12.11.147): int 0..100, default 50.
- **Confirmation** before Start (`ui:MessageWindow`): the camera count, batches, time, expected peak rate and "The
  exposure of these cameras changes for <n> minutes during the check. Recordings of your VMS see the brighter, noisier
  image."

## What is measured

Per camera stream, per second, from the RTP and RTCP of the stream (nothing extra on the camera):

- **Packet loss**: lost RTP packets in % (UDP only, sequence gaps; late reorders inside 50 packets are not lost).
- **Frame loss**: missing frames in % against the asked frame rate, from the RTP timestamps and markers.
- **Received rate**: Mbit/s without overhead, per camera and summed over the batch.
- **Jitter**: RFC 3550 interarrival jitter in ms.
- **Late frames**: frames that arrive more than 2 frame times after their RTP time says.

A stream that does not start in 10 s or stops for 5 s counts as **Interrupted** for the rest of its phase.

**Pass** per camera and phase: average packet loss below 1 %, average frame loss below 1 %, late frames below 1 % and not
Interrupted (Installation Verifier's 1 % lost frames). Jitter is shown, never failed on; its graphs draw a red limit line at 30 ms (`JitterWarningMs`), above which the network is
congested. The thresholds are constants in
the plugin, not settings.

## The page

- **Layout** like the Snapshot report page: one card whose first row is always the toolbar (`PageDeviceChoice`, Start /
  Cancel, Runs on the right), so it never moves; below it the stat cards and graphs, then the camera table, the devices
  left out or what to do first. No text line under the toolbar (the stat cards say it), only progress, a refusal or
  restore warnings when there is one. The batch size and duration fields exist but are hidden for
  now (maintainer, 2026-10-27): every check runs with 50 cameras at once and 2 minutes per phase.
- **Before a run**: the chosen camera count, batches, time and the expected peak rate (largest resolution and frame rate
  per source with a bitrate estimate per resolution and codec).
- **Verdict**: one line per run in the run list only ("The network and cameras carry this load", "Loss starts at 150
  cameras" (the first ramp step or phase with a failed camera, counted over all batches), or "3 cameras fail in Low
  light"). The result itself shows no verdict or run line above the stat cards (they and the table say it); only restore
  warnings stand there, when there are any.
- **Stat cards** (`ui:StatCard`): cameras checked, peak rate of a batch, worst camera loss, headroom (the last passing
  ramp step or "100 %").
- **Graphs**: line graphs over time per phase (sum of received rate, average and worst packet loss, average jitter) and a
  bar graph of the ramp steps (loss per step). They update live during the run (one point per second, `ChangeBatcher`
  like the other streams).
- **Camera table**: virtualized `DataGrid`, `wrapRows`, one row per camera stream: name, address, model, pass per phase
  (`ui:StatusChip`), packet loss, frame loss, rate, jitter (numeric columns right-aligned). A row opens that camera's own
  graphs. Sorted by the worst result first.
- **Opening a run**: `getRun` pages the rows (`Skip`, `Take`, at most 5,000 per page, worst first, `TotalStreams`); the
  graphs come with the first page. The page adds each page to the table in one range and shows a progress row meanwhile.
- **Runs**: the last 5 runs are kept on the server (summary and the per-second values per camera stream, in the
  plugin's own store), listed with date, user, cameras and verdict; open, delete. The 6th run deletes the oldest. No
  export.

## Server and SDK

- The RTSP client and the H.264 / H.265 depacketizers of `Oadm.Core/LiveView` get RTP over UDP (client ports from a fixed
  range, RTCP receiver reports) and per-stream counters; live view keeps TCP.
- `IFirewallRules` and `FirewallRule` get a port range: the plugin opens "OADM Server (Load test, UDP <range>)"
  only while a check runs, through `FirewallRuleKeeper` (a rule left by a crash is removed at start).
- The graphs are new shared controls in `Oadm.Sdk.Client/Controls`: `ui:LineChart` and `ui:BarChart` (theme colours in
  both appearances, a tooltip on hover, points reduced to the pixel width so 200 streams x 300 s stay fast), no chart
  dependency; they go into the catalogue of `visual-style.md` "Shared controls" when built, for other plugins too.
- The streams are not shared with live view (`LiveViewHub`): each counts as its own connection to the camera.
- gRPC through `PluginService.Invoke` and a plugin event stream for the live values; methods `start`, `cancel` and
  `deleteRun` are Operator, `getRun` and `listRuns` Viewer.

## Tests

- RTP over UDP: recorded packets with gaps, reorders and duplicates give the expected loss and jitter.
- Restore: Cancel, a dropped camera, a failed write and plugin stop all write the original exposure back (`FakeCamera`).
- 30,000 chosen devices are refused at once; 1,000 streams plan, run and open (every getRun page well below 16 MB).
- Device groups: chosen ids outside the group are refused whole.

## Decided at the build

- UDP ports 50000-50799 (two per stream, 200 streams at once, room for reuse): the core's `RtpUdpPorts` and the plugin's
  `LoadTestInfo.UdpFirstPort/UdpLastPort` (must stay equal), the firewall rule over them open only while a
  check runs.
- The bitrate estimate table is `plugins/Oadm.Plugins.LoadTest/Checking/BitrateEstimates.cs` (an estimate: H.264
  at 30 fps per pixel count, scaled by fps, H.265 x 0.6, Low light x 2.5).
- Exposure: written to 100, the top of the parameter's range on Axis cameras (camera n = `ImageSource.I<n-1>`); a value that
  is missing or not a number from 0 to 100 is not written (Low light N/A). Confirmed on hardware (P3265-V, M3215-LVE: 0..100, default 50).
- A probe whose UDP was blocked falls back to TCP (`UdpBlocked`): its row says "UDP is blocked between the camera and the
  server, packet loss not measured" (`StreamRow.Note`), no failure.
