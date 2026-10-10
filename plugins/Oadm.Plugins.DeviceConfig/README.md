# Device configuration (`oadm.device-config`)

Context menu **Maintenance** > **Export configuration** and **Import configuration** (AXIS OS 12.5 or later). Uses the
device's own configuration export and import (`/config/rest/$export`, `/config/rest/$import`), the same as the camera's
web interface. Always on.

Spec, device research of 10.0.0.48 and the decisions: [`docs/specs/device-config.md`](../../docs/specs/device-config.md).

- **Export** (`ExportConfigTask`): dialog only, never a task. The client reads each device through the query `export`
  (8 at a time) and saves one `.json` or a ZIP with one file per device, named `<address>_<MAC>_<model>_config.json`.
- **Import** (`ImportConfigTask`): one `.json` for every selected device of its model, or the ZIP of an export where each
  device gets the file with its MAC address. One step per settings area, always a merge; host name, IP address, router,
  DHCP and the device names are never imported (`ConfigIdentity`). The device validates the values; its warnings and
  errors show in the step. Verify configuration exports again and compares.

Tests: `tests/Oadm.Plugins.DeviceConfig.Tests`.
