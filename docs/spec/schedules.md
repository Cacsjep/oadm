# Schedules

Status: **built 2026-11-23** (spec agreed 2026-11-23).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Maintenance at a set time: "every Monday at 23:00, upgrade AXIS OS and restart the cameras tagged Site A". A schedule
has devices, a time and a list of actions; each run gives every device one task with one named step per action, like
Provisioning.

Package `plugins/Oadm.Plugins.Schedules` (+ `.Client`), id `oadm.schedules`, free, on by default (switchable), core plugin
with a rail page **Schedules** in the group Automation (after Event rules), icon `calendarClock`, `siteSupport` full (the
actions' own tasks decide per device). Details in the plugin's README.

## Rules that never break

1. **Runs as its owner.** The owner is the user who last saved the schedule. Every run asks the host for the owner's
   current call (`IUserCalls`, below): the account must still exist and be enabled, have the plugin's "manage" and the
   right of every action, and the devices are only those of the owner's current view. Anything missing: nothing runs, the
   run ends Failed with the reason ("The owner no longer has the right Upgrade AXIS OS."). Fail closed. The rights and the
   view are checked at the start of every run through the owner's call; the device tasks then run on the plugin's own task
   runner, only Upgrade AXIS OS online through the owner's runner (their MyAxis sign-in). Why: the action VAPIX commands
   starts VAPIX Commander's task, a page task of another package, which a user's runner refuses for every user.
2. **Device safety of every action.** Each action starts the existing task of its area with its own checks (API check
   before the first write, input validated first). Never Network, Users, SD card or any address change; dangerous
   VAPIX commands and commands with a password or file are refused in a schedule (as in Provisioning).
3. **Never two runs of one schedule at once.** A start that falls due while the schedule's run goes on is skipped and
   logged as its own run row (Skipped). A device already in a running schedule task of the same schedule gets no second.
4. **Roll out in stages stops at the first failure** (below); offline devices never stop it.
5. **Nothing secret stored in clear.** Files and typed secrets of actions are kept like Provisioning's (the vault:
   files in the plugin's data folder, secrets encrypted with the master key, named by id); payloads in memory only. A kept
   configuration file is read (the file preview) only by the session that stored it or by users who see a schedule naming it.
6. **Time through `TimeProvider`**, server time; a run is due when its time has passed and it has not run for that time.

## A schedule

- **Name** (required, unique, at most 100 characters), **Turned on**, **Roll out in stages**.
- **Devices**: All devices, or chosen devices and tags (the shared `DevicePickerWindow`), worked out at the start of every
  run within the owner's view. A device removed from OADM is simply no longer in it: a check or save drops the chosen
  devices the stored schedule had that are no longer in the caller's view; a device id it did not have is checked (outside
  the view answers like an unknown id).
- **When**: Once (date and time), Daily (time), Weekly (days and time), Monthly (day 1 to 28 and time). Optional
  **window end** ("Start no new devices after 05:00"): no device task starts after it; tasks still queued then are
  cancelled and counted as Not started. Only for runs at their time, never Run now (decision 2026-11-24).
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
- **How many at once**: the task engine's limit, the server setting Parallel tasks per plugin (no setting per schedule). The
  plugin starts all devices of a stage in one start (the whole run without stages); the others wait as queued. The window
  end and a stop cancel the tasks still queued (Not started) and start no further stage; running tasks run to their end.
- **Devices that are not Ok** when their stage starts (the run starts without stages) (offline, Password wrong, Site
  offline, ...) get no task and are counted by status ("12 offline").
- **Roll out in stages** (off by default): stage sizes of the run's devices, rounded up: 1, 2 %, 5 %, 10 %, 20 %, then
  the rest; a stage not bigger than the one before is merged into the next (20 devices: 1, 2, 4, 13). A stage starts
  when every task of the stage before has ended. One failed device stops the run: devices not started are Not started,
  the run is **Stopped** ("Stopped at stage 3: 1 failed"). Done with warnings and Not needed count as success. The
  first stage goes on until a device that is Ok started (one that went offline is skipped); no device of it ran well:
  "Stopped at stage 1: no device could be tested.", stage 2 never starts. The next run starts from the beginning again.
- **Delete and stop during a run**: deleting the schedule closes its run (queued tasks cancelled, no further stage,
  running tasks run to their end, the event Stopped); turning the plugin off or stopping the server ends the run Stopped
  at once ("Schedules stopped during the run. 2 devices were still running; counted as not started."; after a server
  restart a run left Running ends Stopped "The server stopped during the run."): its queued tasks are cancelled, the
  devices still running count as Not started so the counts add up, their tasks run to their end and a later run gives
  those devices no second task while they run. A run that breaks ends Failed the same way.
- **Times of a run** are stored in the server's local time (the offset of Next run and Missed). A look for due starts
  re-reads each schedule (still on, not saved meanwhile, the start not handled yet); a start that cannot begin is a
  Failed row with the reason, the other schedules still run.
- **Results of a run**: Done, Done with warnings, Failed (without stages: at least one device failed), Stopped, Missed
  (the server was off and the window had closed), Skipped (the previous run still ran). No failure but devices not
  reachable or not started: Done with warnings. Counts: done, warnings, failed, not reachable (by status), not started.
