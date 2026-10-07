# Scale audit: 5,000 devices, 50,000 tasks

Audit of server, client and plugins against the CLAUDE.md rule "HARD RULE, scale to thousands of
devices". Target size: **5,000 managed devices** (each with about 100 VAPIX APIs in its API list) and
**50,000 tasks** in the history, Runs on 5,000 devices at once.

How to reproduce the numbers: `manage test perf` (or
`dotnet test Oadm.sln --filter Category=Perf --logger "console;verbosity=detailed"`). Every scale test
writes its measured times to the test output and asserts a generous budget (CI machines are slow), so
an O(n^2) regression fails while normal noise does not. Numbers below are from a developer machine
(Windows, Debug build, SQLite on SSD); "before" numbers were measured with replicas of the former
algorithms on the same machine and data.

## Summary of the biggest findings

| # | Area | Problem | Fix | Before | After |
|---|---|---|---|---|---|
| 1 | Devices page select-all | `GridSelection` added the 5,000 rows one by one (`List.Contains` per row), each add rebuilt the context menu: `RunnableFor` checked every selected id against every plugin's `RepeatedField` (linear) | One `ResetTo` for multi-item selection changes; runnable sets are hash sets built once per catalog refresh | 1,000 rows: 5.7 s, 2,000 rows: 52 s (cubic; 5,000 rows practically hangs the UI) | 5,000 rows incl. menu with 10 plugins: 10 ms; headless DataGrid select-all 29 ms |
| 2 | Task pane vs device changes | Every `DeviceStore.Changed` re-labelled **every** task row; the store raised it for every device event, also for no-op polls | Tasks indexed by device id, only the changed devices' tasks are re-labelled; unchanged devices raise nothing | 1,000 device changes with 10,000 tasks: 1,330 ms | 24 ms |
| 3 | Device polling | All devices polled in one burst (16 parallel); every poll published the full device (100 APIs) to every client even when only LastSeenUtc changed; SQLite FULL sync made every write wait for the disk | Polls spread evenly over 90 % of the interval; LastSeen-only updates not published; WAL + `synchronous=NORMAL` | 5,000 polls: 22 s of DB time per round; 5,000 x ~6 KB device messages per minute per client | 6 s per round (spread over the minute); 0 messages for an unchanged poll |
| 4 | Change feeds | Per-subscriber channel `DropOldest(4096)`: a 5,000-task Run, Refresh all or Delete all silently dropped changes, a client kept stale rows until it reconnected | `KeyedCoalescingChannel`: in order while the reader keeps up, beyond 1,024 waiting coalesced per entity (latest snapshot wins) | changes lost above 4,096 | 5,000 tasks x 11 changes: every final state delivered |
| 5 | Task history | `List`/`Watch` loaded and sent all tasks with steps; startup recovery read all 50,000; Delete all deleted one by one (4 statements + 1 read per task); no retention | Paging (`ListPageAsync`, `List(limit, offset)`), Watch `snapshot_limit` (client: 10,000), Status index, bulk `DeleteManyAsync`, retention setting | Watch snapshot of 50,000: 2.8 s server time plus tens of MB to every client on every connect; Delete all: 50,000 x 5 statements | Watch snapshot of the newest 10,000: 0.75 s; Delete all of 50,000: 5.2 s; retention to 10,000: 5.6 s |
| 6 | Run on 5,000 devices | 5,000 sequential `AddAsync` transactions before the reply | One `AddRangeAsync` transaction | (5,000 commits) | gRPC reply in 0.84 s |
| 7 | ListTaskPlugins | Called after every device change (debounced 400 ms, so starved or constant), each call loaded all devices and ran CanRun 5,000 x N; reply carries 5,000 ids per plugin | Server cache per device-table version (max 10 s); client throttle (max one call per 2 s, never starved); compact form | 1.2 s per call, 1.8 MB per reply (10 plugins) | cached call 43 ms, compact reply 74 KB |
| 8 | Device List message | One message with every device: 22.9 MB for 5,000 devices (client limit 32 MB, so about 7,000 devices break the client) | Client uses Watch with a snapshot end marker (one device per message) instead of List + Watch (which also sent everything twice); gzip | 2 x 23 MB per connect | 1 x streamed, gzip; snapshot of 5,000 in 1.5 s |

## What was checked

### Client

