# I/O Monitor (`oadm.io-monitor`)

Core plugin with a rail page (group Monitoring, icon `toggle`), **off by default**. The page picks one device with inputs
or outputs and draws it as a faceplate: an INPUTS and an OUTPUTS row of terminals, each with its contact drawn open or
closed (green while active, no LEDs, no glow), the port name and the port number as "Set output" counts them (port "0" =
Port 1); the tooltip says open / closed and the normal state. Read-only for devices; no events, no actions (simple and fast).

- Devices: those whose API list has `io-port-management` 1.x (cameras, I/O modules, door controllers).
- Server: while a page shows a device, `getPorts` (`POST /axis-cgi/io/portmanagement.cgi`) every 200 ms with the stored
  credentials, 3 s per request; after a failure the next read starts 1 s after it. One reading per device
  (`DevicePoller`), shared by every page (stream) that shows it; a change (ports or error) is pushed to each of them
  (`ICorePluginContext.Events`, topic `ports`), a page that joins gets the last read at once. Each read asks
  `IVapixClientFactory.CreateAsync` for the client (cached by the host, replaced when the address, credentials or
  certificate change; never disposed by the plugin). Methods `start` ({deviceId}), `stop` and `keepAlive` ({streamId},
  every 5 s; without it the server stops after 20 s; answers `{running}`, false for an unknown stream, e.g. after a server
  restart, and the page then starts again). At most 20 devices read at once (pages of the same device count once; the
  limit is checked together with the insert); no start is accepted after the plugin stopped. Devices with Credentials
  required, Password not set or Certificate changed are refused without a request.
- Page: the device button (primary; it names the chosen device "10.0.0.70 · A9210", else "Choose a device") opens the
  shared `DevicePickerWindow.ShowOneAsync` (search, virtualized, 30,000 devices) with the devices above (others are not
  offered; the view hands it to the view model as `PickDevice`), status chip (Connecting, Live, Error + text) and the device as a faceplate: INPUTS and OUTPUTS rows
  of terminals (contact drawn open / closed and green while active, name, port number); another device or leaving the page
  stops the reading, a change of the device list does not (unless the shown device was removed). A failed keep-alive is
  retried with the next one. Theme: `Border.ioFaceplate` and `Border.ioTerminalBlock` (no own backgrounds),
  `StackPanel.ioTerminal`, `Canvas.ioContact`, `Path.ioContactLever(.active)`.
- Verified read-only on an AXIS A9210 (AXIS OS 12.10.73.2): RELAY, O1-O3, I1-I5, two configurable I/O ports
  (`IoMonitorHardwareTests`, `OADM_IO_URL` / `_USER` / `_PASSWORD`).
- Tests: `tests/Oadm.Plugins.IoMonitor.Tests` (recorded A9210 answer, pushes only on change, errors and recovery, stop
  and lease, refusals, device limit and concurrent starts, shared reading of one device, replaced clients, 1 s wait after
  a timeout (fake clock), keep-alive answers, page view model incl. keep-alive retry, restart after a forgotten stream,
  the device button (offered devices, the chosen one starts the reading, Perf 30,000 devices) and device list changes, headless `io-monitor-page.png`). Fake mode: the A9188 (10.0.0.30).
