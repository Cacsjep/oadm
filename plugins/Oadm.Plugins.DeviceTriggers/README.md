# Device triggers (`oadm.device-triggers`)

Core plugin with a rail page (group Automation, icon `bell`), **off by default**. Spec and decisions:
`docs/specs/device-triggers.md`. An administrator picks an event that the chosen devices offer (I/O ports, virtual inputs,
temperature, storage, Object Analytics scenarios, events of other apps), optionally narrowed to source values; OADM keeps
one WebSocket event stream per device and publishes every trigger as two event types for the event rules.

- **Event types** (`IDynamicEventSource`, they change with the triggers): `oadm.device-triggers.<id>` named like the
  trigger (on/off or one-shot like the device's event), and `oadm.device-triggers.<id>.lost` "&lt;Trigger&gt;: connection
  lost" (on/off). Group "Device triggers". Disabled triggers keep their types (their rules stay valid, the events are
  off); a deleted trigger's types disappear (its rules show "Not available: an event of this rule no longer exists").
- **Event list** (`Device/EventDeclarations`): SOAP `aev:GetEventInstances` at `/vapix/services`, read-only. Topics marked
  `isApplicationData` (on the topic or a parent) are left out. The editor reads it live for every create and edit
  (`startRead` / `readStatus`, 16 devices at a time); a connection reads it on every connect and every 5 minutes.
- **Stream** (`Runtime/DeviceConnection`, one per device with a turned-on trigger): `IVapixClient.ConnectWebSocketAsync`
  (session token from `wssession.cgi`, wss with the pinned certificate, ping every 10 s), then `events:configure` with one
  filter per topic (narrowed values joined with "or", "Any" = the whole topic); what arrives within 1.5 s after the reply
  is the device's report of its current states. Trigger changes set a new filter on the open socket. Reconnect after 1, 2,
  4, 8, 10 s. Devices with Credentials required, Password not set or Certificate changed are not contacted.
- **State** (`Runtime/TriggerRuntime`): per trigger and device the state of every matching source; on while at least one
  is on. The first report of every device (or a problem) makes the trigger report to the rules (`SetState` with the
  event values, then `ReplaceStates`); afterwards each change is one `SetState`, one-shot events are `Fire`. A problem keeps
  the last state; after 30 s "connection lost" turns on with `{reason}`; the next report turns it off.
- **Methods** (`DeviceTriggersMethods`): `list`, `get`, `startRead`, `readStatus` (Operator); `save`, `delete`,
  `setEnabled`, `duplicate` (Admin, audited). Event `changed` (page reload, at most every 300 ms on the page). Triggers are
  stored in the plugin setting `triggers`; at most 200 triggers, 5,000 devices each. Devices removed from OADM leave their
  triggers (checked every 30 s).
- **Page**: triggers (On, Name, Event, Devices, State, Last event; New trigger, Edit, Duplicate, Delete) and the devices of
  the selected one (Connection, State, Last event). Dialog: name, turned on, devices with Read events, event (the events
  all chosen devices offer; a device without the chosen event is shown "Does not offer this event" and blocks Save), one
  select per source item (Any or a value).
- Verified on 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11): 42 events, token login over HTTPS, the report after the filter,
  and virtual input 64 on and off (`DeviceTriggersHardwareTests`, `OADM_TRIGGER_URL` / `_USER` / `_PASSWORD`; the write
  test is `Category=HardwareWrite`).
- **Apps** (loan AXIS Q6411-LE, AXIS OS 13.0.88, 2026-10-10): AXIS Image Health Analytics declares "Image Health
  Analytics - Blur / Block / Redirect / Under-Exposure / Unsuitability / Any-Active-Events" (on/off `active`).
  AXIS Object Analytics declares per scenario `Device1Scenario<id>` named like the scenario plus `Device1ScenarioANY`:
  motion, fence and tailgating are on/off (`active`, `scenarioType`, `classTypes`, `objectId`, `triggerTime`),
  crossline counting and occupancy are one-shot with the counts (`total`, `totalHuman`, ... / `human`, `car`, ...),
  occupancy adds `Device1Scenario<id>Threshold` (on/off). The scenario's **Test alarm** (`sendAlarmEvent`) turns a
  motion / fence / tailgating scenario on for about 6 s; counting scenarios send nothing. Apps that are stopped have no
  events (the trigger shows "The event is no longer on the device").
- Tests: `tests/Oadm.Plugins.DeviceTriggers.Tests` (recorded event list, filters, messages, reports, Any, one-shot,
  connection lost and back, event gone, refused statuses, turn off, types follow save / copy / delete, validation,
  removed devices, live read, roles, page and dialog view models incl. 5,000 devices, fake mode, headless
  `device-triggers-page.png`, `device-triggers-editor.png`). Fake mode: `FakeOadmApi.DeviceTriggers.cs`.
