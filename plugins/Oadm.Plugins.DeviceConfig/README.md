# Device configuration (`oadm.device-config`)

Context menu **Maintenance** > **Export configuration** and **Import configuration** (AXIS OS 12.5 or later). Uses the
device's own configuration export and import (`/config/rest/$export`, `/config/rest/$import`), the same as the camera's
web interface. Always on.

Spec, device research of 10.0.0.48 and the decisions: [`docs/specs/device-config.md`](../../docs/specs/device-config.md).

- **Export** (`ExportConfigTask`): dialog only, never a task. The client reads each device through the query `export`
  (8 at a time) and saves one `.json` or a ZIP with one file per device, named `<address>_<MAC>_<model>_config.json`.
- **Import** (`ImportConfigTask`): one `.json` for every selected device of its model, or the ZIP of an export where each
  device gets the file with its MAC address (the server checks the MAC in the entry name again). One step per settings
  area, always a merge; host name, IP address, router, DHCP, the device names and the multicast addresses are never
  imported (`ConfigIdentity`). **Connection settings** (HTTPS, HTTP port, login method, IEEE 802.1X, IPv6, web interface;
  `ConfigIdentity.ConnectionPaths`) are an area of their own, unchecked by default; when chosen they are written last in
  "Import connection settings", otherwise left out of the write and the verify. The device validates the values; its
  warnings and errors show in the step, and a failing area (also a timeout) does not stop the others; a changed
  certificate stops the task. Verify configuration exports again and compares.

Tests: `tests/Oadm.Plugins.DeviceConfig.Tests`.
