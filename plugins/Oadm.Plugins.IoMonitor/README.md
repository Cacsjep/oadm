# I/O Monitor (`oadm.io-monitor`)

Core plugin with a rail page in group Monitoring (icon `toggle`), with `.Client`, off by default. The page picks one
device with inputs or outputs and draws it as a faceplate with its ports, live. Read only for devices; no events, no
actions. Spec: [Bundled plugins](../../docs/spec/bundled-plugins.md#io-monitor-core-plugin).

## Device API

- Devices: those whose API list has `io-port-management` 1.x (cameras, I/O modules, door controllers).
- `getPorts` (`POST /axis-cgi/io/portmanagement.cgi`) with the stored credentials. Port "0" = Port 1, as "Set output"
  counts them.
- Verified read-only on an AXIS A9210 (AXIS OS 12.10.73.2): RELAY, O1-O3, I1-I5, two configurable I/O ports.

## How it works

- While a page shows a device, `getPorts` every 200 ms, 3 s per request; after a failure the next read starts 1 s after
  it.
- One reading per device (`DevicePoller`), shared by every page (stream) that shows it; a change (ports or error) is
  pushed to each of them (`ICorePluginContext.Events`, topic `ports`); a page that joins gets the last read at once.
- Each read asks `IVapixClientFactory.CreateAsync` for the client (cached by the host, replaced when the address,
  credentials or certificate change; never disposed by the plugin).
- At most 20 devices read at once (pages of the same device count once; the limit is checked together with the insert);
  no start is accepted after the plugin stopped. Devices with Credentials required, Password not set or Certificate
  changed are refused without a request.

## Page

Subtitle "Inputs and outputs of a device, live.".

- The device command button (the I/O device icon; "10.0.0.70 · A9210", else "Choose a device") opens the shared
  `DevicePickerWindow.ShowOneAsync` (search, virtualized, 30,000 devices) with the devices above (others are not
  offered; the view hands it to the view model as `PickDevice`).
- Status chip: Connecting, Live, Error + text.
- The faceplate: an INPUTS and an OUTPUTS row of terminals, each with its contact drawn open or closed (green while
  active, no LEDs, no glow), the port name and the port number; the tooltip says open / closed and the normal state.
- Another device or leaving the page stops the reading; a change of the device list does not (unless the shown device was
  removed). A failed keep-alive is retried with the next one.
- Theme: `Border.ioFaceplate` and `Border.ioTerminalBlock` (no own backgrounds), `StackPanel.ioTerminal`,
  `Canvas.ioContact`, `Path.ioContactLever(.active)`.

## Methods

| Method | Payload | Answer |
|---|---|---|
| `start` | {deviceId} | {streamId} |
| `stop` | {streamId} | none |
| `keepAlive` | {streamId}, every 5 s; without it the server stops after 20 s | {running}: false for an unknown stream (e.g. after a server restart), and the page then starts again |

## Device groups

Every stream is a job of its user (`ctx.Jobs`): `stop` and `keepAlive` look it up for the user; another user's id and an
unknown one fail alike ("The live reading has ended.", `IoMonitorMethods.StreamEnded`; the page then starts again).
`start` takes the device from the user's call (a device outside the user's group answers like an unknown one, NOT_FOUND
"1 device is no longer available."); the ports go only to the page's session (`PluginEventAudience.Of`). A reading is
shared only by pages of the same device scope and reads through their call, so when the device leaves their view the
pages get "1 device is no longer available." and the reading ends. See [Device groups](../../docs/spec/device-scope.md).

## Tests

`tests/Oadm.Plugins.IoMonitor.Tests`: recorded A9210 answer, pushes only on change, errors and recovery, stop and lease,
refusals, device limit and concurrent starts, shared reading of one device, replaced clients, 1 s wait after a timeout
(fake clock), keep-alive answers, page view model incl. keep-alive retry, restart after a forgotten stream, the device
button (offered devices, the chosen one starts the reading, Perf 30,000 devices) and device list changes, headless
`io-monitor-page.png`; `CanaryTests`: the device-group canary, the page's session only, the end when the device leaves the
view. Read-only hardware test `IoMonitorHardwareTests` (`OADM_IO_URL` / `_USER` / `_PASSWORD`). Fake mode: the A9188
(10.0.0.30).
