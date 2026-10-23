# Event rules

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Built-in feature (not a plugin): "IF events THEN actions".
Events and actions come from plugins (SDK `Oadm.Sdk.Events`, `Oadm.Sdk.Actions`), the server keeps the rules, evaluates
them and logs what happened. Implementation `Oadm.Core.Events`, gRPC `EventService` (`events.proto`), client
`EventRules/`.

| Topic | Decision |
|---|---|
| Built-in events | Only what OADM already knows (`BuiltInEvents`, stateful per device): Device unreachable, Credentials required, Certificate changed (device status from polling), Certificate expires soon (30 days or less, fixed), Certificate expired (rechecked hourly). Each has an **Any device** twin for the server (group "Any device", `BuiltInEvents.AnyOf`): on while at least one device has it, value `count` (how many), recomputed by the engine with every device change. Every other device event comes from the opt-in plugin **Device triggers** (any event a device offers, see "Device triggers"; also the AXIS Image Health Analytics detections, the Image Health Dashboard is a live display only). |
| Kinds | Stateful (on / off per device or for the server) or stateless (one shot). Off is the default; a stateful source sends one full report (`ReplaceStates`) per type after every start ("ready"), then `SetState` per change or `ReplaceStates` again; a source that is turned off counts as everything off. |
| Simple mode | Conditions must all hold. The first is the trigger ("When": an event, devices All / chosen / tags); further ones ("And") are stateful events (on "the same device", or "any" of All / chosen / tags) or schedules (weekdays + from / to in server time, over midnight allowed). Only the trigger may be one-shot. Advanced AND / OR later on the same model. |
| Rule state | All events stateful: the rule is on per subject (the trigger's device, or the server) while all conditions hold. One-shot trigger: the rule fires while the others hold. |
| Actions | One to five per rule, run in the listed order on each edge, each on its targets in parallel (16 at once for all rules, 30 s per run); a failure is logged and the next one runs; no retry. Edges run in order per rule and subject (a "turns off" never overtakes its "turns on"; edges with actions on chosen / tag / all devices also in order among themselves); different devices never wait for each other. Targets: the device of the event, chosen devices, devices with tags, all devices. Actions on chosen / tag / all devices run when the first subject turns on and when the last one turns off; actions on the event's device and actions without devices run per subject. |
| Limits | Per rule, editable (security names): **Dead time** per device (default 10 s between two "turns on"), **Max per minute** (default 60 runs per minute for the whole rule); a suppressed "turns on" also suppresses its "turns off"; "Rule paused" logged once a minute. |
| Changes | Turning off, changing or deleting a rule that is on runs its off actions first; a changed rule is then evaluated fresh. A removed device goes off. A plugin turned off: its events go off, rules show "Not available: <plugin> is turned off". |
| Loops | Actions never fire events themselves; a device an action changes may send an event later, which the dead time keeps bounded. |
| Restart | The subjects each rule was on for are stored (`EventRuleStates`); a rule is evaluated once every event type it uses is ready (or after 2 minutes); only real differences run actions; one-shot events missed while the server was down are not replayed. |
| Roles | Administrators create, change, copy, turn on / off and delete rules (audited "Created / Changed / Deleted / Turned ... event rule"); operators read rules and the log. |
| Log | Own event log (`EventLogEntries`), never tasks: events a rule uses, rule edges, every action run with its result; 30 days or 100,000 entries (`Events.LogRetentionDays`, `Events.LogMaxEntries`, hourly). |

- **Device groups** (`docs/spec/device-scope.md`, decision 2026-10-23): event rules are for users with all devices.
  Every `EventService` method (rules, the event log, the catalog, Test) is `AccessRule.AllDevices`: users with a device
  group get PERMISSION_DENIED "Only users with all devices can do this." and the client hides the Event rules page for
  them. Rules have no owner and run on all devices as before device groups (migration `EventRulesWithoutOwner` drops the
  owner columns of the unreleased migration `DeviceGroups`); a deleted group changes no rule. Send HTTP request and Send
  TCP message send to any address, as in 4.1.0.
- **SDK**: `IEventSource` (a core plugin declares `EventTypeInfo`: id, name, group, kind, scope, data fields) and
  `ICorePluginContext.EventPublisher` (`IEventPublisher`: `SetState`, `Fire`, `ReplaceStates`, `IsWatched`; only queues).
  `ICorePluginContext.EventSubscriptions` (`IEventSubscriptions.Subscribe(handler)`, `Core.Events.EventSubscription`):
  every event the engine handles (plugins' and built-in) reaches each subscription as `PublishedEvent` (On / Off / Fired,
  values, time), starting with a snapshot of the events that are on; each subscription has its own queue (10,000, oldest
  dropped, logged once a minute) and loop, so the engine never waits; a failing handler is logged; `IsWatched` is true
  while any subscription exists; the host ends a plugin's subscriptions when it stops, fails or is turned off
  (`PluginEventSubscriptions`). `IEventSubscriptions.Types` (`SubscribedEventType`: the type with its fields, source, available)
  and `TypesChanged` (a source changed its types, a plugin turned on or off; unhooked when the plugin stops). Tests
  `EventSubscriptionTests`. First user: Dashboards (see "Dashboards").
  `EventTypeInfo.DeviceIds`: the devices that can send the event (Device triggers: each trigger's devices now, types changed
  again when they change), null = any device. `EventField.Kind` (`EventValueKind` Text, Number, YesNo) says what a value holds; `EventField.Filter` false = a value
  only (placeholder, no filter box in the rule dialog; proto `EventFieldInfo.no_filter = 4`). Device triggers declare every
  value this way, the kind from the device's declared type.
  `IActionPlugin` (standalone class with a parameterless constructor in a `*.Server.dll`, or contributed through
  `IActionSource.ActionPlugins` of a core plugin): `NeedsDevice`, `SupportsTurnOff`, declarative `Fields` (`ActionField`:
  Text, Multiline, Number, Bool, Choice, DynamicChoice through `ListChoicesAsync`, Password stored encrypted and never sent
  back, Code = a body in the code editor colored by `Language` or by the value of the field `LanguageKey` (json, xml, html,
  text, form), KeyValues = name / value rows stored as `[{"name","value"}]` (at most 50, one line each,
  `ActionInvocation.GetPairs`); `VisibleWhenValue` may list several values "basic|digest"), `UsesPlaceholders` (the rule
  dialog shows the Placeholders list), `Validate`, `Describe`,
  `RunsOn(edge)`, `ExecuteAsync(IActionContext, ActionInvocation)`; `IActionContext` gives a pre-authenticated `Vapix` for
  the target, the plugin's settings, `Log` and `Text(text)` / `Text(text, PlaceholderEscape)` (None, Json, Xml, Url: each
  value escaped for the format it goes into). The placeholders are one list,
  `Oadm.Sdk.Actions.ActionPlaceholders.Fixed` (everything OADM knows): {rule} {event} {state}
  {time} {date} {timeUtc}, device {device} {address} {hostName} {model} {serial} {mac} {firmware} {category} {tags}
  {status} {webUrl} (the action's device, else the event's device), then every value of the event by its name; filled in
  one pass (`ActionPlaceholders.Fill`: a value holding "{x}" is never filled again, unknown names stay). The client draws the settings form from `Fields` (`ActionSettings` checks
  shared by server and dialog); no client part is needed. The rule model `RuleDefinition` (JSON, camelCase) lives in
  `Oadm.Sdk.Events` and is the definition JSON of gRPC.
