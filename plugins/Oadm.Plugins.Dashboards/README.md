# Dashboards (`oadm.dashboards`)

Core plugin (+ `.Client`) with the rail page **Dashboards** (group Monitoring, icon `gauge`), off by default. Live values of
OADM's events (mostly device triggers) and live video on saved dashboards: a dashboard is a grid of widgets, each showing one
value of one device. Spec: [Bundled plugins](../../docs/spec/bundled-plugins.md#dashboards-core-plugin).

## Decisions

| Topic | Decision |
|---|---|
| Data | The server part subscribes to every event the server handles (`ICorePluginContext.EventSubscriptions`) and keeps the latest per event type and device in memory (`ValueStore`): on / off for an on / off event, the last value of each field (an event without a field keeps the field's value), the time. At most 100,000 keys, the least recently updated dropped first, O(1) per event. Memory only: turned off or restarted it starts empty; the subscription's snapshot brings the events that are on. It never talks to a device. |
| Fields | An event offers its values at once: **Active** (`@active`, yes / no) for an on / off event, then its declared `EventField`s (a device trigger declares every value with the type from the device's event list), then values it sends without declaring them, once seen. Kind: the declared `EventField.Kind`, for text the last 20 values received suggest Number (all numbers, invariant culture; `0` / `1` stay Number) or Yes / no (all true / false / yes / no / on / off). `FieldValues`. |
| Old values | `ValueSnapshot.Stale`: the event no longer exists ("Event no longer exists"), its plugin is off ("Device triggers is turned off"), the trigger's "connection lost" (`<type id>.lost`) is on for the device ("Connection lost"), or OADM's own status event of the device is on ("Unreachable", "Credentials required", "Certificate changed"). OADM's own status events and the lost events are never old themselves. The widget is dimmed with the reason below. |
| Watching | The page watches the keys of the widgets in its **open** tabs (`watch` with its own id, renewed every 10 s; `unwatch` when the page closes; forgotten 30 s after the last renewal). Changes of watched keys are pushed as event `values` every 250 ms at most (several events of one widget in between: one update with the latest). A status or "connection lost" change marks the device's watched values; a type change marks all. The catalog (event `catalog`) is pushed at most once a second when a new field was seen or the types changed. |
| Storage | Like Live View: plugin setting `dashboards` (JSON, enums as text), shared by every user, every change pushed (event `dashboards`). A save of an older version is refused ("Another user changed the dashboard X meanwhile. Click Cancel and make your changes again."); an unreadable stored list is kept as `dashboardsUnreadable`. |
| Grid | The shared `ui:TileGrid` with `DashboardsPluginInfo.Layout` (20 x 20 cells, at least 2 x 2, never overlapping); a dropped value takes up to 4 x 4; Add cameras sizes the cameras so all of them fit (16: 5 x 5 each, one: 8 x 8), smaller where the grid is full. |
| Limits | 50 widgets per dashboard, of them 16 live videos; 200 dashboards; names 1..64 (widget names may be empty); unit 10 characters; 10 thresholds; sizes 10..100 % of the widget; colors and icons only from `WidgetColors` / `WidgetIcons` (theme brushes by name: dark and light both work). The server checks all of it again (`DashboardRules`). |
| Widgets | **On / Off** (icon, color and optional text per state, icon size), **Text** (font size, color; shrinks to fit, then "…"), **Number** (decimal places or as received, unit, font size, color, thresholds: the color of the highest threshold the number reaches; a value that is no number shows "Not a number" dimmed), **Gauge** (the Number settings without the font size, plus
**Min** and **Max** (`Widget.Min` / `Max`, 0 and 100, Min below Max, at most 10^12 either way; kept but unused by the other
kinds), **Line width** (`Widget.LineWidth`, a slider 1..20 % of the radius, 2 by default; the ticks move inward with it;
also in the group panel when all are gauges); `GaugeView` draws an arc of 240 degrees from Min (lower left) to Max (lower right) in the threshold bands
(`GaugeScale.Bands`: the widget color up to the first threshold, then each threshold's color up to the next), labelled ticks
at nice steps (`GaugeScale.Ticks`: 1, 2 or 5 times a power of ten, 4 to 10 intervals; small ticks between them, 4 or 5 per
step) colored by their band, a marker inside the ticks at the value (stopping at the ends outside the range; the number still
shows the real value) and the value with its unit in the middle; everything follows the widget size, labels thin out to every
second one and go before they would touch; no value yet shows the scale only. The marker glides to a new value in 0.4 s
(ease-out, one step per frame, `TopLevel.RequestAnimationFrame`); a value arriving meanwhile continues from where it is, the
first value appears in place, the number shows the new value at once. A Number widget switched to Gauge keeps its
settings; one in the text color starts in the accent color). A new threshold of a Number or Gauge widget takes the first
color of Amber, Red, Orange, Green, Blue, Teal, Pink, Violet, Status OK / warning / error that no other threshold and not the
widget itself uses (`WidgetEditorViewModel.NextThresholdColor`; all taken: the one used least), **Live video** (the host's live video component like a Live View tile: controls closed, fullscreen with a double-click, the channel buttons; the channel chosen while editing is saved). Value widgets are transparent on the card (frame class `valueTile`); video keeps the video background. Every value widget has **Show name** (a Yes / No select, Yes by default; off, the value takes the room, the edit frame still names it). `WidgetDisplay` (no UI) decides what a widget shows; `WidgetView` draws it (`FitText`, `FitBox`). |
| Page | Left card (Collapse / Expand like Live View): **Dashboards** (+ New dashboard; Edit and Delete in each row), **Events** (the available events grouped, device triggers first, each with its fields and their kind; search). Right card: Close all tabs, while editing Name / Save / Cancel / **Add cameras** (the shared picker for several devices, cameras only, those on the dashboard greyed; at most 16 live videos, the rest named below the toolbar), the tabs (`ui:PageTab`); the grid; while editing the panel of the selected widget right of the grid (a new `WidgetEditorView` per widget, a `GroupEditorView` for several). Empty states: Device triggers off ("Turn on Device triggers on the Plugins page", `ICorePluginClientContext.IsPluginOn`), no trigger yet ("Create a device trigger first." + **Open Device triggers**), no dashboard yet. No **Open Plugins** button: a page cannot tell an administrator. |
| Editing | New dashboard opens a tab in edit mode ("Dashboard N"). An event dropped or clicked becomes a widget of its first field with the widget that fits (Number for a number, On / Off for yes / no, else Text) and opens its panel: Device (the shared picker for one device, the devices of the event; not for a server event), Value, Type (Automatic (X), Yes / no, Number, Text), Widget (only those that fit the type: a number offers Number, Gauge
and Text), Name (default "<value> · <device address>", follows the value and device until the user changes it), then the widget's settings. Every change shows in the grid at once. **Several widgets** (`DashboardTab.Selection`): a drag over empty cells draws a selection rectangle (`TileGrid.ShowSelectionArea`) and selects every widget it touches, Ctrl+click adds or removes one (no move), Ctrl+A selects all, a click on empty cells selects none. A selected widget dragged moves the whole selection by whole cells (it stops at the grid's edge and never moves onto another widget); dragging an edge or a corner of one resizes every selected widget by the same cells, selected widgets side by side making room for each other (`TryResizeSelection`; all or nothing: nothing changes when one would leave the grid, get smaller than 2 x 2 or hit another widget). The group panel (`GroupEditorViewModel`): copy and remove for all; Width and Height in cells (each widget that has room); Show name; the On / Off settings when all are On / Off, font size when all are Text or Number, color when all are Text, Number or
Gauge, decimals and unit when all are Number or Gauge, Min and Max when all are Gauge (an empty field keeps each widget's
own; a range that would be invalid for a widget leaves it unchanged); a setting where they differ shows "Mixed" (an empty select or field) until changed, a change goes to all of them and nothing else changes. **Copy for other devices** (the copy icon left of a widget's X while editing, `ITileGridItem.CopyCommand`, or the group panel's icon; widgets with a device): the shared picker for several devices (the widget's own device greyed, devices without video for a video widget), one copy per device with the same settings and size in the first free place of that size (`TileLayout.PlaceSized`), the device's address replacing the source's in the name (else " · address" added); as many as fit, the rest named below the toolbar ("12 of 20 copies added. ..."). Save checks the rules and that every widget of a device event has a device. |
| Devices | The pickers (Device, Copy for other devices) offer only the devices of the event: `EventTypeInfo.DeviceIds`, a device trigger's devices now (chosen, tagged, all), asked with `devices` when the picker opens (the catalog only says `hasDeviceList`); events without a list (OADM's own) offer every device. |

## Methods (`InvokeAsync`)

| Method | Payload | Answer | Permission |
|---|---|---|---|
| `list` | none | `DashboardList` sorted by name | show |
| `save` | `SaveDashboardRequest` {id (empty = new), name, version, widgets} | the stored `Dashboard` (version + 1) | manage |
| `delete` | `DeleteDashboardRequest` {id} | none (an unknown id is nothing to do) | manage |
| `catalog` | none | `CatalogReply` {events: typeId, name, group, source, stateful, deviceScope, available, fields, hasDeviceList} | show |
| `devices` | `DevicesRequest` {typeId} | `DevicesReply` {deviceIds, null = any device}: asked when a picker opens, so the catalog never grows with the devices | show |
| `watch` | `WatchRequest` {watchId, keys: [{typeId, deviceId}]} | `ValuesReply` with the values now; more than 5,000 keys are refused ("Too many dashboards are open. Close some tabs.") | show |
| `unwatch` | `UnwatchRequest` {watchId} | none | show |

Events: `dashboards` (`DashboardList`), `catalog` (`CatalogReply`), `values` (`ValuesReply` with the changed watched values).

## Permissions

"show" (automatic, Operator and Viewer), **Change dashboards** (`manage`: save, delete; Operator yes, Viewer no; both
audited). Video needs "Live view"; without it the widget says "Your role does not allow live view.".

## Device groups

[Device groups](../../docs/spec/device-scope.md):

- Every dashboard belongs to the device scope of the user who made it (`Dashboard.owner`: a group's id; none = all
  devices, also for every older dashboard; set by the server only). Users of a group list, change and delete only their
  group's dashboards, and `list` and the `dashboards` event show only the widgets of devices the group has now (and of
  server events); another group's dashboard answers like a deleted one. Names are unique per owner.
- An edit keeps the owner (also an administrator's). A save refuses widget devices outside the owner's devices (NOT_FOUND
  "1 device is no longer available.", nothing stored); dashboards of all devices keep removed devices. A deleted group's
  dashboards are seen only by users with all devices; an administrator's save gives such a dashboard to all devices.
- A user with all devices who saves a group's dashboard whose devices left the group gets the save: those widgets are
  dropped and the answer says how many (`Dashboard.dropped`, never stored; the page shows "Saved without 1 widget of a
  device that is no longer in the device group." below its toolbar as plain text, not as an error). A device the save adds
  outside the group, and every outside id of a group user, is still NOT_FOUND.
- A group user sees an event only when it can come from the group's devices: an event with a device list needs one of
  them (e.g. the group's own device triggers), an event of the device without a list only when OADM sends it (the device
  status events), never one of another source such as the compliance checks; of the server only the site events of the
  group's sites. Device triggers of other owners are hidden even on the group's devices (`EventTypeInfo.OwnerScopeKey`).
  This holds for `catalog`, the `catalog` event per group, `devices` and the watch keys (keys of events the user does not
  see are left out).
- `watch` belongs to the caller's session (the same watch id in two sessions is two watches); a group user's keys must
  name devices of the group (else NOT_FOUND). Values reach users with all devices as before, each session of a group user
  only the values its pages watch (`PluginEventAudience.Session`). Values stop at once when a device leaves the group: every
  push checks the group's devices now, and a renewal that names a device that left is refused (NOT_FOUND) and ends the old
  watch.
- The watches of a group user's session are bound to one job (`ctx.Jobs`, kind `watch`) that the host ends when the user's
  devices, role or session change; the session's last unwatch ends the job. A watch for another group in the same session
  (the user was moved) replaces the job and drops the session's watches of the old group at once.
- What a group sees (`Reach`: its devices and sites) is kept per group (`ViewerScopes.ViewAsync`) and read again only on
  `IDeviceScopes.Changed` for the group or after 5 minutes; the pushes check a device with `IDeviceScopes.Contains(key,
  id)`, never by reading the device table. At most 1,000 watches, 8 per session (a new one replaces the session's oldest).

## Tests

`tests/Oadm.Plugins.Dashboards.Tests`: `DeviceGroupTests` (the plugin canary; dashboards, catalog, devices and values per
group); storage, versions and every refusal; the values (each field's last value, the key
cap, old values and their reasons, reading and suggesting types); the catalog (declared values at once, refined by received ones, at most once a second);
watching (only watched values, one update per 250 ms, a page that went away); turned off keeps nothing; Perf: 50 widgets
updating 4 times a second next to 30,000 other devices. `GroupScaleTests`: 50 group pages of 5 groups at 30,000 devices
(no read of the device table per push, Perf the time of 100 pushes), a group change reads its devices again, a session
moved to another group keeps no watch of the old one, 8 watches per session. Gauge: nice ticks for many ranges, the marker's place, the bands,
the Min / Max rules (also refused by the server), the editor keeping the Number settings, headless drawing at several sizes
(`plugin-dashboards-gauge.png`). Page: what a widget shows for each kind, the empty states, a new
dashboard from an event and a camera with the panel and live values, several widgets selected, moved, sized and changed together, Add cameras up to 16, the device check, other users, roles; headless
screenshots with fake-mode video (`plugin-dashboards.png`, `-edit`, `-onoff`, `-drop`, `-empty`). Core:
`EventSubscriptionTests` (the subscription's types and their changes). Fake mode (`FakeOadmApi.Dashboards.cs`): three
sample triggers, the dashboard "Building A" and values that change every second.
