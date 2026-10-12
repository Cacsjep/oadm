# Device triggers (`oadm.device-triggers`)

Core plugin with a rail page (group Automation, icon `flame`), **off by default**. An administrator picks an event that the chosen devices offer (I/O ports, virtual inputs,
temperature, storage, Object Analytics scenarios, events of other apps), optionally narrowed to source values; OADM keeps
one WebSocket event stream per device and publishes every trigger as two event types for the event rules.

## Decisions

| Topic | Decision |
|---|---|
| Event pick | Devices first. The editor reads the event list of the trigger's devices **live every time** a trigger is created or edited (never cached: an app that is stopped or removed has no events). It offers **every event any of the devices offers**. A chosen device that was read and lacks the event blocks Save ("Does not offer this event"); a device that cannot be read now (offline, login refused) stays and is used once it answers; a device of a tag or of all devices without the event is left out quietly. Save waits for the chosen devices only, not for the devices of a tag or of all devices, once the event is known. A device whose API list was not read yet (just added) is read like the others. |
| Narrowing | Each source item of the event (port, disk, channel, scenario, ...) is a select with the values the device lists, or **Any**. Sent to the device as its content filter. No data value conditions in v1. |
| Devices | All devices, tags and chosen devices together (the shared device picker). Devices of a tag or of all devices join and leave with the device table (every 30 s); such a device without the event is left out quietly. A device removed from OADM leaves the trigger. |
| State | On/off events: the trigger is on per device while at least one matching source is on (Any port: on while any matching port is on). One-shot events stay one-shot. |
| Values | Every source and data item becomes a placeholder for actions (`{port}`, `{total}`, the device's item name) and is shown in the event log. |
| Connection lost | Every trigger automatically has a second, on/off event **"<Trigger>: connection lost"** per device: on when OADM receives no events of the trigger from the device for 30 s (connection down, login rejected, event no longer on the device), off once the stream works again. The reason goes to the event log and the page. |
| State on loss | The trigger keeps its last state per device. After reconnect the device's current-state report corrects it; only real changes run actions. |
| Event gone | The event list of each connected device is read again every 5 minutes and on every reconnect (a trigger change reads it only when a chosen device lacks an event not checked yet; devices of a tag or of all devices wait for the next regular read). Missing event = "connection lost" on, reason "The event is no longer on the device"; it resumes when the event is back. |
| Connection | One WebSocket per device carrying every turned-on trigger of that device, open permanently while at least one trigger of the device is on (also without a rule). At most 16 devices connect (event list + stream) at the same time; a trigger change sets a new filter only on the devices whose triggers changed. |
| Refused filter | A filter entry the device refuses (error 2104) is a problem of its triggers only ("connection lost" after 30 s with the device's text); with several entries each is tried alone once, the accepted ones are set together. The other triggers of the device keep working. |
| Turned off / no devices | A turned-off trigger keeps its event types and reports them as off at once; a turned-on trigger without devices right now (a tag without devices) reports "off everywhere" at once. Rules never wait for a report that cannot come. |
| Roles | Administrators create, change, copy, turn on/off and delete (audited); operators see triggers and live states. |
| Hardware test | Allowed: switch virtual input 64 of the development camera on and off (opt-in `HardwareWrite`). Everything else read-only. |

## How it works

- **Event types** (`IDynamicEventSource`, they change with the triggers): `oadm.device-triggers.<id>` named like the
  trigger (on/off or one-shot like the device's event), and `oadm.device-triggers.<id>.lost` "&lt;Trigger&gt;: connection
  lost" (on/off). Group "Device triggers". Disabled triggers keep their types (their rules stay valid) and report them
  as off at once (`ReplaceStates` with nobody on); a turned-on trigger without devices right now reports the same; a
  deleted trigger's types disappear (its rules show "Not available: an event of this rule no longer exists").
- **Event list** (`Device/EventDeclarations`): SOAP `aev:GetEventInstances` at `/vapix/services`, read-only. Topics marked
  `isApplicationData` (on the topic or a parent) are left out. The editor reads it live for every create and edit
  (`startRead` / `readStatus`, 16 devices at a time; a device whose API list was not read yet is read too); a connection
  reads it on every connect and every 5 minutes, and on a trigger change only when a chosen device lacks a topic not
  checked yet (devices of a tag or of all devices never cause a read).
- **Stream** (`Runtime/DeviceConnection`, one per device with a turned-on trigger): `IVapixClient.ConnectWebSocketAsync`
  (session token from `wssession.cgi`, wss with the pinned certificate, ping every 10 s), then `events:configure` with one
  filter per topic (narrowed values joined with "or", "Any" = the whole topic); what arrives within 1.5 s after the reply
  is the device's report of its current states. Trigger changes set a new filter on the open socket of the devices whose
  triggers changed only. At most 16 devices connect at once (`DeviceTriggersOptions.ConnectParallelism`). The request's
  `context` is numbered; answers to a replaced list are ignored. A refused entry (2104) is a problem of its triggers only:
  with several entries each is tried alone, the accepted ones are set together. A value with both quote characters is an
  XPath `concat()`. A refused upgrade (401 / 403) shows the shared Unauthorized / Forbidden text. Reconnect after 1, 2,
  4, 8, 10 s. Devices with Credentials required, Password not set or Certificate changed are not contacted.
- **State** (`Runtime/TriggerRuntime`): per trigger and device the state of every matching source; on while at least one
  is on. The first report of every device (or a problem) makes the trigger report to the rules (`SetState` with the
  event values, then `ReplaceStates`; a pending count per trigger keeps the check O(1)); afterwards each change is one `SetState`, one-shot events are `Fire`. A problem keeps
  the last state; after 30 s "connection lost" turns on with `{reason}`; the next report turns it off.
- **Methods** (`DeviceTriggersMethods`): `list`, `get`, `startRead`, `readStatus` (Operator); `save`, `delete`,
  `setEnabled`, `duplicate` (Admin, audited). Event `changed` (page reload, at most every 300 ms on the page). Triggers are
  stored in the plugin setting `triggers`; at most 200 triggers, 5,000 devices each. Devices removed from OADM leave their
  triggers (checked every 30 s).
- **Page**: triggers (On, Name, Event, Devices, State, Last event; New trigger, Edit, Duplicate, Delete) and the devices of
  the selected one (Connection, State, Last event). Dialog: name and turned on in one row, Choose devices (the shared device
  picker with All devices, tags and single devices; OK reads the event lists of the devices that joined at once, the
  chosen ones first), the trigger's devices with "Included by" (Chosen, Tag X, All devices) and their event count or why
  they were not read (they stay), the event (grouped select of every event any device offers; a chosen device that was
  read and lacks the event is shown "Does not offer this event" and blocks Save, a device of a tag or of all devices is
  left out; Save waits only for the chosen devices once the event is known), one select per source item (Any or a value).
  Server: `AllDevices` / `Tags` are resolved after every change and every 30 s (`DeviceTriggersOptions.ResolveInterval`,
  `TriggerDefinition.Implicit`), one resolve at a time so an older list never replaces a newer one (stop waits for a
  resolve in flight; a stopped runtime takes no new connections); `TriggerRuntime.NotOffered` leaves such a device out
  without "connection lost".
- Verified on an AXIS P3265-V (AXIS OS 12.11): 42 events, token login over HTTPS, the report after the filter,
  and virtual input 64 on and off (`DeviceTriggersHardwareTests`, `OADM_TRIGGER_URL` / `_USER` / `_PASSWORD`; the write
  test is `Category=HardwareWrite`).
## Page and dialog

- **Page** (own cards): triggers (On, Name, Event, Devices, State "On for 1 of 3 · 1 connection lost", Last event; New
  trigger, Edit, Duplicate, Delete) and the devices of the selected trigger (Connection chip with the problem, State, Last
  event), live through the plugin's `changed` events. Dialog `TriggerEditorWindow` (fixed size, no device count): Name and
  Turned on in one row; Devices card with **Choose devices** (the shared `DevicePickerWindow`: add, and remove by
  unchoosing; a new trigger has no devices; devices without the stream API are greyed there with the reason) and only the
  trigger's devices (virtualized: Address, Model, Events "42 events" / "Does not offer this event" / warning "Not read:
  <reason>. It stays in the trigger."). OK in the picker reads the event lists of the added devices at once (the server 16
  at a time, progress row); editing a trigger reads all its devices. The Event select offers
  every event any device offers (not only the common ones); a device that cannot be read now (offline, login refused)
  stays and does not block Save (OADM connects once it answers; the event of a trigger being edited stays even when no
  device can be read); a device that was read and lacks the chosen event blocks Save. Event (`ComboBox.grouped`, fixed width
  (the open list keeps the width of the box, long names wrap and the list scrolls), grouped like the device's own event page: every app event (topics below
  `CameraApplicationPlatform`, `AcapOnvifEvent` or `tns1:RuleEngine`: Object Analytics, Video Motion Detection incl. VMD 3,
  Image Health Analytics, ...)
  in one group "Applications", otherwise the section below the top level ("Device status", "Input ports"); the event's own
  name below; "Turns on and off" /
  "Happens once"), one select per source item (Any or a value); Save disabled with the first problem as tooltip.
- **Fake mode** (`FakeOadmApi.DeviceTriggers.cs`): three sample triggers, event lists per device kind.
- **Tests**: `tests/Oadm.Plugins.DeviceTriggers.Tests` (fake camera speaking the WebSocket protocol in memory; page and
  dialog; headless `device-triggers-page.png`, `device-triggers-editor.png`; hardware read test and the opt-in write test
  switching virtual input 64 and the Object Analytics test alarm (`OADM_TRIGGER_OA_SCENARIO`), `OADM_TRIGGER_URL` /
  `_USER` / `_PASSWORD`; read-only app event test for Image Health and Object Analytics), `tests/Oadm.Core.Tests/Events/
  DynamicEventTypesTests`, `tests/Oadm.Core.Tests/Vapix/VapixWebSocketTests`.

## SDK

- `IVapixClient.ConnectWebSocketAsync(path, keepAlive)` (implemented by `VapixClient`): session token, then the upgrade
  through the client's own handler (certificate pin), pings with `KeepAliveTimeout`. A refused upgrade (HTTP 401 / 403,
  `CollectHttpResponseDetails`) is `UnauthorizedAccessException` with the shared texts "Unauthorized - HTTP 401 ..." /
  "Forbidden - HTTP 403 ...", like a refused token request.
