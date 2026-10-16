# Dashboards (planned)

Status: design, agreed with the maintainer on 2026-10-16. Nothing is built yet. Defaults marked **(default)** were not
asked and may change.

## Purpose

Device triggers turn device events into OADM events, and many of them carry values: a temperature, a count, a number
plate, an input that is on or off. **Dashboards** show those values live, like a small Grafana: a dashboard is a grid of
widgets, each showing one value of one device.

## Decisions

| Topic | Decision |
|---|---|
| Name | Core plugin **Dashboards** (`oadm.dashboards`), rail page **Dashboards**, group Monitoring, **off by default**, switchable. **New dashboard** like New view in Live View. |
| Data source | **Subscribe** to OADM's events: the missing half of the event messaging (plugins can publish today, only the event rules can listen). The host only passes events on and stores nothing; the Dashboards plugin keeps the latest value per event, device and field itself. Device triggers first, OADM's own events too. The plugin never talks to devices. |
| One widget | Shows **one field of one event on one device**. A trigger on 40 cameras gives up to 40 widgets if the user wants them all. |
| Field type | **A received event is required**: the widget panel offers a field only after OADM received at least one event with it (from any device of the event); until then it says "Trigger the event once on the device." and updates live when it arrives. The type comes from the device's declaration (yes / no, number, text) and, for fields declared as text, from the received values (all numbers, or all true / false, suggest the better widget). A **Type** select (Automatic, Yes / no, Number, Text) always lets the user force it. A saved widget keeps its type, also after a restart. |
| Sharing | **Like Live View**: dashboards live on the server, every user sees the same ones, every change is pushed to every open page. Rights "show" (Operator and Viewer) and "Change dashboards" (`manage`, Operator yes, Viewer no). |
| After a restart | **Memory only.** On / off events come back by themselves (Device triggers reports every state after a start); other values show "No value yet" until their next event. |
| Old values | The widget keeps the last value, **dimmed**, with the reason below it ("Connection lost", "Unreachable"); hover shows when it arrived ("41.5 at 14:02:17"). |
| Sizes | Every size is **a share of the widget**: a slider 10..100 % (default 60 %); content never overflows, a long text shrinks further to fit. |
| Widgets v1 | **On / Off** (yes / no), **Text**, **Number**. |

## Subscribing to events (host)

OADM has the publish half of its event messaging (`IEventPublisher`: plugins send events to the server) but only one
receiver, the event rules engine. Dashboards needs the **subscribe** half; the server stores nothing new.

- SDK `ICorePluginContext.EventSubscriptions` (`IEventSubscriptions`, DIM null): `Subscribe(handler)` -> a subscription
  (dispose to stop). The handler gets every published event as `PublishedEvent` {type id, device id or none, kind (state
  on, state off, one-shot), the fields as received (name -> text), time}, plus `ReplaceStates` reports as one state per
  device. `Types`: the event types with their fields and kinds (the catalog the rule dialog uses) and `TypesChanged`.
- **A late subscriber still sees the current on / off states**: subscribing starts with a snapshot of every event that is
  on now (the rule engine keeps these states already), then the live events.
- **Never slows the server**: the server hands each event to every subscription's own queue (10,000 events **(default)**,
  oldest dropped and counted, logged at most once a minute) and goes on; a subscription's handler runs on its own loop. A
  throwing handler is logged and keeps its subscription.
- Implementation in `Oadm.Core.Events` next to the publisher's queue: every event already passes there for the rule
  engine; it is passed on to the subscriptions too. A plugin that turns off loses its subscriptions with its context.
- Later plugins can use it too (history, charts, forwarding to other systems).

## Values in the Dashboards plugin

- The plugin's server part subscribes while it is on and keeps the latest value per key (event type id, device id or
  none) in memory: on / off for stateful events, the fields of the last event, the time. At most 100,000 keys
  **(default)**, the least recently updated dropped first; O(1) per event (100,000 keys with a few fields each: about
  50 MB worst case **(to be measured)**).
- Stale: the plugin marks a value stale while the trigger's "connection lost" event is on for that device, the device is
  not Ok (Unreachable, Credentials required, ...; from `ICorePluginContext.Devices`) or the event's source is turned off.
- Stateful events offer their state as a yes / no field **Active** next to their data fields.
- The plugin is off by default and keeps nothing while off: turned on, it starts with the current on / off states (the
  snapshot above); other values appear with their next event.

## Field types

- A field is offered only after the plugin has seen it in a received event (any device of the event). The
  plugin also keeps, per event type and field, the last 20 values seen **(default)** for the suggestion below. Memory only:
  after a server restart a one-shot event must fire again before a **new** widget can use its fields; existing widgets keep
  their saved type and show "No value yet".
- `EventField` gets `Kind` (`EventValueKind`: Text, Number, YesNo; DIM default Text). Device triggers fills it from the
  device's event list: `plugins/Shared/DeviceEvents` reads the `Type` of every `SimpleItemInstance` (today skipped):
  `xsd:boolean` = YesNo; `xsd:int`, `integer`, `long`, `short`, `byte`, unsigned ones, `double`, `float`, `decimal` = Number;
  everything else Text.
- Suggestion for a Text field: the last 20 values **(default)** the plugin saw are all numbers (invariant culture) =
  Number suggested; all `true` / `false` / `yes` / `no` / `on` / `off` = Yes / no suggested. `0` / `1` alone stay Number.
- Reading a value: Yes / no is on for `true`, `1`, `yes`, `on` (any case); Number parses invariant culture; a value that
  does not parse shows "Not a number" (widget dimmed) instead of a wrong reading.

