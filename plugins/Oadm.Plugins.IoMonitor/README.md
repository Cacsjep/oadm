# I/O Monitor (`oadm.io-monitor`)

Core plugin with a rail page (group Monitoring, icon `toggle`), **off by default**. The page picks one device with inputs
or outputs and shows every port as a tile: name, port number (as "Set output" counts them: port "0" = Port 1), a lamp
(green while active) and Active / Inactive; the tooltip says open / closed and the normal state. Read-only for devices;
no events, no actions (user decision 2026-10-10: simple and fast).

- Devices: those whose API list has `io-port-management` 1.x (cameras, I/O modules, door controllers).
- Server: while a page shows a device, `getPorts` (`POST /axis-cgi/io/portmanagement.cgi`) every 200 ms with the stored
  credentials, 3 s per request, 1 s between tries after a failure; a change (ports or error) is pushed to the page
  (`ICorePluginContext.Events`, topic `ports`). Methods `start` ({deviceId}), `stop` and `keepAlive` ({streamId}, every
  5 s; without it the server stops after 20 s). At most 20 devices shown at once. Devices with Credentials required,
  Password not set or Certificate changed are refused without a request.
- Page: device picker, status chip (Connecting, Live, Error + text) and the device as a faceplate: INPUTS and OUTPUTS rows
  of terminals (contact drawn open / closed and green while active, name, port number); another device or leaving the page
  stops the reading.
- Verified read-only on a loan AXIS A9210 (AXIS OS 12.10.73.2): RELAY, O1-O3, I1-I5, two configurable I/O ports
  (`IoMonitorHardwareTests`, `OADM_IO_URL` / `_USER` / `_PASSWORD`).
- Tests: `tests/Oadm.Plugins.IoMonitor.Tests` (recorded A9210 answer, pushes only on change, errors and recovery, stop
  and lease, refusals, page view model, headless `io-monitor-page.png`). Fake mode: the A9188 (10.0.0.30).
