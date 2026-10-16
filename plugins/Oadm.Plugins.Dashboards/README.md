# Dashboards (`oadm.dashboards`)

Core plugin (+ `.Client`), rail page **Dashboards** (group Monitoring, icon `gauge`), **off by default**, switchable. Live
values of OADM's events (mostly device triggers) and live video on saved dashboards, like a small Grafana: a dashboard is a
grid of widgets, each showing one value of one device. Spec and decisions: `docs/dashboards.md`.

## Decisions

| Topic | Decision |
|---|---|
| Data | The server part subscribes to every event the server handles (`ICorePluginContext.EventSubscriptions`) and keeps the latest per event type and device in memory (`ValueStore`): on / off for an on / off event, the last value of each field (an event without a field keeps the field's value), the time. At most 100,000 keys, the least recently updated dropped first, O(1) per event. Memory only: turned off or restarted it starts empty; the subscription's snapshot brings the events that are on. It never talks to a device. |
| Fields | An on / off event offers **Active** (`@active`, yes / no) at once; other fields only after an event with them arrived (any device). Kind: the declared `EventField.Kind`, for text the last 20 values suggest Number (all numbers, invariant culture; `0` / `1` stay Number) or Yes / no (all true / false / yes / no / on / off). `FieldValues`. |
| Old values | `ValueSnapshot.Stale`: the event no longer exists ("Event no longer exists"), its plugin is off ("Device triggers is turned off"), the trigger's "connection lost" (`<type id>.lost`) is on for the device ("Connection lost"), or OADM's own status event of the device is on ("Unreachable", "Credentials required", "Certificate changed"). OADM's own status events and the lost events are never old themselves. The widget is dimmed with the reason below. |
| Watching | The page watches the keys of the widgets in its **open** tabs (`watch` with its own id, renewed every 10 s; `unwatch` when the page closes; forgotten 30 s after the last renewal). Changes of watched keys are pushed as event `values` every 250 ms at most (several events of one widget in between: one update with the latest). A status or "connection lost" change marks the device's watched values; a type change marks all. The catalog (event `catalog`) is pushed at most once a second when a new field was seen or the types changed. |
| Storage | Like Live View: plugin setting `dashboards` (JSON, enums as text), shared by every user, every change pushed (event `dashboards`). A save of an older version is refused ("Another user changed the dashboard X meanwhile. Click Cancel and make your changes again."); an unreadable stored list is kept as `dashboardsUnreadable`. |
| Grid | The shared `ui:TileGrid` with `DashboardsPluginInfo.Layout` (20 x 20 cells, at least 2 x 2, never overlapping); a dropped value takes up to 4 x 4, a camera up to 8 x 8 (`VideoLayout`). |
| Limits | 50 widgets per dashboard, of them 16 live videos; 200 dashboards; names 1..64 (widget names may be empty); unit 10 characters; 10 thresholds; sizes 10..100 % of the widget; colors and icons only from `WidgetColors` / `WidgetIcons` (theme brushes by name: dark and light both work). The server checks all of it again (`DashboardRules`). |
| Widgets | **On / Off** (icon, color and optional text per state, icon size), **Text** (font size, color; shrinks to fit, then "…"), **Number** (decimal places or as received, unit, font size, color, thresholds: the color of the highest threshold the number reaches; a value that is no number shows "Not a number" dimmed), **Live video** (the host's live video component like a Live View tile: controls closed, fullscreen with a double-click, the channel buttons; the channel chosen while editing is saved). `WidgetDisplay` (no UI) decides what a widget shows; `WidgetView` draws it (`FitText`, `FitBox`). |
| Roles | "show" (automatic, Operator and Viewer), **Change dashboards** (`manage`: save, delete; Operator yes, Viewer no; both audited). Video needs "Live view"; without it the widget says "Your role does not allow live view.". |
| Page | Left card (Collapse / Expand like Live View): **Dashboards** (+ New dashboard; Edit and Delete in each row), **Cameras** (`ui:DeviceTree`, devices with video), **Events** (the available events grouped, device triggers first, each with its fields and their kind; search). Right card: Close all tabs, while editing Name / Save / Cancel, the tabs (`ui:PageTab`); the grid; while editing the panel of the selected widget right of the grid (a new `WidgetEditorView` per widget). Empty states: Device triggers off ("Turn on Device triggers on the Plugins page", `ICorePluginClientContext.IsPluginOn`), no trigger yet ("Create a device trigger first." + **Open Device triggers**), no dashboard yet. |
| Editing | New dashboard opens a tab in edit mode ("Dashboard N"). An event dropped or clicked becomes a widget of its first field with the widget that fits (Number for a number, On / Off for yes / no, else Text) and opens its panel: Device (the shared picker for one device, every managed device; not for a server event), Value, Type (Automatic (X), Yes / no, Number, Text), Widget (only those that fit the type), Name (default "<value> · <device address>", follows the value and device until the user changes it), then the widget's settings. Every change shows in the grid at once. A camera dropped or clicked becomes a live video widget. Save checks the rules and that every widget of a device event has a device. |
| Deviations from the spec | The device picker offers every managed device (a trigger's devices are not known to the plugin; an on / off value of a device that never sent the event reads Off). No **Open Plugins** button for administrators (a page cannot tell an administrator). The channel of a video widget is chosen with the picture's channel buttons, not in the panel. |

## Methods (`InvokeAsync`)

| Method | Payload | Answer | Permission |
|---|---|---|---|
| `list` | none | `DashboardList` sorted by name | show |
| `save` | `SaveDashboardRequest` {id (empty = new), name, version, widgets} | the stored `Dashboard` (version + 1) | manage |
| `delete` | `DeleteDashboardRequest` {id} | none (an unknown id is nothing to do) | manage |
| `catalog` | none | `CatalogReply` {events: typeId, name, group, source, stateful, deviceScope, available, fields, hasUnseenFields} | show |
| `watch` | `WatchRequest` {watchId, keys: [{typeId, deviceId}]} | `ValuesReply` with the values now | show |
| `unwatch` | `UnwatchRequest` {watchId} | none | show |

Events: `dashboards` (`DashboardList`), `catalog` (`CatalogReply`), `values` (`ValuesReply` with the changed watched values).

## Tests

`tests/Oadm.Plugins.Dashboards.Tests`: storage, versions and every refusal; the values (each field's last value, the key
cap, old values and their reasons, reading and suggesting types); the catalog (fields once seen, at most once a second);
watching (only watched values, one update per 250 ms, a page that went away); turned off keeps nothing; Perf: 50 widgets
updating 4 times a second next to 30,000 other devices. Page: what a widget shows for each kind, the empty states, a new
dashboard from an event and a camera with the panel and live values, the device check, other users, roles; headless
screenshots with fake-mode video (`plugin-dashboards.png`, `-edit`, `-onoff`, `-drop`, `-empty`). Core:
`EventSubscriptionTests` (the subscription's types and their changes). Fake mode (`FakeOadmApi.Dashboards.cs`): three
sample triggers, the dashboard "Building A" and values that change every second.