- **Server** (`Oadm.Core.Events`): `EventCatalog` (built-in + plugin types and actions, with availability), `RuleStore`
  (table `EventRules`: definition JSON with password values encrypted, purpose `oadm:event-rule:<rule>:<action key>:<field>`;
  `ForClient` removes them and lists `StoredSecretFields`; an empty field keeps the stored value; at most 500 rules),
  `RuleValidator`, `RuleEngine` (one loop owns all state; publishers, the device feed and rule changes only queue; built-in
  events from `DeviceChangeFeed`; ticks every 15 s for schedules and ready timeouts), `ActionRunner` (edges of one rule in
  order), `EventLog` (queued, batched every 500 ms, live watch), `EventRulesService`, `RuleTexts` (summaries). The engine
  starts after the plugin activation and before the core plugins (`OadmServerHost.StartAsync` 4b); `CorePluginHost` hands
  a publisher to every core plugin that implements `IEventSource`. Actions wait until the core plugins have started
  (`ActionRunner.PluginsStarted`, after `CorePluginHost.StartAllAsync`). Rule states are dropped from memory only after a
  successful write and written once more at stop; a saved / turned on or off / deleted rule always reaches the engine (it
  waits for room in a full queue, events are dropped). Saving a rule whose action's plugin is off runs only the shared field
  checks. The catalog is an id-indexed snapshot rebuilt when plugins or event types change; subject-independent conditions
  are evaluated once per re-evaluation; a chosen device or tag counts only while OADM manages the device. Types can change while the server runs
  (`IDynamicEventSource.EventTypesChanged`, device triggers): the registry reads them again, a new stateful type waits for
  its first report, a removed type counts as off ("Not available: an event of this rule no longer exists"), and a type a
  stored rule uses that no source declares yet waits like a source after a start (2 minutes), then counts as off.
  Placeholders also fill every value of the event by its name (`{port}`, `{total}`) after the fixed ones.
