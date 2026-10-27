# Outputs (`oadm.outputs`)

Event rule action, server only, always on. Contributes **Set output** (`SetOutputAction`, group Devices): switches an
output port of a device, for example the relay of a camera or an I/O module. Spec:
[Event rules](../../docs/spec/event-rules.md).

## Decisions

| Mode | On / fired | Turns off |
|---|---|---|
| Follow the rule (stateful rules only) | active | inactive |
| Pulse, Seconds 1..3600 (default 5) | active for N s (the device times it) | - |
| Set active / Set inactive / Toggle | as named | - |

- Output number 1..64 as in the device's I/O settings (1 = the first port).
- The event log line names the port ("Output 1 (RELAY) active").

## Device API

- **I/O port management API** (`io-port-management` 1.x in the device's API list, `POST /axis-cgi/io/portmanagement.cgi`):
  `getPorts` first (the port must exist and be an output, else nothing is written), then `setPorts` or
  `setStateSequence` (pulse). Active means the state differs from the port's normal state (open / closed).
- Otherwise **port.cgi**: `action=<n>:/` active, `<n>:\` inactive, `<n>:/<ms>\` pulse (the device answers after the
  pulse, so the action asks for the pulse + 15 s: `MaxRunTime`), `checkactive=<n>` for Toggle (`checkoutput` is refused).
- Output n is port "n-1" of the port management API; on an AXIS A9210 output 1 is the RELAY, outputs 4, 5, 7 are O1-O3.

## Device groups

Event rules are for users with all devices, so the action depends on no device group: it switches only the output of
the device it is given, through that device's client ([device groups](../../docs/spec/device-scope.md)). Tests: server
`DeviceGroups/CanaryTests`.

## Tests

`tests/Oadm.Plugins.Outputs.Tests`. Opt-in hardware test `OutputHardwareWriteTests` (`Category=HardwareWrite`,
`OADM_OUTPUT_WRITE_URL`, `_USER`, `_PASSWORD`, optional `_PORT`): every mode through both APIs, read back, ends
inactive; verified on an AXIS A9210 (AXIS OS 12.10.73.2).
