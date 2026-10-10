# Event rules (spec, draft)

"IF this happens, THEN do that": the server watches events, evaluates rules and runs actions. Events and actions are
plugins, so new ones (email, webhook, device events of any kind) come without changing the engine.

Status: built 2026-10-10 (implementation notes in `CLAUDE.md`, "Event rules"). Decisions by the user, 2026-10-10.

## Decisions

| Topic | Decision |
|---|---|
| Home | Built into the server (host feature, always there): engine, rule store, event log, rail page. Event sources and actions come from plugins through new SDK interfaces, like task plugins. |
| Built-in events | Only what OADM already knows: device status (Unreachable, Credentials required, Certificate changed) and the HTTPS certificate (expires within 30 days, expired). |
| Plugin events | Every other device event comes from an opt-in plugin. First: Image Health (AIHA detections). Later e.g. a "Device events" plugin (any device event, digital inputs) that keeps event streams open only where the user asked for them. |
| AIHA monitoring | A switch on the Image Health page, "Monitor continuously" (off by default). Only while it is on the plugin checks in the background and publishes events; off = today's behavior (checks only while the page is open). |
| Action runs | Own event log only, never tasks: a rule that fires 200 times a day does not fill the task history. |
| Action target | Per action: the device that fired the event, chosen devices, devices with tags, or all devices. |
| Roles | Administrators create, change, turn on and delete rules (audited). Operators see rules and the event log read-only. |
| Conditions | Simple mode in v1: a list of conditions that must all hold (AND): events (stateful or stateless) and schedules. Advanced mode (AND / OR groups) later on the same data model. |
| VAPIX action | Saved VAPIX Commander command when the rule turns on, optional second saved command when it turns off. |
| Output action | Modes Follow the rule, Pulse for N seconds, Set active, Set inactive, Toggle. |
| Server restart | Re-evaluate, act on real changes: event and rule states are stored; at start the sources report their current states again and only a rule whose state differs from the stored one runs its on / off actions. Unchanged = nothing is sent. |
| Storm limit | Per rule, editable: cooldown per device (default 10 s between two "turns on"), cap per rule (default 60 action runs per minute); beyond that runs are dropped and logged. |
| Several actions | A rule has one to five actions. On each edge they run in the listed order, one after another, each on its targets in parallel (bounded); a failed action is logged and the next one still runs. The rule dialog reorders them (move up / down). |
| Several devices | Actions on chosen / tag / all targets act on the rule as a whole: on when the first device turns on, off when the last one turns off (OR); actions on "the device that fired the event" stay per device. |
| Rule changed or off | Turning a rule off or saving a change while it is on runs its off actions first; a saved rule is then evaluated fresh. |
| Device removed | Its states are dropped; rules that were on for it run their off actions for it. |
| Failed action | No retry in v1: logged as Failed; a Follow output is set again on the next change. |
| Certificate | "Certificate expires soon" = 30 days or less (fixed, like the Devices grid). |
| Fake mode | `--fake` shows sample rules, active states and a live event log (UI, screenshots, docs). |
| UI | Rail page **Event rules** (group Automation) with two tabs: Rules and Event log. |
| Retention | Event log: 30 days or 100,000 entries, whichever comes first, cleaned hourly. |

## Concepts

- **Event type**: declared by a source (built-in or plugin): id (`oadm.device.unreachable`, `oadm.image-health.blur`), name
  ("Blur detected"), group for the picker ("Device status", "Image Health"), **kind** stateful (on / off, e.g. blur detected
  and cleared) or stateless (a pulse, e.g. "rollout failed"), **scope** device (one state per device) or server (one
  state), optional data fields (name, type) a condition can filter on.
- **Event**: one occurrence: type, device (device scope), time, for stateful types the new state (on / off), data. The
  engine keeps the current state of every stateful (type, device) in memory.
- **Rule**: name, enabled, conditions, actions, limits. Stored in the database.
- **Condition** (simple mode, all must hold):
  - Event condition: event type + which devices (all, chosen, tags) + optional data filter (field equals value).
  - Schedule condition: weekdays + from / to, in the server's time zone (stateful: on inside the window).
  - At most one **stateless** event condition per rule (two pulses never arrive at the same moment).
- **Rule state**:
  - Only stateful conditions: the rule is **stateful**, on while all are on, off as soon as one is off. Evaluated per
    device for device-scoped conditions ("blur on camera X" and "camera X reachable" join on the device); a schedule
    applies to every device.
  - With a stateless event condition: the rule **fires** each time that event arrives while all stateful conditions
    hold (for its device).
- **Action**: action type (plugin id), target (event device / chosen / tags / all), settings (JSON, checked by the
  plugin), and what it does on the edges: stateless rule = "when it fires"; stateful rule = "when it turns on" and,
  when the action supports it, "when it turns off" (Follow, second VAPIX command).

## SDK (draft)