- **Missed times**: at server start a due run of the last 24 hours starts while its window is open (or it has none);
  else one Missed row. Never more than one catch-up per schedule.
  Only after a server restart (decision 2026-11-24): turning the plugin off and on again makes up nothing; a start missed
  while it was off is one Missed row ("Schedules was turned off at the time.", once per schedule, its event once) and the
  schedule runs at its next time. The host loads one plugin instance per process and turns it off and on with Stop and
  Start; the instance's first start is the server's start.
- **Run now** (page and MCP): starts a run at once as the owner; the user who pressed it needs "manage"; audited with
  their name. Run now has no window (decision 2026-11-24): it starts every device of all its stages; the window end
  applies only to runs at their time.
- **Estimate**: devices of the choice now / parallel limit x the time per device (sum of the actions; from this
  schedule's last ended run when it had one, else the table's starting values), plus one per stage for its wait. The
  parallel limit of the estimate is 16, the setting's default (the SDK does not tell a plugin the setting). Shown
  in the wizard's Overview and on step When. Longer than the time between two runs: a warning directly below When ("A
  run takes about 24 h, longer than the 5 h between runs. A start during a run is skipped."); Save stays possible
  (decision 2026-11-23).

## The page

- Toolbar: **New schedule**, Edit, **Run now**, Turn on / Turn off, Delete (red, last, confirmation); search right.
- Grid (virtualized, `RangeObservableCollection`): Name, Devices ("All devices", "Tag Site A and 12 devices",
  `DeviceSelectorTexts.Brief`), When ("Mondays 23:00"), Then (the actions, comma separated), Next run, Last run (status chip and counts:
  "212 done, 3 failed"), Owner. A schedule that will not run (owner lost a right, no MyAxis sign-in for an online
  upgrade) shows the reason as a warning chip in Last run until it is fixed. The MyAxis warning shows only after a run
  failed with "Sign in to MyAxis." (a plugin cannot ask a user's sign-in outside a task); a later run that upgrades a
  device clears it. Empty: "Create a schedule to run tasks at a
  set time."
- Below, the **runs** of the selected schedule (newest first, the last 50 kept per schedule): Started, Ended, Result
  chip with counts, Current step and Progress like the Tasks grid (a running run: "Stage 2 of 4: Install the
  certificate" with stages, else the step most running devices are on; "Stage 1 of 4" alone before a device started;
  progress = the devices ended, done, warnings, failed, not reachable or not started, of the run's devices; both empty
  once it ended; live through `changed`, at most every 2 s), and for a run with failures its failed devices (device,
  step, message; at most 1,000 per run, virtualized). No count captions above the lists. The runs panel has the header
  "Runs" with **Delete** (the chosen rows) and **Delete all**, as the Tasks pane: "manage", the schedule visible to the
  caller; a run that still runs is never deleted (left out by Delete all, refused when chosen: "A run that still runs
  cannot be deleted."); its failed devices go
  with it; audited "Deleted 3 runs of schedule X" or "Deleted every run of schedule X".
- Live: the plugin's `changed` event refreshes the grid and the runs.

## The editor: a wizard

`ScheduleEditorWindow`, a wizard like the Event rule editor (`ui:WizardSteps`, a step opens when every step before is
complete, Back / Next disabled with the step's first problem as tooltip, errors directly below their field). The check
after the devices were checked once sends `DevicesUnchanged` and their count (no 100,000 ids per keystroke); Save always
resolves the devices.

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

SDK 0.16.0 `ICorePluginContext.UserCalls` (`IUserCalls?`, default null; only bundled plugins get it): `ForUserAsync(userName, ct)` answers the user's
current `ICallContext` (role permissions, device view, task runner as that user, so the user's MyAxis sign-in and queue
checks apply) or null when the account does not exist or is disabled. Never a session (`SessionId` empty, no events of a
session). The host logs every call at Debug.

## Rights and audit

- Permissions "show" (Operator on, Viewer off) and "manage" (Operator and Viewer off). Save needs "manage" and the
  right of each action the schedule has, checked against the saving user; Run now needs "manage".
- Users with a device group: their schedules reach only their devices; they see only their own group's schedules
  (owned by a user whose current view is theirs: the owner's call, cached 30 s, decides; a run stores it), administrators
  see all.
- An MCP access key never owns a schedule: save, turn on and Run now refuse it ("An access key cannot own a schedule.
  Create it in the client or with the Assistant."); no user name may start with "MCP " (the names access keys act under).
- Audited: create, change, turn on / off, delete, Run now (the user), and "Started task" with user "Schedule <name>
  (<owner>)", target "Run <name>", at its end the counts (`TaskAudit`): one entry per stage (one per run without stages).

## MCP

Area `schedules` (the host names a plugin's tools `oadm_<area>_<name>`): `oadm_schedules_list` (paged, search by name),
`oadm_schedules_get` (one, with the estimate and the next five runs), `oadm_schedules_runs` (paged; with `run_id` that
run's failed devices, at most 50, with `more`), `oadm_schedules_actions` (the actions with their default settings, PKI's
policies, VAPIX Commander commands by `command_search`), changes with preview and run: `_save`, `_delete`, `_turn_on`,
`_turn_off`, `_run_now`, `_runs_delete` (`run_ids`, or `all`; never a running run); `_file_store` (an uploaded file
for an action) as in Provisioning.

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