| Item | Checked | Finding | Fix |
|---|---|---|---|
| Devices grid | DataGrid virtualization, filter, sort, select-all, live updates while sorted | Virtualized (13 rows realized of 5,000). `ApplyFilter` used `List.Contains` in two loops (O(n^2), 25 million comparisons) and raised one event per added/removed row (4,608 events for one search) | `ApplyFilter` builds the matching list once and calls `ReplaceAll` (one Reset, none when unchanged). Headless test: first render 0.7 s, select all 29 ms, sort 5,000 rows, 1,000 updates while sorted 65 ms, search 171 ms |
| Selection, context menu, toolbar enable checks | `GridSelection`, `RebuildContextMenu`, `TaskPluginCatalog.RunnableFor`, `ToolbarContext.CanRunTask` | See finding 1 | `IResettableList` + `RangeObservableCollection` for the selection, hash sets per plugin (`ConditionalWeakTable` per `TaskPluginInfo`) for both the full and the compact form |
| `DeviceStore` | cost per change, reset | `Changed` for every event (also no-ops), stale removal `Devices.Where(...).Remove` per row, every update rebuilt 100 `DeviceApi` objects | `ApplyBatch` (O(1) per change, one collection event per batch, `Changed` with the affected ids, nothing for a no-op detected via property changes), interned API lists (`DeviceApiLists`, 500,000 objects become a few shared lists). Reset of 5,000: 80 ms; 1,000 single updates: 20 ms |
| `TaskStore` | cost per change, reset, active count, memory | `Reset` inserted every task at index 0 (50,000 Insert(0) events), `ActiveCount` counted all tasks on every change, every device change re-labelled all tasks (finding 2), no upper bound (50,000 rows with steps ~150 MB) | Batch apply, sorted `ReplaceAll`, `ActiveCount` maintained per change, device index, `MaxTasks` = 10,000 (all active tasks always kept). Reset with 50,000: 0.49 s, keeps 10,000 + active; 5,000 Added: 53 ms, one notification |
| Stream to UI | `ServerConnection` | `_ui.Post` per change (5,000 posts for one burst); `List` then `Watch` sent every device and task twice | `ChangeBatcher` (1 post per burst, measured 1 post for 5,000 changes), snapshot end marker, one `Reset` per (re)connect |
| Task details | log and steps of one task | Bounded (1,000 log entries, 200 steps) | none needed |
| Add page | discovered list for a /16 range (up to 65,536 addresses, thousands of devices) | Every discovery event did two `Rows.FirstOrDefault` and four `Rows.Count(...)` (O(n) each, 10,000 events over 5,000 rows: 0.8 s plus the UI), commit results looked up with `FirstOrDefault` per result, search rebuilt the list item by item | Dictionaries by discovered id and serial, summary counters per row (O(1) per change), `ReplaceAll` for search. 5,000 devices + 5,000 login results: 96 ms; select all: 9 ms |
| Logs page | 500 entries, filter | Rebuild added entries one by one | `ReplaceAll` |
| Task plugin catalog refresh | `RequestRefresh` | Debounce restarted on every device change: with constant changes (5,000 devices polling) it never fired, or after a pause it fired for every burst | Throttle: first request schedules a call after `DebounceDelay`, at most one call per `MinRefreshInterval` (2 s), later requests are covered; test: ~150 requests in 1.5 s give 4 calls |

### Server

| Item | Checked | Finding | Fix |
|---|---|---|---|
| Polling (5,000 devices) | `DevicePollingService` | Burst every interval; next round started one interval after the previous ended (drift); the full refresh scheduler listed all devices with their API JSON every 30 s and made all 5,000 due at the same second after a start | Round starts every interval from the previous start, starts spread evenly (`PollRoundAsync`, measured about 500 per second for a 10 s window, never more than 16 in parallel), id-only queries, staggered first full refresh (after 10 min: 1 device due, 15 min: half, 20 min: all) |
| Full refresh queue | dedup, parallelism | Deduplicating, bounded (8) | unchanged |
| Device writes | `DeviceRepository.UpdateAsync` | Unchanged rows were saved and published; LastSeen-only changes published | No write without a change, no publish for LastSeen-only (measured: 0 messages for the second poll of 5,000 devices) |
| Remove, SetCredentials on 5,000 | gRPC | One transaction per device | `RemoveManyAsync` (0.2 s for 5,000), `SetManyAsync` (2.5 s for 5,000 incl. encryption) |
| Device Watch | snapshot, per-change cost | Credential lookup (one query) per change and watcher | Batches of changes share one credential-set query; snapshot end marker; snapshot of 5,000 devices with 100 APIs: 1.5 s |
| Task engine | Run on 5,000, queueing, persistence | Sequential store inserts; a redundant write per task at start; `ListAsync` inserted unstored tasks at index 0; default 8 parallel tasks per plugin means a 5,000-device Restart (~90 s each) takes ~16 h | `AddRangeAsync`, start state persisted with the device start, `PrependUnstored`; `Oadm:MaxParallelTasksPerPlugin` configuration (1..256) for large sites. Measured: Run reply 1.9 s (engine) / 0.84 s (gRPC), 5,000 trivial tasks executed and persisted in 41-45 s (about 9 ms per task in SQLite; real tasks take seconds), memory store 0.18 s |
| Task history | queries, indexes, size | No paging, no Status index, no retention, `RecoverInterruptedAsync` loaded all tasks | `ListPageAsync` (first page 9 ms, page at offset 49,900: 143 ms), `ListActiveAsync` with the new Status index (2 ms), `ListExpiredAsync` + `DeleteManyAsync`, `Tasks.RetentionDays` (90) and `Tasks.MaxHistory` (50,000), hourly `TaskRetentionHostedService`; seeding 50,000 tasks with steps in one transaction: 28 s |
| Task log table | size | At most 1,000 entries per task (existing), deleted with the task; `TaskId` index | retention also removes logs |
| ListTaskPlugins | CanRun 5,000 x N, size, frequency | See finding 7 | `TaskPluginRunnableCache` (one computation per device-table version, logs a throwing CanRun once per plugin instead of 5,000 times), compact form |
| Message sizes | gRPC limits | Device `List` 22.9 MB; task `List` with 50,000 tasks one message of tens of MB | Streams with marker, paging, gzip (`ResponseCompressionAlgorithm`, live view frames excluded), server `MaxReceiveMessageSize` 16 MB |
| SQLite | durability mode | `synchronous=FULL` (default) with WAL: every commit waits for the disk | `SqlitePragmaInterceptor` sets `synchronous=NORMAL` (consistent after a crash, the last commits may be lost on power loss) |
| Discovery | /16 range scan, logins | Range enumerated lazily with bounded parallelism (`Scan.Parallelism`), progress only on percent changes, credential candidates cached per session, 8 logins in parallel | none needed |