```csharp
// Oadm.Sdk.Events
public enum EventKind { Stateless, Stateful }
public enum EventScope { Device, Server }

public sealed record EventField(string Name, string DisplayName, EventFieldType Type, IReadOnlyList<string>? Values = null);
public sealed record EventTypeInfo(string Id, string DisplayName, string Group, EventKind Kind, EventScope Scope,
    IReadOnlyList<EventField> Fields);

public interface IEventSource              // a plugin (or a core plugin's member) that publishes events
{
    IReadOnlyList<EventTypeInfo> EventTypes { get; }
}

public interface IEventPublisher           // ICorePluginContext.Events2 / EventPublisher (DIM null)
{
    void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null); // stateful
    void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null);          // stateless
    void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices); // stateful: the full picture, every device
                                           // not listed is off (server scope: empty = off, [Guid.Empty] = on)
    bool IsWatched(string typeId);         // an enabled rule uses the type: sources may skip work otherwise
}
// Contract for stateful sources (user decision 2026-10-10): off is the default, no initial value is declared. After every
// start (and after being turned on) a source sends one ReplaceStates per stateful type it serves: that is its "ready".
// Until then the engine keeps the stored states and runs nothing for the type. Later it may send Set per change, or
// ReplaceStates again at any time (e.g. after each check round); the engine diffs. A source turned off (plugin off,
// "Monitor continuously" off) counts as ReplaceStates(type, []). Stateless types have no state and need nothing.

// Oadm.Sdk.Actions
public enum ActionEdge { Fired, TurnedOn, TurnedOff }

public interface IActionPlugin : IPlugin
{
    string Group => "General";             // picker group
    bool SupportsTurnOff { get; }          // has a "when the rule turns off" part
    bool NeedsDevice { get; }              // acts on devices (target picker shown)
    IReadOnlyList<ActionField> Fields => [];  // declarative settings: the host draws the form, no client part needed
                                           // (text, multiline, number, bool, choice, password (encrypted, never sent back),
                                           // device output port, saved VAPIX command); empty = the plugin's IActionEditor
    IReadOnlyList<string> Validate(string settingsJson);           // field errors, before a rule is saved
    string Describe(string settingsJson);  // one line for the rule list ("Pulse output 1 for 5 s")
    Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct);
}

public sealed record ActionInvocation(Guid RuleId, string RuleName, ActionEdge Edge, string SettingsJson,
    IDeviceInfo? Device, EventSnapshot Trigger);   // one invocation per target device

public interface IActionContext
{
    IVapixClient? Vapix { get; }           // pre-authenticated for Device (null without a device)
    ILogger Logger { get; }
    ICorePlugin? Owner { get; }            // contributing core plugin (VAPIX Commander: saved commands)
    void Log(string message);              // into the event log entry of this run
    ISettingsStore Settings { get; }       // the plugin's own server settings (e.g. an SMTP server)
    ISecretProtector? Secrets { get; }     // password fields are stored encrypted and handed over decrypted here only
    string Text(string template);          // fills {rule}, {event}, {device}, {address}, {model}, {state}, {time}
}

// ICorePlugin gains (DIM empty): IReadOnlyList<IActionPlugin> ActionPlugins, IReadOnlyList<EventTypeInfo> EventTypes.
// Standalone IActionPlugin / IEventSource classes in a plugin package are found by the loader like task plugins.

// Oadm.Sdk.Client
public interface IActionEditor             // settings form inside the rule dialog
{
    string ActionId { get; }
    Control CreateEditor(IActionEditorContext ctx, string? settingsJson); // ctx: Query (read-only, e.g. list outputs
                                           // of a device, list saved commands), devices, validation state
    string? GetSettings(Control editor);   // null = invalid (errors shown below the fields)
}
```

Writing a new action (email, webhook, MQTT, anything): one class implementing `IActionPlugin` with its `Fields`, in a
plugin project like a task plugin (`plugin.json`, `<Name>.Server.dll`); no client project unless it needs a special form.
Example: an email action declares To, Subject, Body (with `{device}` placeholders) and reads its SMTP server from its own
plugin settings. The loader registers it and the rule dialog lists it at once. A sample action goes into
`tests/TestPlugins` and the guide into `plugins/README.md`.

Names are a draft; the review settles them. Plugins that are turned off: their event types and actions disappear from
the pickers; rules using them stay, show "Not available: <plugin> is turned off" and do not run.

## Engine (server)

- `Oadm.Core.Events`: `EventBus` (bounded channel, publishers never block: `Set` / `Fire` only enqueue; full = oldest
  dropped and counted), `EventStateTable` (current states), `RuleEngine` (one loop: applies an event to the state
  table, re-evaluates only the rules that use its type, computes edges per (rule, device)), `ActionRunner` (bounded
  parallelism, default 16; per action 30 s timeout; device actions refused without a request for Credentials required /
  Password not set / Certificate changed, like tasks).
- Limits per rule: cooldown per device and runs per minute; a dropped run is logged once per rule per minute
  ("Rule paused: too many events").
- No loops: actions cannot fire events directly. An action that changes a device may cause a device event later; the
  cooldown keeps that bounded.
