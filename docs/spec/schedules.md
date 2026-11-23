# Schedules

Status: **spec agreed 2026-11-23, not built.**

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Maintenance at a set time: "every Monday at 23:00, upgrade AXIS OS and restart the cameras tagged Site A". A schedule
has devices, a time and a list of actions; each run gives every device one task with one named step per action, like
Provisioning.

Package `plugins/Oadm.Plugins.Schedules` (+ `.Client`), id `oadm.schedules`, free, on by default (switchable), core plugin
with a rail page **Schedules** in the group Automation (after Event rules), icon `calendar`, `siteSupport` full (the
actions' own tasks decide per device). Details in the plugin's README.

## Rules that never break

1. **Runs as its owner.** The owner is the user who last saved the schedule. Every run asks the host for the owner's
   current call (`IUserCalls`, below): the account must still exist and be enabled, have the plugin's "manage" and the
   right of every action, and the devices are only those of the owner's current view. Anything missing: nothing runs, the
   run ends Failed with the reason ("The owner no longer has the right Upgrade AXIS OS."). Fail closed.
2. **Device safety of every action.** Each action starts the existing task of its area with its own checks (API check
   before the first write, input validated first). Never Network, Users, SD card or any address change; dangerous
   VAPIX commands and commands with a password or file are refused in a schedule (as in Provisioning).
3. **Never two runs of one schedule at once.** A start that falls due while the schedule's run goes on is skipped and
   logged as its own run row (Skipped). A device already in a running schedule task of the same schedule gets no second.
4. **Roll out in stages stops at the first failure** (below); offline devices never stop it.
5. **Nothing secret stored in clear.** Files and typed secrets of actions are kept like Provisioning's (the vault:
   files in the plugin's data folder, secrets encrypted with the master key, named by id); payloads in memory only.
6. **Time through `TimeProvider`**, server time; a run is due when its time has passed and it has not run for that time.

## A schedule

- **Name** (required, unique, at most 100 characters), **Turned on**, **Roll out in stages**.
- **Devices**: All devices, tags, a device group or chosen devices (the shared device choice), worked out at the start of
  every run within the owner's view. A device removed from OADM is simply no longer in it.
- **When**: Once (date and time), Daily (time), Weekly (days and time), Monthly (day 1 to 28 and time). Optional
  **window end** ("Start no new devices after 05:00"): no device task starts after it; tasks still queued then are
  cancelled and counted as Not started.
- **Then**: a straight list of actions, at least one, at most 10, in order, each with **Continue on error** (on by
  default). An action failing with it off ends that device's task; the next device is unaffected.
- At most 200 schedules per server.

## Actions

The nodes of Provisioning (`docs/spec/provisioning.md` "Nodes"), the same code (the node sources are compiled into this
package too, like Provisioning compiles Device configuration's), the same node list, picker and side panel in the
editor. Each starts the task of its area; those tasks show in the tasks pane too.

| Action | Step name | Task | Starting estimate per device |
|---|---|---|---|
| Upgrade AXIS OS online | Upgrade AXIS OS from axis.com | `oadm.firmware` with the owner's MyAxis sign-in | 10 min |
| Upgrade AXIS OS from a file | Upgrade AXIS OS from a file | `oadm.firmware` | 10 min |
| Apply configuration (`.cfg`) | Apply the configuration | `oadm.device-config.configure` | 1 min |
| Restart | Restart the device | `oadm.restart` (a new node, not offered in Provisioning) | 3 min |
| Certificate (PKI HTTPS policy or EST) | Install the certificate | PKI's / EST's | 1 min |
| 802.1X (PKI policy) | Turn on 802.1X | PKI's | 1 min |
| VAPIX commands | Run the VAPIX commands | Commander's | 1 min |

Upgrade AXIS OS online without a valid MyAxis sign-in of the owner: the step fails with "Sign in to MyAxis."; the page
shows a warning on the schedule while the owner's sign-in is missing or expired. A device on the newest version: the
step is Not needed (success). Not in the first version and never: hardening scan, backup of the configuration
(decision 2026-11-23); never Network, Users, SD card, Date and time, Autofocus.

## A run

- **One task per device**, `oadm.schedules.run`, name "Run <schedule>", hidden from the menus, one named step per action
  (the step names above). Success, Warning (Done with warnings), Not needed (success with its reason) and Error as in
  Provisioning. The task fails when a step failed.
- **How many at once**: the server setting Parallel tasks per plugin (no setting per schedule). The plugin starts device
  tasks itself as places free up, so the window end and a stop take effect at once.
- **Devices that are not Ok** when their turn comes (offline, Password wrong, Site offline, ...) get no task and are
  counted by status ("12 offline").
- **Roll out in stages** (off by default): stage sizes of the run's devices, rounded up: 1, 2 %, 5 %, 10 %, 20 %, then
  the rest; a stage not bigger than the one before is merged into the next (20 devices: 1, 2, 4, 13). A stage starts
  when every task of the stage before has ended. One failed device stops the run: devices not started are Not started,
  the run is **Stopped** ("Stopped at stage 3: 1 failed"). Done with warnings and Not needed count as success. The
  first stage takes a device that is Ok. The next run starts from the beginning again.
- **Results of a run**: Done, Done with warnings, Failed (without stages: at least one device failed), Stopped, Missed
  (the server was off and the window had closed), Skipped (the previous run still ran). Counts: done, warnings, failed,
  not reachable (by status), not started.
- **Missed times**: at server start a due run of the last 24 hours starts while its window is open (or it has none);
  else one Missed row. Never more than one catch-up per schedule.
- **Run now** (page and MCP): starts a run at once as the owner; the user who pressed it needs "manage"; audited with
  their name.
- **Estimate**: devices of the choice now / parallel limit x the time per device (sum of the actions; from this
  schedule's last ended run when it had one, else the table's starting values), plus one per stage for its wait. Shown
  in the wizard's Overview and on step When. Longer than the time between two runs: a warning directly below When ("A
  run takes about 24 h, longer than the 5 h between runs. A start during a run is skipped."); Save stays possible
  (decision 2026-11-23).

## The page

- Toolbar: **New schedule**, Edit, **Run now**, Turn on / Turn off, Delete (red, last, confirmation); search right.
- Grid (virtualized, `RangeObservableCollection`): Name, Devices ("All devices", "Tag Site A", "Group Lobby", "12
  devices"), When ("Mondays 23:00"), Then (the actions, comma separated), Next run, Last run (status chip and counts:
  "212 done, 3 failed"), Owner. A schedule that will not run (owner lost a right, no MyAxis sign-in for an online
  upgrade) shows the reason as a warning chip in Last run until it is fixed. Empty: "Create a schedule to run tasks at a
  set time."
- Below, the **runs** of the selected schedule (newest first, the last 50 kept per schedule): Started, Ended, Result
  chip with counts, and for a run with failures its failed devices (device, step, message; at most 1,000 per run,
  virtualized). No count captions above the lists.
- Live: the plugin's `changed` event refreshes the grid and the runs.

## The editor: a wizard

`ScheduleEditorWindow`, a wizard like the Event rule editor (`ui:WizardSteps`, a step opens when every step before is
complete, Back / Next disabled with the step's first problem as tooltip, errors directly below their field):

1. **Schedule**: Name, Turned on, Roll out in stages.
2. **Devices**: the shared device choice and the list it gives.
3. **When**: Once / Daily / Weekly / Monthly, date, days, day of month, time, window end; the estimate warning here only
   when it applies.
4. **Then**: the actions (the node list of Provisioning: Add from the picker, move up and down, remove; the side panel
   with the settings and Continue on error).
5. **Overview**: one short line per fact the earlier steps do not show, then **Save**:
   Next run ("Mon 24 Nov, 23:00"), Then (the actions in order), Stages ("1 → 3 → 8 → 15 → 30 → 93", only with Roll out
   in stages), Estimated ("about 3 h 20 min"). No captions, no device count.

Save of a turned-on schedule makes it run at its next time.

## Event for rules

Event **Schedule run ended** (`IEventPublisher`), one per run (never per device), also for Missed and Skipped. Fields:
Schedule, Result (Done, Done with warnings, Failed, Stopped, Missed, Skipped), Done, Failed, Not reachable, Not started
(numbers), First failures (text: at most 5 "device: step: message" lines). A rule sends an email on every run or, with a
field check on Result, only when it is not Done.

## Host: running as the owner

SDK 0.16.0 `ICorePluginContext.UserCalls` (`IUserCalls?`, default null): `ForUserAsync(userName, ct)` answers the user's
current `ICallContext` (role permissions, device view, task runner as that user, so the user's MyAxis sign-in and queue
checks apply) or null when the account does not exist or is disabled. Never a session (`SessionId` empty, no events of a
session). Only plugins whose manifest declares it get it (`"actsAsOwner": true`); the host logs every call at Debug.

## Rights and audit

- Permissions "show" (Operator on, Viewer off) and "manage" (Operator and Viewer off). Save needs "manage" and the
  right of each action the schedule has, checked against the saving user; Run now needs "manage".
- Users with a device group: their schedules reach only their devices; they see only their own group's schedules
  (owned by a user with the same view), administrators see all.
- Audited: create, change, turn on / off, delete, Run now (the user), and every run "Started task" with user "Schedule
  <name> (<owner>)", target "Run <name>", at its end the counts (`TaskAudit`).

## MCP

`oadm_schedules` (list, paged), `oadm_schedule` (one, with the estimate and the next runs), `oadm_schedule_runs`
(paged, a run's failed devices with `more`), `oadm_schedule_actions` (the actions and their settings), changes with
preview and run: `_save`, `_delete`, `_turn_on`, `_turn_off`, `_run_now`; `_file_store` (an uploaded file for an action)
as in Provisioning.

## Scale

Devices of a run resolved in one pass (O(n)); stage sizes computed, never per-device lookups over all rows; task starts
in batches of the free places; 100,000 devices in a run: resolving, staging and the counts stay fast (Perf test). The
run's failed-device list is capped at 1,000. Run rows kept: 50 per schedule.

## Tests

- Due times (once, daily, weekly, monthly, over midnight, window end) with `FakeTimeProvider`; missed and skipped runs;
  never two runs of one schedule.
- Stages: sizes for 1, 20, 150, 100,000 devices; a failure stops the run and leaves the rest Not started; offline devices
  counted, not started, never stopping; warnings and Not needed continue.
- Owner: deleted, disabled, a lost right or device out of the view: nothing runs, Failed with the reason.
- Parallel limit held with gates; window end cancels queued tasks.
- The event once per run with its fields; the estimate and its warning.
- Client: the wizard's steps open in order, errors below fields, Overview lines; headless screenshots of page and wizard.
