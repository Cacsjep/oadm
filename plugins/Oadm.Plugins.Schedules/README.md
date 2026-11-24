# Schedules

Core plugin `oadm.schedules` (spec: [docs/spec/schedules.md](../../docs/spec/schedules.md)). Maintenance at a set time: a
schedule has devices, a time and a list of actions; each run gives every device one task "Run <schedule>" with one named
step per action. Rail page Schedules (group Automation), free, on by default. The client part is
`plugins/Oadm.Plugins.Schedules.Client`.

## Files

| File | What |
|---|---|
| `SchedulesContract.cs` | Ids, page methods, models, the check reply; shared with the client part |
| `SchedulesPlugin.cs` | The core plugin: rights, audit, the page methods, the event type, start and stop |
| `Engine/ScheduleEngine.cs` | Ticks, runs, stages, window end, the device task's steps |
| `Engine/RunState.cs` | One running run: live counts, failed devices, the device tasks' reports, the wake-up |
| `Engine/ScheduleTimes.cs` | Starts of Once, Daily, Weekly, Monthly in server local time, window end, the gap |
| `Engine/Rollout.cs` | Stage sizes, the estimate, the When warning, durations |
| `Engine/ScheduleChecks.cs` | The wizard's check (the same on save), the device choice in one pass |
| `Engine/ActionCatalog.cs` | The actions (Provisioning's nodes), their rights and starting estimates |
| `Engine/RunNodeHost.cs` | The `INodeHost` of the actions during a run |
| `Engine/ScheduleStore.cs` | Schedules (setting `schedules`), runs and failed devices (records `runs`, `failures`) |
| `Engine/ScheduleTaskPlugin.cs` | The task `oadm.schedules.run` (hidden, a page task) |
| `Engine/RunEndedEvent.cs` | The event "Schedule run ended" and its fields |
| `Tools/SchedulesTools.cs` | The MCP tools `oadm_schedules_*` |

## Actions

The actions are Provisioning's nodes, compiled into this assembly as linked sources (see the csproj): Upgrade AXIS OS
online and from a file, Configuration, Restart (`Provisioning/Nodes/RestartNode.cs`, a node Provisioning never offers),
Certificate, 802.1X, VAPIX commands. Only the kinds of `ActionKinds.All`; `ActionCatalog` is the registry. `NetworkNode.cs`
is linked only because `INodeHost` names its pool; it is never offered (`RunNodeHost` refuses an address and a first
password). The VAPIX Commander library is copied into the plugin folder like Provisioning's. Files of actions live in a
`NodeVault` (Provisioning's) in the plugin's data folder; files no schedule and no running run names go after a day.

Each action starts the task of its area with its own checks through the plugin's task runner; Upgrade AXIS OS online
through the owner's task runner (`ICallContext.Tasks` of `IUserCalls.ForUserAsync`), so the owner's MyAxis sign-in
applies. The right of each action (`ActionCatalog.RightOf`): firmware "upgrade", Configure devices "configure",
`task:oadm.restart`, `task:oadm.pki.https-enable`, EST "enroll", `task:oadm.pki.dot1x-enable`, VAPIX Commander "rollout";
plus the plugin's "manage". A save checks them against the saving user, every run against the owner (fail closed).

## A run

- Start: a tick every 30 s takes the latest start after the one last handled (`StoredSchedule.Handled`; a save and a
  turn on set it to now). A run of the schedule goes on: a Skipped row. Older than 24 h or its window closed: a Missed
  row. Else the run starts. One per schedule and tick, so a server that was off catches up at most once. Only the first
  start of the plugin instance (the server's start; the host keeps one instance per process and turns it off and on with
  `StopAsync`/`StartAsync`) catches up: after the plugin was turned on again the first look records due starts as Missed
  ("Schedules was turned off at the time.", `ScheduleEngine.Start(catchUp: false)`; decision 2026-11-24).
- Window end: only for runs at their time; Run now starts every device of all its stages (decision 2026-11-24).
- Owner: `ICorePluginContext.UserCalls` asked at the start of every run; no host support, no account, a missing right or a
  chosen device outside the owner's view: the run ends Failed with the reason, nothing starts. A chosen device removed
  from OADM is left out.
- Devices: resolved in one pass in the owner's view; with stages an Ok device goes first. Each device is looked up again
  when its stage starts; not Ok is counted by its status text (`DeviceMessages.StatusText`), never started.
- Parallel: all devices of a stage start in one `RunAsync` (the whole run without stages); the task engine's limit, the
  server setting Parallel tasks per plugin, decides how many run (the task plugin's `MaxParallelDevices` is null), the
  others wait as queued. The window end and a stop cancel the tasks that did not start (`ITaskRunner.Cancel` of a queued
  task): Not started, and no further stage starts. A task that starts after its run closed does nothing (Not started). Running tasks run to their end.
- Device task: `ScheduleEngine.ExecuteAsync`, one step per action like `ProvisioningService.ExecuteAsync`. It reports its
  end to the run (by `ctx.TaskId`) and wakes the loop; tasks that never reported (cancelled while queued) are read from
  the runner every 5 s and after a close.
- Result: Stopped (stages and a failure: "Stopped at stage 3: 1 failed."), Failed (a device failed), Done with warnings
  (a warning, a device not reachable or not started), else Done. Missed and Skipped are rows of their own.
- The run's row is written at the start and the end (the live counts come from memory); at most 50 rows per schedule,
  1,000 failed devices per run. A run still Running when the server starts again ends Stopped "The server stopped during
  the run.".
- Audit: one entry per start, so one per stage (one per run without stages), user "Schedule <name> (<owner>)",
  target "Run <name>".
- Event: "Schedule run ended" (`oadm.schedules.run-ended`, server scope, owner scope "all") once per run, also Missed and
  Skipped; fields schedule, result (choice), done, failed, notReachable, notStarted, firstFailures (5 lines).
- MyAxis: an online upgrade failing with "Sign in to MyAxis." sets the schedule's warning until a run of it upgrades a
  device again (the SDK cannot ask a user's sign-in outside a task).

## Estimate

Waves of the parallel limit (16, the server setting's default; the SDK does not tell the setting) per stage times the
time per device: the average device task of the schedule's last ended run, else the sum of the starting estimates
(`ActionCatalog.StartingEstimate`). Longer than the shortest time between two starts: the When warning.

## Rights and groups

"show" (Operator on, Viewer off) reads; "manage" (Operator and Viewer off) everything else; saving also needs each
action's right. A user with a device group sees only schedules whose owner's current `DeviceScopeKey` is theirs (the owner's
call cached 30 s per owner, `ScheduleEngine.CanSeeAsync`; a run stores it as `OwnerScopeKey`); a chosen device outside the
caller's view answers like an unknown id (`DevicesNotAvailableException`). The page's `changed` event
goes to users with all devices and to the owner's device scope.

An access key (user name "MCP <key>") never owns a schedule: save, turn on and Run now refuse it
(`SchedulesPlugin.RequireUser`); the host refuses user names starting with "MCP ".

Delete closes the schedule's run (`ScheduleEngine.StopRun`: queued tasks cancelled, no further stage). Stopping the
plugin ends every run Stopped at once (`DisposeAsync`); its running tasks go to `LeftTasks`, kept by the plugin, so the next
engine gives those devices no second task while they run. The tick loop stops first and Run now is refused while it stops;
the devices still running count as Not started ("2 devices were still running; counted as not started."). A run that
breaks does the same and ends Failed.

Checks and saves drop the chosen devices the stored schedule had that are no longer in the caller's view
(`ScheduleChecks.DropGone`; a camera removed from OADM never blocks a save); a new id outside the view answers like an unknown
id. `ScheduleRequest.DevicesUnchanged` with `KnownDeviceCount` lets the wizard's Check skip the devices (Save never).

A kept configuration file is readable (`readConfigFile`) only by the session that stored it (`storeFile`) or by users who
see a schedule naming it; any other id answers like a file that is gone.

## Tests

`tests/Oadm.Plugins.Schedules.Tests/Server`: `EngineHarness` and `GateRunner` (a task runner that runs the device tasks
through the engine with a limit and holds actions on gates), a fake clock in UTC. Times, stages, runs, owner, parallel
limit, window end, Missed and Skipped, the event, the plugin's rights and groups, the canary, the tools, and one Perf test
with 100,000 devices.