- **gRPC** `EventService`: `GetCatalog`, `ListRules`, `WatchRules` (list on subscribe and after every change, at most every
  500 ms), `GetRule`, `SaveRule`, `DeleteRule`, `SetRuleEnabled`, `DuplicateRule` ("<name> (copy)", off, passwords kept),
  `ListActionChoices` (the last five Admin), `ListLog` (newest first, limit 0 = 10,000, optional rule filter), `WatchLog`,
  `TestAction` (Admin, audited "Tested event rule action": runs an action without devices once now, outside rule states and
  limits, with "sample" (or the first listed value) for every value of the trigger and the trigger's first chosen device as
  the device of the event; empty passwords take the stored ones of the rule's action; nothing in the event log; reply ok +
  message).
- **Notifications** (bundled always-on package `oadm.notifications`,
  `plugins/Oadm.Plugins.Notifications`, server only, plugin `README.md`): **Send HTTP request** (method, URL, query and
  header rows, login None / Basic / Digest / Bearer token, body type None / JSON / XML / Text / Form in the code editor,
  "Allow untrusted certificates", timeout, success = 2xx) and **Send email** (MailKit; mail server, port, STARTTLS / TLS /
  none, optional login, "Allow untrusted certificates", From, To, Subject, Format Text / HTML; the server settings live in
  each action, no shared setting) and **Send TCP message** (host, port, Text / JSON / XML / Hex bytes, line end, optional TLS
  with "Allow untrusted certificates", "Wait for an answer" + "Answer must contain", timeout). All three run without devices,
  once each time the rule turns on or fires, optionally also when the rule turns off.
