# Outputs (`oadm.outputs`)

Bundled, always on, server only. Contributes the event rule action **Set output** (`SetOutputAction`): switches an output
port of a device, for example the relay of a camera or an I/O module. Spec: `docs/spec/event-rules.md`.

| Mode | On / fired | Turns off |
|---|---|---|
| Follow the rule (stateful rules only) | active | inactive |
| Pulse for N seconds (1..3600) | active for N s (the device times it) | - |
| Set active / Set inactive / Toggle | as named | - |

- Output number 1..64 as in the device's I/O settings (1 = the first port).
- **I/O port management API** (`io-port-management` 1.x in the device's API list, `POST /axis-cgi/io/portmanagement.cgi`):
  `getPorts` first (the port must exist and be an output, else nothing is written), then `setPorts` or
  `setStateSequence` (pulse). Active means the state differs from the port's normal state (open / closed).
- Otherwise **port.cgi**: `action=<n>:/` active, `<n>:\` inactive, `<n>:/<ms>\` pulse (the device answers after the
  pulse, so the action asks for the pulse + 15 s: `MaxRunTime`), `checkactive=<n>` for Toggle (`checkoutput` is refused).
- Output n is port "n-1" of the port management API; on an AXIS A9210 output 1 is the RELAY, outputs 4, 5, 7 are O1-O3.
  The event log line names the port ("Output 1 (RELAY) active").
- Verified on an AXIS A9210 (AXIS OS 12.10.73.2) with the opt-in `OutputHardwareWriteTests` (`OADM_OUTPUT_WRITE_URL`,
  `_USER`, `_PASSWORD`, optional `_PORT`): every mode through both APIs, read back, ends inactive. Tests:
  `tests/Oadm.Plugins.Outputs.Tests`.

## Device groups

SDK 0.4.0 (`minSdkVersion` 0.4.0, docs/spec/device-scope.md): the action switches only the output of the device the rule engine gives it, and the engine gives a rule of a device group only the group's devices. Test: `DeviceGroupTests` (`PluginCanary` through `tests/Shared/TaskPluginCanaryHost.cs`).