- State is persisted: the current state of every stateful (type, device) and every stateful (rule, device) in tables
  `EventStates` / `RuleStates`, written with the event (batched, at most once per second).
- Start: the stored states are loaded as "last known" without running anything. The built-in device status source
  re-publishes from the device table, plugins publish their current states once they run (Image Health after its first
  background check). Each source reports when its first round is complete; then a state it did not report again counts
  as off. Only a rule whose state now differs from the stored one runs its on / off actions (e.g. the blur cleared while
  the server was down: the relay opens; the rule is still on: nothing is sent). Stateless events missed while the server
  was down are not replayed. A source that never comes back (plugin turned off) is treated as off after 2 minutes.
- Built-in source `oadm.device`: from the polling results (status change, certificate days). Stateful, device scope.

## First actions

1. **Run VAPIX command** (contributed by VAPIX Commander, so it needs that plugin on): when the rule turns on / fires a
   saved command, optional second saved command when it turns off. Values: the saved command's stored values (password
   fields from the stored secrets); placeholders `{{event.device}}`-style data later. Uses Commander's `CommandExecutor`;
   the result line goes into the event log. Commands that change the device are allowed (the rule is the confirmation);
   commands marked dangerous need an extra check box in the action editor.
2. **Set output** (bundled always-on plugin `oadm.outputs`, `plugins/Oadm.Plugins.Outputs`): device output port (the editor reads the device's ports
   through a read-only query), mode Follow / Pulse N s (1..3600) / Set active / Set inactive / Toggle. Follow and the
   off edge only on stateful rules. VAPIX: I/O port management API (`io/portmanagement.cgi`, `setStates`), fallback
   `io/port.cgi` on older firmware. To verify on hardware (needs a device with a relay or output).

## AIHA events (Image Health plugin)

- Page switch "Monitor continuously" (plugin setting, Admin, default off). On: background check every 30 s (24 cameras at
  a time, 3 s each, the existing monitor), events published per camera:
  `oadm.image-health.blur|block|redirect|under-exposure|unsuitability` (stateful: on = detected; pending is not on),
  `oadm.image-health.not-running` (stateful: app installed but stopped), `oadm.image-health.unreachable` (stateful:
  could not be checked). The page shows the same rows and cards.
- Off: no background request, as today; the event types stay listed, rules using them do not fire (states go off).

## Event log

- Table `EventLogEntries`: time, kind (Event, Rule turned on, Rule turned off, Rule fired, Action, Dropped), event type,
  device, rule, action, result (Ok, Failed, Skipped), message. Events no rule uses are not logged (busy sites).
- Retention 30 days or 100,000 entries, hourly (`Events.LogRetentionDays`, `Events.LogMaxEntries`).
- gRPC `EventService`: `ListTypes`, `ListActions`, `ListRules`, `SaveRule`, `DeleteRule`, `SetRuleEnabled` (Admin, audited),
  `ListLog` (paged, filters), `WatchLog` (stream), `ListActiveStates`, `QueryAction` (read-only query of an action plugin
  for its editor).

## UI

- Rail page **Event rules** (Automation, icon `zap`). Tab **Rules**: grid On (switch, Admin), Name, Conditions ("Blur
  detected · Mon-Fri 08:00-18:00"), Actions ("Pulse output 1 for 5 s on 10.0.0.70"), State (on / off / devices on),
  Last fired; New rule, Edit, Duplicate, Delete (red, confirmed). Rule dialog: Name, Conditions card (add event / add
  schedule rows; device picker all / chosen / tags), Actions card (add action, per action the target and the plugin's
  editor; for stateful rules "When the rule turns off" where supported), Limits (cooldown, runs per minute). Errors below
  the fields; Save disabled with the reason.
- Tab **Event log**: virtualized grid (time, what, device, rule, action, result chip, message), SearchBox, filters,
  live (WatchLog), 5,000+ rows O(n).
- Image Health page: the "Monitor continuously" switch.

## Later

Advanced conditions (AND / OR groups), more actions (email, webhook, MQTT, run a task plugin), a "Device events" plugin
(device event streams, digital inputs), event data placeholders in actions, a "Test rule" button, rule import / export.

## Implementation notes (2026-10-10)

- The first condition is the trigger ("When"); only it may be one-shot. Further conditions on events use "the same device"
  (joined on the trigger's device) or "any" of All / chosen devices / tags; a schedule applies to every device.
- Only the rule states are stored (`EventRuleStates`: the subjects whose "turns on" actions ran); event states live in
  memory and come back through each source's full report after a restart. A rule waits until every event type it uses is
  ready (2 minutes at most).
- Actions without devices (email) run with every edge, like actions on the event's device.
- The output number is typed (1..64) instead of read from the device, so it works for every target mode.
- Library commands of VAPIX Commander can be used when every field has a default; saved commands keep their values.

## Open questions

- Resolved 2026-10-10 on a loan AXIS A9210: both output APIs work (see `plugins/Oadm.Plugins.Outputs/README.md`).
