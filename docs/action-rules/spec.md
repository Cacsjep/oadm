# Action Rule Editor (spec draft)

Status: designed with the maintainer on 2026-10-15. **Stages 1, 2 and 3 built** (read, group, edit with Change check boxes,
on / off, delete, undo; copy rules, the Recipients and Schedules tabs; plugin `plugins/Oadm.Plugins.ActionRules`, see its
README) and verified on 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77), 10.0.0.202 (AXIS M3215-LVE, 12.11.77), 10.0.0.12 (AXIS
P12 Mk III, 11.9.65) and 10.0.0.200 (AXIS M3206-LVE, 10.12.338). Stage 3: the rule builder (**New rule**) and the action
catalog, verified on the same four cameras. Recorded device answers are in this folder.

## Goal

Find, change, deploy and delete the camera's own action rules (the Events page of the web interface: Rules, Recipients,
Schedules) on hundreds of cameras at once. Use case: a partner set up about 500 cameras with "Send notification through
TCP" where "Send notification through TCP while the rule is active" was needed, and must not fix them one by one.

Not the OADM event rules (those run on the server); this edits the rules that run on the devices.

## Decisions

| Topic | Decision |
|---|---|
| Plugin | Core plugin `oadm.action-rules`, "Action Rule Editor", rail page group Automation, **off by default**, switchable. |
| Page | The purple **Selected devices** button (shared device picker with All devices and tags; starts with the Devices page selection, else the last choice on this computer, else nothing and the page reads nothing) chooses the devices; the page reads and shows only those, Refresh reads them again. New rule and Copy to devices list every device, those whose rules were not read greyed. Tabs Rules / Recipients / Schedules. Every change is one task per camera in the tasks pane below the page. |
| Same rule on many cameras | Matched by what the rule does, not by name. Default group = same trigger (event with its filter, conditions, schedule) + same action type. Names and values may differ; the group shows them per column ("Host: 192.168.1.5 (480), 10.1.1.9 (7)"). |
| Group by | Selector: What it does (default), Name, Action, Trigger. The edit form offers only the parts that are equal in the group; the action type can always be changed. |
| Edit | A form where every field has a **Change** check box. Unchecked = each camera keeps its own value (e.g. a message with a unique number per camera). Checked = the same value on every camera of the group. |
| Once / while active | Manual: the installer changes the action type and the trigger kind (start event / condition) separately. A combination the camera refuses fails that camera's task with the camera's text, nothing changed there. |
| Deploy | (a) Copy a rule from a camera or a group to other cameras, with the same Change form; (b) own **rule builder**: event from the cameras' live event list (like Device triggers), conditions, schedule, action type and fields. Schedules and recipients the rule needs are created with it. |
| Action forms | Bundled JSON catalog per action template: labels, field types, choices, defaults, hints. Real forms for the common types (notifications TCP / HTTP / HTTPS / email, send images and video clips to FTP / SFTP / HTTP / HTTPS / email / network share, recording, I/O output, overlay text, MQTT publish, LED, IR light, audio clip). Every other template the camera lists: one text field per parameter name. |
| Recipients | Edit / deploy / delete recipients. Editing one offers "Also update N rules that send to <old server>" (checked; rules found by the old server settings), done in the same task per camera. |
| Schedules | OADM-created schedules get one id on every camera (`com.axis.schedules.oadm.<name>`). Deploy matches an existing schedule by name + times, else creates it. Delete is refused where a rule uses the schedule ("Used by 2 rules"), with the option to delete those rules too. Built-in `com.axis.schedules.*` schedules are never deleted. |
| Older AXIS OS | Supported. Without the REST API the rule is recreated over SOAP: the new action and rule first, then the old rule and its action are removed (as Configure devices does). The rule gets a new id; the task log says so. |
| Safety | Preview per camera before Run ("487 change, 13 skipped: action type differs"), confirmation, delete in red. Before its first write each task stores the rules, recipients and schedules it touches as they were (plugin data folder, passwords encrypted with the master key, 30 days). **Undo last change** per run puts them back (recreated where deleted). |
| Roles | "show" (Operator, Viewer: read), "Change action rules" (`manage`: edit, deploy, turn on / off, recipients, schedules; Operator yes, Viewer no), "Delete action rules" (`delete`: Operator yes). Every run audited. |
| Not in v1 | The Manual triggers tab (manual triggers can still be the event of a rule). |

## Device API (verified on AXIS OS 12.11)

### REST, Device Configuration API (`/config/discover` lists them)

- `action-rules` v2 (2.0.1): `/config/rest/action-rules/v2` with `rules`, `recipients`, `templates.actions`,
  `templates.recipients`. Docs: `action-rules.doc.md`, model `action-rules.model.json`.
- A rule is one object: `{id, name, enabled, activationConfig {startEvent {topicExpression, messageContent} | null,
  condition [EventFilter] | null, timeout "PT10S" | null}, actionConfig {template, actionParameters [{name, value}],
  recipientParameters [{name, value}] | null, recipientId (write only)}}`.
- `POST rules` creates rule + action configuration. `PATCH rules/<id>` (whole or one property: `name`, `enabled`,
  `activationConfig`, `actionConfig`) changes it **in place: the rule id stays**. `DELETE rules/<id>` also removes its
  action configuration.