### Plugins

See the section "Plugins" below.

## Contracts (backward compatible)

All changes are additive or wire compatible; no field was renumbered:

- `DeviceService.Watch(WatchDevicesRequest)`, `TaskService.ListTaskPlugins(ListTaskPluginsRequest)`,
  `TaskService.List(ListTasksRequest)`, `TaskService.Watch(WatchTasksRequest)`: the new request messages
  replaced `Empty`; a request without fields (an old client) keeps the former behavior.
- New fields: `TaskPluginInfo.runnable_on_all_except = 9`, `not_runnable_device_ids = 10` (only with
  `compact`), `TaskList.total_count = 2`, `Kind.SNAPSHOT_END = 4` in `DeviceChanged` and `TaskChanged`
  (only sent when asked for).
- Settings: `Tasks.RetentionDays`, `Tasks.MaxHistory` (server-only keys like `Uploads.*`).

## Tests

| Test class | Project | Covers |
|---|---|---|
| `KeyedCoalescingChannelTests` | Core (unit) | order below the threshold, coalescing above it, Added stays Added, 5,000 x 11 changes keep every final state |
| `TaskHistoryScaleTests`, `TaskRetentionScaleTests` | Core (Perf) | 50,000 tasks: seeding, paging, active tasks, Watch snapshot with and without limit, retention, Delete all |
| `TaskEngineBatchScaleTests` | Core (Perf) | Run on 5,000 devices with SQLite and memory store, bounded parallelism, a non-reading subscriber still sees 5,000 final states |
| `DevicePollingScaleTests` | Core (Perf) | poll spreading, no publish for LastSeen-only, staggered full refresh, RemoveMany |
| `ServerScaleTests`, `ServerBatchScaleTests` | Server (Perf) | ListTaskPlugins latency, cache and compact size; device Watch snapshot of 5,000; task Watch limit 10,000 of 50,000; List paging; SetCredentials, Run, DeleteAll, Remove on 5,000 via gRPC |
| `DeviceListScaleTests`, `TaskListScaleTests` | Client (Perf) | device store reset and per-change cost, search, select all and context menu, runnable sets, catalog throttle, task store 50,000 snapshot, device re-labelling, 5,000-task batches, change batcher, add page with 5,000 discovered devices |
| `DeviceGridScaleTests` | Client (Perf, headless Avalonia) | the real Devices page with 5,000 rows: render, select all, sort, updates while sorted, search |

`manage test unit` excludes `Category=Perf` and stays fast (about 30 s for the solution);
`manage test perf` runs the scale tests (a few minutes, dominated by SQLite seeding of 50,000 tasks and
the 5,000-task Runs).

## Not changed (deliberately)

- Engine overhead per task (about 9 ms in SQLite: one read and one write per state transition) is far
  below the duration of a real task (seconds); batching state writes across tasks would complicate the
  "every state transition is persisted" rule for no visible gain.
- Default task parallelism stays 8 per plugin (firmware uploads of 100 MB each do not set their own
  limit); large sites raise `Oadm:MaxParallelTasksPerPlugin`.
- The retention settings are server-only for now (like `Uploads.*`); the Settings page is being
  reworked separately.
