# Tasks

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

- **A task always targets exactly one device.** Running a plugin on N selected devices creates
  N tasks (one Run, one shared BatchId, the payload shared in memory), so one failing device
  never marks the others as failed. The task state is the device state.
- States: Queued, Running, Done, Failed, Cancelled, DoneWithWarnings (the plugin called
  `ReportWarning`; a failure wins over a warning). Persisted, visible in the tasks pane, history
  kept until the user deletes it or the retention removes it (finished tasks older than
  `Tasks.RetentionDays` = 90 or beyond the newest `Tasks.MaxHistory` = 50,000; hourly, see Settings).
- **Steps: a task always tells the user exactly what it is doing.** Every device request and every
  wait of a task plugin is its own short, imperative, named step ("Read users", "Upload firmware",
  "Wait for the device to come back"); five requests = five steps. Plugins plan their steps up front
  (`PlanSteps`, shown Pending) and may add steps on the way; steps that do not apply are Skipped with a
  reason ("Keep unchanged"). Engine rules: beginning a step completes the running one; when the plugin
  returns, a running step ends Done and planned steps never reached end Skipped ("Not run."); when it
  throws, the running step (or the one its `using` just ended) ends Failed with the exception message and
  pending ones Skipped ("Not run: an earlier step failed." / "... the task was cancelled."; a cancelled
  running step is Failed "Cancelled."). The running step is also the device message ("Upload firmware -
  12 of 80 MB"). When the task succeeds (Done or Done with warnings) the engine appends a final step
  **Completed** (Done, `TaskStepList.CompletedStepName`, also for plugins without own steps), so the
  tasks pane reads just "Completed"; Failed and Cancelled tasks get none (the failed step stays
  current). Overall progress = steps with equal weights, the running one with its own progress,
  unless the plugin calls `ReportProgress` (then that value wins). Step changes go to the change feed
  immediately and are persisted with the throttled writes (max 1/s per device) plus every state
  transition. Recovery after a server restart marks a Running step Failed ("Server stopped while the
  task was running.") and Pending ones Skipped.
- **Task names: the task list says exactly what a task does** ("Add user joe", "Set static IP
  10.0.0.60", "Upgrade firmware to 12.11.77 (factory default)"), never the plugin's menu name: the engine
  names each task with `ITaskPlugin.GetTaskName(payloadJson)` once per Run (trimmed, trailing "..."
  stripped, at most `TaskPluginNames.MaxTaskNameLength` = 48 characters, longer names shortened with
  "…" and a warning logged; empty or throwing = `DisplayName`). Names never contain secrets.
- Task engine parallelism: at most `Tasks.MaxParallelPerPlugin` running tasks (= devices) per plugin, a
  **server setting** (default 16, 1..256, Settings page "Parallel tasks per plugin"). A plugin's
  `ITaskPlugin.MaxParallelDevices` can only lower it: limit = min(setting, MaxParallelDevices) (firmware
  sets 4: large uploads share the server's link). Further tasks wait in Queued (FIFO per plugin,
  `Core/Tasks/PluginSlots`) and a cancel while queued ends them as Cancelled ("Cancelled before start.").
  The engine reads the setting live (`TaskParallelismSetting` -> `TaskEngineOptions.MaxParallelTasksPerPluginSource`)
  each time a queued task could start: a change applies without a server restart to tasks that start
  afterwards (a raised limit starts queued tasks at once via `TaskEngine.RescheduleQueued`; a lowered one
  never interrupts running tasks, new ones start when fewer run). The former configuration value
  `Oadm:MaxParallelTasksPerPlugin` is no longer read. Example: a Restart on 5,000 devices at about 90 s
  each takes about 8 hours at 16, about 1 hour at 128. Cancellation via
  `CancellationToken`, exceptions become `Failed` with message (also logged as an Error entry),
  never crash the server. A Run on N devices publishes N Added changes and writes all N tasks in
  one store transaction (`ITaskStore.AddRangeAsync`) before any of them starts; the engine overhead
  is measured at about 9 ms per task with SQLite (5,000 trivial tasks in about 45 s).
- Persistence: every state transition writes the snapshot plus new log entries; a progress
  report with a message, a warning or a log entry writes at most once per second per device
  (the message survives as the device result). Payloads are never persisted or logged.
- Task log: `ctx.Log(level, message)`, warnings, failures, precondition errors and credential
  changes; live from memory while the task runs (`GetLog`), from TaskLogEntries afterwards.
- **Device groups** (`docs/spec/device-scope.md`): a user's Run takes its devices from the user's view
  (`TaskEngine.RunAsync(pluginId, ViewedDevices, payload, TaskCaller)`; `TaskEngine.ForView` for `call.Tasks`). The task
  keeps its owner (`TaskCaller`: user id, name, session, permissions) in memory only. When it leaves the queue it reads
  the owner's view as it is now (`DeviceViewFactory.ForUserAsync`, the user's row by id): a device that left it, a user without the group or a
  deleted user fails the task with "1 device is no longer available." before any request. The plugin gets the owner's
  call (`ITaskExecutionContext.Caller`, `TaskCallContext`); tasks the system starts have none. The raw-id
  `RunAsync` is the system runner; inside the call of a user with a group it runs only on devices of that user's view
  (backstop). History, Watch, log, cancel and delete of a user with a group show only tasks whose device is in the view
  now, filtered in the query (`ITaskStore.ListPageAsync(deviceIds, ...)`, `EfTaskStore`: one JSON parameter through
  `json_each`); 30,000 devices and 50,000 tasks: a page in under 1 s, a Watch snapshot of 10,000 in about 2 s. The Watch
  follows devices that leave (their tasks Removed at once) and enter (their tasks Added, like the snapshot) through tag,
  site and rule changes.
- No scheduling, retry or recurrence.