- `actionConfig` is replaced as a whole: a PATCH with only `template` = "Internal error". A missing or unknown parameter
  for the template = "Internal error". Nothing is changed on any error.
- An `UNLIMITED` (while active) template needs a condition: "Validation error: Action configuration is of type
  'continuous' and requires at least one condition". `unlimited.notification.tcp` needs `message` and `period`.
- Passwords read as `**Redacted**`. **Sending that value back stores the text "**Redacted**" as the password.** A PATCH of
  `name`, `enabled` or `activationConfig` leaves the action and its password untouched. A PATCH of `actionConfig` must
  carry the real password: the server reads it over SOAP (below), never sends it to the client, never logs it.
- `recipientId` copies the recipient's parameters into the rule at write time; it reads back as null. No link remains:
  a later recipient change does not reach the rule.
- The device rewrites `messageContent` (e.g. reorders `and` terms): compare filters parsed, not as text.
- Recipients: `POST`, `PATCH recipients/<id>/<property>` (id stays), `DELETE`.
- `event-schedules` v2 (2.0.1): `/config/rest/event-schedules/v2/schedules`, `{id, name, schedule (iCalendar text),
  scheduleType INTERVAL | PULSE}`. `POST` accepts an own `id` (any string, e.g. `com.axis.schedules.oadm.night`).
  `PATCH schedules/<id>/schedule` changes in place. `DELETE` succeeds while a rule uses the schedule (the rule is left
  pointing at nothing).
- A schedule in a rule: condition `tns1:UserAlarm/tnsaxis:Recurring/Interval` with
  `boolean(//SimpleItem[@Name="id" and @Value="<schedule id>"]) and boolean(//SimpleItem[@Name="active" and @Value="1"])`
  (without the `active` term: "property state key's value not specified"). Pulse schedules: `.../Recurring/Pulse`.

### SOAP (`/vapix/services`, every AXIS OS with the action engine)

- action1: `GetActionRules`, `GetActionConfigurations`, `GetRecipientConfigurations`, `GetActionTemplates`,
  `GetRecipientTemplates`, `Add/RemoveActionRule`, `Add/RemoveActionConfiguration`, `Add/RemoveRecipientConfiguration`.
  No change method (`ChangeActionRule`, `SetActionRule` ...: "Optional action not implemented").
- event1: `GetScheduledEvents`, `AddScheduledEvent` (optional `EventID`), `RemoveScheduledEvent`, `GetEventInstances`.
- `GetActionConfigurations` and `GetRecipientConfigurations` return passwords **in plain text** (admin only).
- A rule points to its configuration by `PrimaryAction`; a REST edit of `actionConfig` may give the configuration a new
  id (seen 6 -> 9), the rule id stays.
- Existing code: `plugins/Oadm.Plugins.DeviceConfig/Configure/ActionEngineApi.cs` (read, add, remove, schedules,
  replace-by-name order). To be moved where both plugins use it.

### Templates on the M3215-LVE

53 action templates (`GetActionTemplates.xml`), each `fixed` (once) or `unlimited` (while active), 29 of them with a
recipient template. Recipient templates and parameters (`GetRecipientTemplates.xml`): networkshare, ftp, https, http,
sftp, smtp, tcp, sip. The list differs per model and installed applications: always read it from the camera.

## Verified on AXIS OS 10.12 (10.0.0.200)

- No Device Configuration API (`/config/discover` 404): rules change only over SOAP, by recreating.
- A condition's topic is stored with `//.` appended (`tns1:Device/tnsaxis:IO/VirtualInput//.`); compare topics without it.
- 49 action templates instead of 52: no defog, no erase configuration; `set_overlay` without `index`, audio clips without
  device and output ids. The same recipient templates.
- The same rule on AXIS OS 10 and 12 forms one group; one run changes it on both (end-to-end test).

## Review (2026-10-15)

An independent review of stages 1 and 2 found 21 points; all fixed: keyed versions (no password guessing), undo with the state
before and after every write (no duplicates, later changes stay), the recreate rollback, per-device tasks one at a time,
undo only after the run and once, the mixed-firmware copy, schedule delete with the exact rules, recipients matching one
rule, the undo store's lock and age rule, progress-only polling, the shared form row, the `//.` fix in DeviceConfig too.
A second pass confirmed the fixes and found four problems they introduced, fixed too: a removal without an answer is
decided by reading the device again (never removing the only copy), undo is safe with identical twins and restores lost
items, required settings may stay empty (the device decides), left-out settings are reported. The three points left open
are fixed with stage 3: one SOAP engine for this plugin and Configure devices (`plugins/Shared/ActionEngine`), a login
inside an address shows as `user:********@` and keeps each device's password, and undo asks the task engine whether the
run's tasks still run (`ITaskRunner.IsActive`).

## Open points

- Which AXIS OS added `action-rules` v2 and `event-schedules` v2: not 11.9.65 (10.0.0.12), present in 12.11. OADM uses
  `/config/discover`, never a version number.
- Actions that need something of the camera (storage, network share, recording group, audio clip, I/O port, light) are
  text fields in the catalog; a list read from each camera would be nicer.
- How the camera's web interface writes a "while active" rule (which fields it fills), to match it exactly.
- Undo for rules on old firmware recreated over SOAP: the undo recreates them too.