## Page

Layout like Live View (one left card, one right card):

- **Left card** (Collapse / Expand like Live View): **Dashboards** (New dashboard, the list; Edit and Delete at the right of
  each row) and **Events**: the events that carry values, grouped like the rule dialog's event list (each Device trigger,
  then OADM's own events), with a search. Each event lists its fields with their kind icon. No devices here: with 30,000
  devices the device is chosen per widget.
- **Right card**: one row Close all tabs (editing: Name, Save, Cancel), the tabs (`ui:PageTab`, one per open dashboard),
  then the grid.
- **Empty states** (one line each, with the action that exists):
  - Device triggers is turned off: "Dashboards show the values of device triggers. Turn on Device triggers on the Plugins
    page." (administrators get **Open Plugins**).
  - Device triggers is on but has no trigger: "Create a device trigger first." with **Open Device triggers**.
  - No dashboard yet: "Click New dashboard, then drag an event onto the grid."
  - A field with no value yet in a widget: "No value yet".

## Editing

- The grid is Live View's: 20 x 20 cells, always filling the tab, a widget at least 2 x 2, never overlapping, drag inside
  to move, an edge or a corner to resize. **The grid code moves out of the Live View plugin into a shared control** in
  `Oadm.Sdk.Client` (`TileGrid`, `TileFrame`, the layout rules now in `ViewLayout` / `ViewRules`), used by both plugins.
- Dragging an event onto the grid (or clicking it: first free place) opens the widget panel on the right of the grid:
  - **Device**: a button naming the device, opening the shared device picker for one device
    (`DevicePickerWindow.ShowOneAsync`), offering only the devices the event comes from (for a Device trigger, its
    devices).
  - **Field**: the event's fields seen in a received event (Active first for on / off events) with their kind. Before
    the first event: "Trigger the event once on the device." in the panel; the fields appear live when it arrives.
  - **Type**: Automatic (the declared or suggested kind, named: "Automatic (Number)"), Yes / no, Number, Text.
  - **Widget**: only the widgets that fit the type; the first one chosen.
  - **Name**: shown at the top of the widget; default "<field> · <device address>".
  - Then the widget's own settings (below). Every change shows at once in the grid.
- A click on a widget while editing opens the same panel for it. Outside editing a click does nothing in v1.
- At most 50 widgets per dashboard and 200 dashboards **(defaults)**; the server checks every rule again on save (shared
  rules, like `ViewRules`). A save of an older version is refused (two users), like Live View.
- A removed device keeps its widget and says "Removed device 1a2b3c4d"; a removed trigger says "Event no longer exists".

## Widgets

Shared by every widget: **Name** (top of the widget, small, grey, one line, cut with "…"), the stale state and the
tooltip with the value's time.

- **On / Off** (Yes / no): per state (On, Off) an **icon** (the OADM icon set, a searchable icon picker), a **color** (the
  tag palette of eight plus the status colors) and an optional **text** below the icon (empty by default); **Icon size**
  10..100 %. Defaults: On = check circle green, Off = circle grey.
- **Text**: the value as received, **Font size** 10..100 %, **Color**. Longer text shrinks to fit, then is cut with "…";
  the full text is the tooltip.
- **Number**: **Decimal places** 0..6 (default as received), **Unit** (optional, up to 10 characters, shown smaller after
  the value), **Font size** 10..100 %, **Color**, and **Thresholds** (off by default): rows of value and color, sorted by
  value; the value takes the color of the highest threshold it reaches, below the lowest it keeps the normal color
  (example: 20 green, 50 orange, 100 red). Up to 10 rows.

Colors are theme keys (palette names), never raw color values, so dark and light both work.

## Live updates and scale

- The page asks its server part for the values of the widgets of the **open** dashboard tabs only; the server part
  watches exactly those keys in its memory and pushes changes to the page through `ICorePluginContext.Events`
  (at most 4 updates per second per widget, coalesced). Closing a tab stops its watch.
- A dashboard never lists all devices; the Events list holds event types (tens), and the device is chosen through the
  virtualized device picker. 30,000 managed devices change nothing on the page.

## Roles and audit

- "show" (Operator, Viewer): open dashboards. "Change dashboards" (`manage`, Operator yes, Viewer no): create, change,
  delete; save and delete audited ("Saved dashboard", "Deleted dashboard").
- A widget shows values of any device the user can see; no device is changed.

## Tests

- Core: `EventSubscriptionTests` (every publisher call reaches a subscriber, the snapshot of on states, a full queue drops
  the oldest and never blocks the rule engine, a throwing handler, unsubscribe), the type mapping of the event list (`xsd:`
  types).
- Plugin values: the latest value per key, stale states, the key cap, the text suggestion and value parsing.
- Plugin: dashboard rules and versions (two users), the widget settings, Number thresholds, the watch follows the open
  tabs; Perf: 50 widgets updating 4 times a second.
- Client: headless page tests (empty states, editing, the widget panel, the three widgets at small and large sizes, light
  and dark) with screenshots for the docs.

## Build order

1. Subscribe in the host (SDK `IEventSubscriptions`); `EventField.Kind` and the `xsd:` types in Device triggers.
2. Shared tile grid out of Live View into `Oadm.Sdk.Client` (Live View unchanged for the user).
3. The plugin: dashboards on the server, the page with the Events list, editing, the widget panel.
4. The three widgets, thresholds, stale state; docs page "Dashboards" with screenshots.