- `IDynamicEventSource`: the plugin's event types change with its triggers (`EventTypesChanged`); the event values reach
  actions as placeholders (`ActionTrigger.Data`).

## Device side (AXIS P3265-V, AXIS OS 12.11, read-only)

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
  answer `data: {}` (the request's `context` echoed; OADM numbers it and ignores answers to a replaced list), a bad
  filter `error {code 2104, "Could not use supplied event filter"}`; an unknown topic is accepted
  silently (OADM checks against the event list). A new configure replaces the old filter (one per device, union of its
  triggers; an event matching two filters arrives twice, so OADM matches each message to its triggers itself). A value
  with both quote characters is written as XPath `concat()`.
- Messages: `{"method":"events:notify","params":{"notification":{"topic":...,"timestamp":<ms>,"message":{"source":{...},"key":{...},"data":{...}}}}}`.
  Right after configure the device sends the current state of every matching stateful event (64 virtual inputs in 0.1 s):
  that is the plugin's full report (`ReplaceStates`). No Initialized/Changed marker.
- Keep-alive: the device sends nothing on an idle connection (75 s tested) and answers a WebSocket ping in about 1 ms:
  OADM pings every 10 s, no pong within 10 s = connection lost, reconnect with backoff 1, 2, 4, 8, 10 s.
- 30 parallel connections to one camera worked.

## Apps

Verified on an AXIS Q6411-LE (AXIS OS 13.0.88): AXIS Image Health Analytics declares "Image Health
  Analytics - Blur / Block / Redirect / Under-Exposure / Unsuitability / Any-Active-Events" (on/off `active`).
  AXIS Object Analytics declares per scenario `Device1Scenario<id>` named like the scenario plus `Device1ScenarioANY`:
  motion, fence and tailgating are on/off (`active`, `scenarioType`, `classTypes`, `objectId`, `triggerTime`),
  crossline counting and occupancy are one-shot with the counts (`total`, `totalHuman`, ... / `human`, `car`, ...),
  occupancy adds `Device1Scenario<id>Threshold` (on/off). The scenario's **Test alarm** (`sendAlarmEvent`) turns a
  motion / fence / tailgating scenario on for about 6 s; counting scenarios send nothing. Apps that are stopped have no
  events (the trigger shows "The event is no longer on the device").
## Tests

`tests/Oadm.Plugins.DeviceTriggers.Tests` (recorded event list, filters, messages, reports, Any, one-shot,
  connection lost and back, event gone, refused statuses, turn off, types follow save / copy / delete, validation,
  `RobustnessTests`: resolve order and stop, filters only to changed devices, connect limit, refused filter of one
  trigger, turned-off and empty triggers report at once, 5,000 devices,
  removed devices, live read, roles, page and dialog view models incl. 5,000 devices, fake mode, headless
  `device-triggers-page.png`, `device-triggers-editor.png`). Fake mode: `FakeOadmApi.DeviceTriggers.cs`.

## Permissions

`oadm.device-triggers:show` (list, details, reading a device's events) and `:manage` "Change triggers" (`save`,
`delete`, `setEnabled`, `duplicate`). Without "manage" the toolbar is hidden and the on switches are locked. Operator
default: show only. Refusals show the server's text ("Your role does not allow: ...").
