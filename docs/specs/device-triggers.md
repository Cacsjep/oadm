# Device triggers (core plugin)

User decisions 2026-10-10. Plugin `plugins/Oadm.Plugins.DeviceTriggers` (+ `.Client`), id `oadm.device-triggers`, rail page
**Device triggers** (group Automation), **off by default**. An administrator picks an event that one or more devices
really offer (I/O port, virtual input, temperature, storage, Object Analytics scenario, any ACAP event); OADM keeps a
permanent WebSocket event stream to each device and hands the event to the event rules as an event type of its own.

## Decisions

| Topic | Decision |
|---|---|
| Event pick | Devices first. The editor reads the event list of the chosen devices **live every time** a trigger is created or edited (never cached: an app that is stopped or removed has no events). It offers the events **all** chosen devices have; a device without it is shown with the reason and cannot stay in the trigger. |
| Narrowing | Each source item of the event (port, disk, channel, scenario, ...) is a select with the values the device lists, or **Any**. Sent to the device as its content filter. No data value conditions in v1. |
| Devices | A fixed list of chosen devices (no tags). A device removed from OADM leaves the trigger. |
| State | On/off events: the trigger is on per device while at least one matching source is on (Any port: on while any matching port is on). One-shot events stay one-shot. |
| Values | Every source and data item becomes a placeholder for actions (`{port}`, `{total}`, the device's item name) and is shown in the event log. |
| Connection lost | Every trigger automatically has a second, on/off event **"<Trigger>: connection lost"** per device: on when OADM receives no events of the trigger from the device for 30 s (connection down, login rejected, event no longer on the device), off once the stream works again. The reason goes to the event log and the page. |
| State on loss | The trigger keeps its last state per device. After reconnect the device's current-state report corrects it; only real changes run actions. |
| Event gone | The event list of each connected device is read again every 5 minutes and on every reconnect. Missing event = "connection lost" on, reason "The event is no longer on the device"; it resumes when the event is back. |
| Connection | One WebSocket per device carrying every turned-on trigger of that device, open permanently while at least one trigger of the device is on (also without a rule). |
| Roles | Administrators create, change, copy, turn on/off and delete (audited); operators see triggers and live states. |
| Hardware test | Allowed: switch virtual input 64 of 10.0.0.48 on and off (opt-in `HardwareWrite`). Everything else read-only. |

## Device side (verified read-only on AXIS P3265-V, AXIS OS 12.11, 2026-10-10)

- Support: API `event-streaming-over-websocket` 1.0 in the device's API list (`device.Apis`); devices without it cannot be
  chosen ("Needs the event stream over WebSocket (AXIS OS ...)").
- Event list: SOAP `aev:GetEventInstances` (event1) at `/vapix/services`, read-only. `wstop:TopicSet` tree of topics with
  `aev:NiceName`; each leaf has one `aev:MessageInstance`: `aev:isProperty="true"` = on/off (stateful), else one-shot;
  `SourceInstance` / `KeyInstance` / `DataInstance` with `SimpleItemInstance` (Name, Type, NiceName, listed `Value`s);
  the data item with `isPropertyState="true"` is the state. The P3265-V lists 49 types (most stateful). Object Analytics
  scenarios appear as `tnsaxis:CameraApplicationPlatform/ObjectAnalytics/Device1Scenario<n>` (+ `Device1ScenarioANY`,
  data `active`) only while the app runs and has scenarios; counting data is one-shot.
- Stream: `GET /vapix/ws-data-stream?sources=events` upgraded to WebSocket. Login with a session token: `GET
  /axis-cgi/wssession.cgi` through the device's VAPIX client (stored credentials, Digest over HTTP / Basic over HTTPS,
  pinned certificate), then `?wssession=<token>`; the token works once and only for 15 s. ws for HTTP devices, wss with
  the pinned certificate for HTTPS devices.
- Filter: `{"apiVersion":"1.0","method":"events:configure","params":{"eventFilterList":[{"topicFilter":"tns1:Device/tnsaxis:IO/VirtualInput","contentFilter":"boolean(//SimpleItem[@Name=\"port\" and @Value=\"3\"])"}]}}`;
  answer `data: {}`, a bad filter `error {code 2104, "Could not use supplied event filter"}`; an unknown topic is accepted
  silently (OADM checks against the event list). A new configure replaces the old filter (one per device, union of its
  triggers; an event matching two filters arrives twice, so OADM matches each message to its triggers itself).
- Messages: `{"method":"events:notify","params":{"notification":{"topic":...,"timestamp":<ms>,"message":{"source":{...},"key":{...},"data":{...}}}}}`.
  Right after configure the device sends the current state of every matching stateful event (64 virtual inputs in 0.1 s):
  that is the plugin's full report (`ReplaceStates`). No Initialized/Changed marker.
- Keep-alive: the device sends nothing on an idle connection (75 s tested) and answers a WebSocket ping in about 1 ms:
  OADM pings every 10 s, no pong within 10 s = connection lost, reconnect with backoff 1, 2, 4, 8, 10 s.
- 30 parallel connections to one camera worked.

## Apps

- **Apps** (loan AXIS Q6411-LE, AXIS OS 13.0.88, 2026-10-10): AXIS Image Health Analytics declares "Image Health
  Analytics - Blur / Block / Redirect / Under-Exposure / Unsuitability / Any-Active-Events" (on/off `active`).
  AXIS Object Analytics declares per scenario `Device1Scenario<id>` named like the scenario plus `Device1ScenarioANY`:
  motion, fence and tailgating are on/off (`active`, `scenarioType`, `classTypes`, `objectId`, `triggerTime`),
  crossline counting and occupancy are one-shot with the counts (`total`, `totalHuman`, ... / `human`, `car`, ...),
  occupancy adds `Device1Scenario<id>Threshold` (on/off). The scenario's **Test alarm** (`sendAlarmEvent`) turns a
  motion / fence / tailgating scenario on for about 6 s; counting scenarios send nothing. Apps that are stopped have no
  events (the trigger shows "The event is no longer on the device").

## Engine and SDK changes

- `IDynamicEventSource` (SDK): an event source whose types change raises `EventTypesChanged`; the registry reads the types
  again and the rule engine follows: a new stateful type waits for its first `ReplaceStates`, a removed type counts as off
  (its rules turn off, "Not available: an event of this rule no longer exists"). Types rules use that no source declares
  yet (device triggers declare theirs once the plugin started) wait like a source after a start, then count as off.
- `IVapixClient.ConnectWebSocketAsync(path, keepAlive)` (SDK, implemented by `VapixClient`): session token, then the
  upgrade through the client's own handler (certificate pin), pings with `KeepAliveTimeout`.
- Action placeholders: every value of the event (`ActionTrigger.Data`) by its name, after the fixed ones.
- Per trigger two types: `oadm.device-triggers.<id>` (stateful or stateless, device scope) and `....lost` (stateful).
- The device's report has no end marker: what arrives within 1.5 s after the filter reply is the report.

## Tests

Parser of the recorded `GetEventInstances` answer and notifications; filter building; stream lifecycle against a fake
WebSocket server (initial states, changes, ping timeout, reconnect, token per connection, event gone, configure error);
engine integration (dynamic types, connection lost, keep state on loss); page and editor view models with 5,000 devices;
fake mode; headless screenshots; read-only hardware test (connect, initial states) and the opt-in write test (virtual
input 64).