- **First actions**: **Set output** (bundled always-on package `oadm.outputs`, `plugins/Oadm.Plugins.Outputs`, server only):
  output number 1..64, modes Follow the rule (stateful rules only; inactive on the off edge), Pulse N s (1..3600), Set
  active, Set inactive, Toggle; I/O port management API (`io-port-management`: getPorts, setPorts, setStateSequence; active
  = state differs from the normal state; inputs refused; the log names the port, "Output 1 (RELAY) active") when listed,
  else `port.cgi` (`action=n:/`, `n:\`, `n:/ms\` (answers after the pulse: the action asks for the pulse + 15 s through
  `IActionPlugin.MaxRunTime`), `checkactive=n` for Toggle; `checkoutput` is refused by the A9210). Output n = port "n-1".
  Verified with the opt-in `OutputHardwareWriteTests` (`OADM_OUTPUT_WRITE_URL` / `_USER` / `_PASSWORD` / `_PORT`) on an
  AXIS A9210 (AXIS OS 12.10.73.2, io-port-management 1.1, output 1 = RELAY), every mode on both APIs. **Run VAPIX command** (contributed by VAPIX
  Commander, available while it is on): a saved command (stored values and passwords) or a library command whose fields all
  have defaults, optional second command when the rule turns off, "Allow dangerous commands" required for dangerous ones;
  device safety like a rollout (cached API list, fresh one before a write). The event's values reach the command: a text field of the saved command whose value holds placeholders ("{carID}", "{port}", "{device}")
  is filled before the field checks and the send (`RunCommandAction.FillEventValues`; passwords never). Event type
  descriptions list their values one per line ("Values:" then "• {name}"), shown in the info icon of the rule dialog.
- **Client**: rail page **Event rules** (key `HostPages.EventRules`, icon `zap`, group Automation before the plugin pages of
  that group; `EventRulesViewModel` + `EventRulesView`): rules on top, the **Event log** card below with a splitter, like the Devices
  page and its tasks (no tabs). **Rules** (toolbar New rule first, Edit, Duplicate,
  Delete (red, confirmed); search on the right; grid On check box (administrators), Name, When, Then, State chip (On ok, waiting accent, problem
  warning), Last run; double click edits; search; status "3 rules · 2 turned on · 1 active now"; live through
  `WatchRules`) and **Event log** (title, Refresh, **Delete all** (administrators, red, confirmed; gRPC `ClearLog`, audited
  "Cleared event log"), search, virtualized grid Time, Type, What, Device, Rule, Result chip + message (events and rule edges
  the blue info icon, Done ok, Failed error, Skipped warning; a skip message does not repeat "Skipped");
  newest 10,000, live through `WatchLog`, read and watched again after a drop with the rules' backoff (1, 2, 4 ... 15 s);
  errors of rule actions stand below the rules list, errors reading the log below the log). Rule dialog `RuleEditorWindow` (`RuleEditorViewModel`), a wizard with
  the steps as a segmented switch (a step opens when every step before it is complete): **1 Rule** (Name and Turned on in
  one row, Limits), **2 When** (Conditions: "When" / "And" rows, Add event, Add schedule, day toggles), **3 Then** (Actions:
  "Then" rows: action, On (targets), the settings form (code fields in `ui:CodeEditor`, name / value rows with Add and
  remove icons, every setting `FormField.wide`), move up / down, remove; actions without devices have **Send test** with
  the result chip next to it (`TestAction`); Add action up to five; right of the actions the **Placeholders** card, only while an action of the rule writes texts
  (`IActionPlugin.UsesPlaceholders`, `ActionEntry.uses_placeholders = 11`: Send email, Send HTTP request, Send TCP message; a list with copy buttons): "Event: <trigger>" with the trigger's values, then Rule and Device from
  `ActionPlaceholders.Fixed`, each "{name}" + a short description + a copy icon; window 1160 wide), **4 Overview** (one
  short line per part: Name, State, When, And, Then, Limits; Save). Footer Back / Next (disabled with the step's first
  problem as tooltip) and Save on the overview. The devices of a condition or an action have no mode drop-down: one purple
  button that says what is chosen ("Choose devices" while nothing is, else "Selected devices: All" / "Selected devices: Tag
  Building A and 3 devices", `DeviceSelectorTexts.Brief`; the addresses as tooltip; the same button in the trigger editor)
  opens the shared `DevicePickerWindow` with every option (`DevicePickerOptions.Full`): "The same device" / "The
  device of the event", All devices, or chosen devices and tags together; the event's and the action's description sit in an info icon right of their
  select; the When / And / Then rows are outlined (`Border.ruleItem`, no fill). Server: `DeviceSelectorMode.Devices` (and the older `Tags`) = the listed devices
  plus every device with one of the tags (`DeviceSelector.ChoiceMatches`, `IsEmptyChoice`: "choose at least one device
  or tag"). The limits are **Dead time (s)** and **Max per minute** with their explanation in an info icon
  (`FormField.Info`); errors below their field; a server refusal below the overview. Fake mode (`FakeOadmApi.Events.cs`): catalog, three sample rules,
  a sample log.
- **Tests**: `tests/Oadm.Core.Tests/Events` (the engine with a fake clock), `tests/Oadm.Server.Tests/EventServiceTests`,
  `tests/Oadm.Plugins.Outputs.Tests` (+ opt-in `OutputHardwareWriteTests`), `tests/Oadm.Plugins.Notifications.Tests`,
  `RunCommandActionTests` (VAPIX Commander), `tests/Oadm.Client.Tests/EventRulesTests` (5,000 devices).
