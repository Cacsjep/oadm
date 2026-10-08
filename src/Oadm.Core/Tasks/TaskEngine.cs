using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Plugins;
using Oadm.Core.Uploads;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tasks;

/// <summary>
/// Runs task plugins against devices. Every task starts immediately; devices of one task run
/// with bounded parallelism per plugin: min(server setting <c>Tasks.MaxParallelPerPlugin</c> via
/// <see cref="TaskEngineOptions.MaxParallelTasksPerPluginSource"/>, <c>ITaskPlugin.MaxParallelDevices</c>),
/// read live whenever a queued task is scheduled. A task always targets exactly one device.
/// Plugin exceptions become a Failed device result and never escape the engine.
/// </summary>
public sealed partial class TaskEngine : ITaskRunner, IAsyncDisposable
{
    private readonly ITaskStore _store;
    private readonly PluginRegistry _plugins;
    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TaskEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly TaskChangeFeed _feed;
    private readonly IUploadedFiles _files;
    private readonly ITaskDeviceCredentials? _credentials;
    private readonly ITaskDeviceAddresses? _addresses;
    private readonly ConcurrentDictionary<Guid, RunningTask> _active = new();
    private readonly ConcurrentDictionary<string, PluginSlots> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public TaskEngine(
        ITaskStore store,
        PluginRegistry plugins,
        IDeviceRepository devices,
        IVapixClientFactory vapix,
        ILoggerFactory? loggerFactory = null,
        TaskEngineOptions? options = null,
        TimeProvider? timeProvider = null,
        IUploadedFiles? uploadedFiles = null,
        ITaskDeviceCredentials? credentials = null,
        ITaskDeviceAddresses? addresses = null)
    {
        _credentials = credentials;
        _addresses = addresses;
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(vapix);
        _store = store;
        _plugins = plugins;
        _devices = devices;
        _vapix = vapix;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<TaskEngine>();
        _options = options ?? new TaskEngineOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxParallelTasksPerPlugin, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxParallelTasksPerPlugin, MaxParallelLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxLogEntriesPerTask, 1);
        _files = uploadedFiles ?? NoUploadedFiles.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _feed = new TaskChangeFeed(_options.ChangeFeedCapacity, _logger);
    }

    /// <summary>Raised synchronously for every task change. Handlers must be fast and must not throw.</summary>
    public event EventHandler<TaskChange>? TaskChanged
    {
        add => _feed.Changed += value;
        remove => _feed.Changed -= value;
    }

    /// <summary>The change feed. Subscribe before listing to get a gap-free view.</summary>
    public TaskChangeFeed Changes => _feed;

    /// <inheritdoc />
    /// <remarks>
    /// One task per device, all with a new shared batch id; the payload is shared in memory by the
    /// batch and never persisted. <paramref name="ct"/> only cancels the submission, not the tasks.
    /// Use <see cref="Cancel"/>. Throws <see cref="ArgumentException"/> for an unknown plugin id or an
    /// empty device list. Inside an authenticated call the owner is the caller ("user@machine",
    /// <see cref="Auth.CallerContext"/>), whatever <paramref name="owner"/> says.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        owner = Auth.CallerContext.Current?.Owner ?? owner;
        ArgumentNullException.ThrowIfNull(deviceIds);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var distinct = deviceIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            throw new ArgumentException("A task needs at least one device.", nameof(deviceIds));
        }

        if (!_plugins.TryGetTaskPlugin(pluginId, out var registration))
        {
            throw new ArgumentException($"Unknown task plugin '{pluginId}'.", nameof(pluginId));
        }

        var batchId = Guid.NewGuid();
        var name = TaskNameFor(registration, payloadJson);
        if (distinct.Length == 1)
        {
            return [await StartTaskAsync(registration, batchId, distinct[0], payloadJson, owner, name, ct).ConfigureAwait(false)];
        }

        return await StartBatchAsync(registration, batchId, distinct, payloadJson, owner, name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Scale: a Run on 5,000 devices publishes 5,000 Added changes and writes all tasks in one store
    /// transaction (<see cref="ITaskStore.AddRangeAsync"/>) before any of them starts; the tasks then
    /// wait in Queued for a slot of their plugin.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> StartBatchAsync(RegisteredTaskPlugin registration, Guid batchId, Guid[] deviceIds, string? payloadJson, string owner, string name, CancellationToken ct)
    {
        var created = _time.GetUtcNow();
        var tasks = new List<RunningTask>(deviceIds.Length);
        var snapshots = new List<TaskRecord>(deviceIds.Length);
        foreach (var deviceId in deviceIds)
        {
            var task = new RunningTask(
                Guid.NewGuid(),
                registration,
                [deviceId],
                payloadJson,
                owner ?? string.Empty,
                created,
                _shutdown.Token,
                _options.MaxLogEntriesPerTask,
                batchId,
                name);
            tasks.Add(task);
            _active[task.Id] = task;
            lock (task.PublishLock)
            {
                // Publish Added before the store write so no later Updated can overtake it.
                var snapshot = task.Snapshot();
                snapshots.Add(snapshot);
                _feed.Publish(new TaskChange(TaskChangeKind.Added, snapshot));
            }
        }

        try
        {
            await _store.AddRangeAsync(snapshots, ct).ConfigureAwait(false);
        }
        catch
        {
            foreach (var task in tasks)
            {
                _active.TryRemove(task.Id, out _);
                _feed.Publish(new TaskChange(TaskChangeKind.Removed, task.Snapshot()));
                task.Dispose();
            }

            throw;
        }

        LogBatchQueued(batchId, registration.Id, tasks.Count, owner ?? string.Empty);
        foreach (var task in tasks)
        {
            _ = Task.Run(() => ExecuteTaskAsync(task), CancellationToken.None);
        }

        return [.. tasks.Select(t => t.Id)];
    }

    /// <summary>
    /// The task name from <see cref="ITaskPlugin.GetTaskName"/>: trimmed, without a trailing "...", at most
    /// <see cref="TaskPluginNames.MaxTaskNameLength"/> characters (longer names are shortened with an ellipsis
    /// and a warning is logged). The plugin's display name when the plugin returns nothing or throws.
    /// </summary>
    internal string TaskNameFor(RegisteredTaskPlugin registration, string? payloadJson)
    {
        string? raw;
        try
        {
            raw = registration.Plugin.GetTaskName(payloadJson);
        }
#pragma warning disable CA1031 // Plugin code is untrusted; a broken name never stops the task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTaskNameFailed(ex, registration.Id);
            return registration.DisplayName;
        }

        var stripped = TaskPluginNames.StripEllipsis(raw);
        if (stripped.Length == 0)
        {
            return registration.DisplayName;
        }

        var name = TaskPluginNames.Normalize(stripped, TaskPluginNames.MaxTaskNameLength);
        if (stripped.Length > TaskPluginNames.MaxTaskNameLength)
        {
            LogTaskNameTooLong(registration.Id, stripped.Length, TaskPluginNames.MaxTaskNameLength, name);
        }

        return name;
    }

    private async Task<Guid> StartTaskAsync(RegisteredTaskPlugin registration, Guid batchId, Guid deviceId, string? payloadJson, string owner, string name, CancellationToken ct)
    {
        var task = new RunningTask(
            Guid.NewGuid(),
            registration,
            [deviceId],
            payloadJson,
            owner ?? string.Empty,
            _time.GetUtcNow(),
            _shutdown.Token,
            _options.MaxLogEntriesPerTask,
            batchId,
            name);

        _active[task.Id] = task;
        try
        {
            lock (task.PublishLock)
            {
                // Publish Added before the store write so no later Updated can overtake it.
                _feed.Publish(new TaskChange(TaskChangeKind.Added, task.Snapshot()));
            }

            await _store.AddAsync(task.Snapshot(), ct).ConfigureAwait(false);
        }
        catch
        {
            _active.TryRemove(task.Id, out _);
            _feed.Publish(new TaskChange(TaskChangeKind.Removed, task.Snapshot()));
            task.Dispose();
            throw;
        }

        LogTaskQueued(task.Id, registration.Id, deviceId, task.Owner);
        _ = Task.Run(() => ExecuteTaskAsync(task), CancellationToken.None);
        return task.Id;
    }

    /// <summary>Largest accepted per-plugin limit (the range of the server setting).</summary>
    public const int MaxParallelLimit = 256;

    /// <summary>
    /// The per-plugin limit of the server (the setting <c>Tasks.MaxParallelPerPlugin</c> when a source is set,
    /// else <see cref="TaskEngineOptions.MaxParallelTasksPerPlugin"/>), clamped to 1..256. Read live.
    /// </summary>
    public int ServerParallelLimit
    {
        get
        {
            var value = _options.MaxParallelTasksPerPlugin;
            if (_options.MaxParallelTasksPerPluginSource is { } source)
            {
                try
                {
                    value = source();
                }
#pragma warning disable CA1031 // A failing source must not stop scheduling; the static value applies.
                catch (Exception)
#pragma warning restore CA1031
                {
                    value = _options.MaxParallelTasksPerPlugin;
                }
            }

            return Math.Clamp(value, 1, MaxParallelLimit);
        }
    }

    /// <summary>
    /// Starts queued tasks that fit under the current limits, e.g. after the setting
    /// <c>Tasks.MaxParallelPerPlugin</c> was raised. Lowering a limit never interrupts running tasks.
    /// </summary>
    public void RescheduleQueued()
    {
        foreach (var slots in _gates.Values)
        {
            slots.Pump();
        }
    }

    /// <summary>Number of running (not queued) tasks of a plugin. For tests and diagnostics.</summary>
    internal int RunningCount(string pluginId) => _gates.TryGetValue(pluginId, out var slots) ? slots.Running : 0;

    /// <summary>
    /// Concurrency limit of a plugin: min(<see cref="ServerParallelLimit"/>, its MaxParallelDevices when set).
    /// </summary>
    internal int ParallelLimit(RegisteredTaskPlugin registration)
    {
        int? requested;
        try
        {
            requested = registration.Plugin.MaxParallelDevices;
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            requested = null;
        }

        var server = ServerParallelLimit;
        return requested is > 0 ? Math.Min(server, requested.Value) : server;
    }

    /// <summary>Requests cancellation. Returns false when the task is not active.</summary>
    public bool Cancel(Guid taskId)
    {
        return _active.TryGetValue(taskId, out var task) && task.Cancel();
    }

    /// <summary>Completes when the task has finished (immediately if it is not active).</summary>
    public Task WaitForCompletionAsync(Guid taskId, CancellationToken ct)
    {
        return _active.TryGetValue(taskId, out var task) ? task.Completion.Task.WaitAsync(ct) : Task.CompletedTask;
    }

    public async Task<TaskRecord?> GetAsync(Guid taskId, CancellationToken ct)
    {
        if (_active.TryGetValue(taskId, out var task))
        {
            return task.Snapshot();
        }

        return await _store.GetAsync(taskId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The task's log, oldest first: live (including entries not persisted yet) while the task is
    /// active, else from the store. Null for an unknown task.
    /// </summary>
    public async Task<IReadOnlyList<TaskLogEntry>?> GetLogAsync(Guid taskId, CancellationToken ct)
    {
        if (_active.TryGetValue(taskId, out var task))
        {
            return task.LogSnapshot();
        }

        if (await _store.GetAsync(taskId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        return await _store.GetLogAsync(taskId, ct).ConfigureAwait(false);
    }

    /// <summary>All tasks, newest first. Active tasks reflect their live progress.</summary>
    public async Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct)
    {
        var stored = await _store.ListAsync(ct).ConfigureAwait(false);
        var result = new List<TaskRecord>(stored.Count + _active.Count);
        var seen = new HashSet<Guid>();
        foreach (var record in stored)
        {
            seen.Add(record.Id);
            result.Add(_active.TryGetValue(record.Id, out var live) ? live.Snapshot() : record);
        }

        // Tasks already published but not yet written to the store must be part of a snapshot,
        // otherwise a watcher subscribing in that window misses their Added event.
        // (Collected first: inserting each at index 0 is O(n) per task, O(n^2) for a 5,000-task Run.)
        return PrependUnstored(result, seen);
    }

    /// <summary>Live tasks not yet in <paramref name="seen"/>, newest first, followed by <paramref name="stored"/>.</summary>
    private List<TaskRecord> PrependUnstored(List<TaskRecord> stored, HashSet<Guid> seen)
    {
        var unstored = _active.Values.Where(t => seen.Add(t.Id)).Select(t => t.Snapshot()).OrderByDescending(t => t.CreatedUtc).ToList();
        if (unstored.Count == 0)
        {
            return stored;
        }

        unstored.AddRange(stored);
        return unstored;
    }

    /// <summary>
    /// One page of the history, newest first, with the total count (store paging; active tasks show
    /// their live progress). For the Tasks pane and the gRPC List with a limit.
    /// </summary>
    public async Task<TaskPage> ListPageAsync(int offset, int limit, CancellationToken ct)
    {
        var page = await _store.ListPageAsync(offset, limit, ct).ConfigureAwait(false);
        return page with { Tasks = [.. page.Tasks.Select(r => _active.TryGetValue(r.Id, out var live) ? live.Snapshot() : r)] };
    }

    /// <summary>
    /// Snapshot for a watcher: every active task (also those not stored yet) plus the newest
    /// <paramref name="limit"/> tasks of the history (null = all, the former behavior), newest first.
    /// With 50,000 tasks in the history a client asks for a few thousand instead of all.
    /// </summary>
    public async Task<IReadOnlyList<TaskRecord>> SnapshotAsync(int? limit, CancellationToken ct)
    {
        if (limit is null)
        {
            return await ListAsync(ct).ConfigureAwait(false);
        }

        var recent = await _store.ListPageAsync(0, limit.Value, ct).ConfigureAwait(false);
        var active = await _store.ListActiveAsync(ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        var result = new List<TaskRecord>(recent.Tasks.Count + active.Count);
        foreach (var record in active.Concat(recent.Tasks).OrderByDescending(r => r.CreatedUtc))
        {
            if (seen.Add(record.Id))
            {
                result.Add(_active.TryGetValue(record.Id, out var live) ? live.Snapshot() : record);
            }
        }

        return PrependUnstored(result, seen);
    }

    /// <summary>
    /// Subscribes to the change feed, then takes <see cref="SnapshotAsync"/>: nothing that happens in
    /// between is lost (it may be delivered twice). The caller disposes the subscription.
    /// </summary>
    public async Task<(IReadOnlyList<TaskRecord> Snapshot, TaskChangeSubscription Subscription)> SubscribeWithSnapshotAsync(int? limit, CancellationToken ct)
    {
        var subscription = _feed.Subscribe();
        try
        {
            return (await SnapshotAsync(limit, ct).ConfigureAwait(false), subscription);
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Clears the history: cancels every active task, waits for them to stop, then deletes all tasks in
    /// one store transaction and publishes one Removed per task. Returns the number deleted.
    /// </summary>
    public async Task<int> DeleteAllAsync(CancellationToken ct)
    {
        var active = _active.Values.ToList();
        foreach (var task in active)
        {
            task.Cancel();
        }

        await Task.WhenAll(active.Select(t => t.Completion.Task)).WaitAsync(ct).ConfigureAwait(false);
        var ids = await _store.ListIdsAsync(ct).ConfigureAwait(false);
        return await DeleteManyAsync(ids, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Task history retention: deletes finished tasks older than <paramref name="maxAge"/> and finished
    /// tasks beyond the newest <paramref name="maxTasks"/> (null disables a rule). Active tasks are never
    /// deleted. Returns the number deleted.
    /// </summary>
    public async Task<int> PruneHistoryAsync(TimeSpan? maxAge, int? maxTasks, CancellationToken ct)
    {
        DateTimeOffset? before = maxAge is { } age ? _time.GetUtcNow() - age : null;
        var expired = await _store.ListExpiredAsync(before, maxTasks, ct).ConfigureAwait(false);
        var deleted = await DeleteManyAsync([.. expired.Where(id => !_active.ContainsKey(id))], ct).ConfigureAwait(false);
        if (deleted > 0)
        {
            LogPruned(deleted, maxAge?.TotalDays, maxTasks);
        }

        return deleted;
    }

    private async Task<int> DeleteManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        var deleted = await _store.DeleteManyAsync(ids, ct).ConfigureAwait(false);
        foreach (var record in deleted)
        {
            _feed.Publish(new TaskChange(TaskChangeKind.Removed, record));
        }

        return deleted.Count;
    }

    /// <summary>Deletes a task from history. An active task is cancelled first and deleted once it stopped.</summary>
    public async Task<bool> DeleteAsync(Guid taskId, CancellationToken ct)
    {
        if (_active.TryGetValue(taskId, out var task))
        {
            task.Cancel();
            await task.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        var existing = await _store.GetAsync(taskId, ct).ConfigureAwait(false);
        var deleted = await _store.DeleteAsync(taskId, ct).ConfigureAwait(false);
        if (deleted && existing is not null)
        {
            _feed.Publish(new TaskChange(TaskChangeKind.Removed, existing));
        }

        return deleted;
    }

    /// <summary>
    /// Streams task changes. When <paramref name="includeSnapshot"/> is true, starts with one
    /// <see cref="TaskChangeKind.Added"/> per existing task (the gRPC Watch contract). Changes
    /// that race with the snapshot may be delivered again; every change is a full snapshot.
    /// </summary>
    public async IAsyncEnumerable<TaskChange> WatchAsync(bool includeSnapshot, [EnumeratorCancellation] CancellationToken ct)
    {
        using var subscription = _feed.Subscribe();
        if (includeSnapshot)
        {
            foreach (var record in await ListAsync(ct).ConfigureAwait(false))
            {
                yield return new TaskChange(TaskChangeKind.Added, record);
            }
        }

        await foreach (var change in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return change;
        }
    }

    /// <summary>
    /// Call once at startup: tasks left Queued or Running by a previous server process are
    /// marked Failed, since their execution is gone.
    /// </summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        const string Message = "Server stopped while the task was running.";
        var now = _time.GetUtcNow();
        foreach (var record in await _store.ListActiveAsync(ct).ConfigureAwait(false))
        {
            if (record.State.IsTerminal() || _active.ContainsKey(record.Id))
            {
                continue;
            }

            var devices = record.Devices
                .Select(d => d.State.IsTerminal() ? d : d with { State = TaskState.Failed, Message = Message })
                .ToArray();
            var steps = record.Steps
                .Select(s => s.State switch
                {
                    TaskStepState.Running => s with { State = TaskStepState.Failed, Detail = Message, FinishedUtc = now },
                    TaskStepState.Pending => s with { State = TaskStepState.Skipped, Detail = "Not run: the server stopped." },
                    _ => s,
                })
                .ToArray();
            var recovered = record with { State = TaskState.Failed, FinishedUtc = now, Progress = 100, Devices = devices, Steps = steps };
            await _store.UpdateAsync(recovered, ct).ConfigureAwait(false);
            _feed.Publish(new TaskChange(TaskChangeKind.Updated, recovered));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        var pending = _active.Values.Select(t => t.Completion.Task).ToArray();
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogShutdownTimeout(pending.Count(t => !t.IsCompleted));
        }

        _feed.Complete();
        _shutdown.Dispose();
    }

    private async Task ExecuteTaskAsync(RunningTask task)
    {
        var registration = task.Registration;
        var gate = _gates.GetOrAdd(registration.Id, id => new PluginSlots(
            () => ParallelLimit(_plugins.TryGetTaskPlugin(id, out var current) ? current : registration)));
        var entered = false;
        try
        {
            // Queued until the plugin has a free slot; a cancel while waiting ends it as Cancelled.
            try
            {
                await gate.WaitAsync(task.Token).ConfigureAwait(false);
                entered = true;
            }
            catch (OperationCanceledException) when (task.Token.IsCancellationRequested)
            {
            }

            if (entered)
            {
                task.MarkStarted(_time.GetUtcNow());
                // Published now, persisted by the device start right after (one store write less per task:
                // a Run on 5,000 devices saves 5,000 transactions).
                await PublishAsync(task, persist: false).ConfigureAwait(false);
            }

            foreach (var deviceId in task.DeviceIds)
            {
                await ExecuteDeviceAsync(task, deviceId).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The engine must never crash the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEngineFault(ex, task.Id);
            task.Steps?.FailCurrent(ex.Message);
            task.Steps?.Close(succeeded: false, "Not run: an earlier step failed.");
            task.FailUnfinished(ex.Message);
        }
        finally
        {
            if (entered)
            {
                gate.Release();
            }

            await AwaitPendingWritesAsync(task).ConfigureAwait(false);
            task.Finish(_time.GetUtcNow());
            await PublishAsync(task, persist: true).ConfigureAwait(false);
            var final = task.Snapshot();
            LogTaskFinished(task.Id, final.PluginId, final.State);
            _active.TryRemove(task.Id, out _);
            task.Completion.TrySetResult();
            task.Dispose();
        }
    }

    private async Task ExecuteDeviceAsync(RunningTask task, Guid deviceId)
    {
        var ct = task.Token;
        if (ct.IsCancellationRequested)
        {
            task.SetDevice(deviceId, TaskState.Cancelled, "Cancelled before start.", null);
            AddLog(task, deviceId, TaskLogLevel.Info, "Cancelled before start.");
            await PublishAsync(task, persist: true).ConfigureAwait(false);
            return;
        }

        task.SetDevice(deviceId, TaskState.Running, null, 0);
        await PublishAsync(task, persist: true).ConfigureAwait(false);

        var plugin = task.Registration.Plugin;
        var pluginLogger = _loggerFactory.CreateLogger("Oadm.Plugins." + plugin.Id);
        using var scope = pluginLogger.BeginScope(new Dictionary<string, object> { ["TaskId"] = task.Id, ["DeviceId"] = deviceId });
        TaskStepList? steps = null;
        try
        {
            var device = await _devices.FindAsync(deviceId, ct).ConfigureAwait(false);
            var precondition = CheckPrecondition(device, task);
            if (precondition is not null)
            {
                task.SetDevice(deviceId, TaskState.Failed, precondition, null);
                AddLog(task, deviceId, TaskLogLevel.Error, precondition);
            }
            else
            {
                var vapix = await _vapix.CreateAsync(deviceId, ct).ConfigureAwait(false);
                var context = new TaskExecutionContext(
                    task.Id,
                    deviceId,
                    vapix,
                    pluginLogger,
                    task.Registration.Owner,
                    _files,
                    new Sink(this, task),
                    _time);
                steps = context.Steps;
                task.AttachSteps(steps);

                await plugin.ExecuteAsync(context, device!, task.PayloadJson, ct).ConfigureAwait(false);
                steps.Close(succeeded: true, "Not run.");
                task.SetDevice(deviceId, task.SuccessState(deviceId), task.LastWarning(deviceId), 100);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            steps?.FailCurrent("Cancelled.");
            steps?.Close(succeeded: false, "Not run: the task was cancelled.");
            task.SetDevice(deviceId, TaskState.Cancelled, "Cancelled.", steps?.Progress);
            AddLog(task, deviceId, TaskLogLevel.Info, "Cancelled.");
        }
#pragma warning disable CA1031 // Plugin failures are isolated per device.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDeviceFailed(ex, task.Id, deviceId, plugin.Id);
            steps?.FailCurrent(ex.Message);
            steps?.Close(succeeded: false, "Not run: an earlier step failed.");
            task.SetDevice(deviceId, TaskState.Failed, ex.Message, steps?.Progress);
            AddLog(task, deviceId, TaskLogLevel.Error, ex.Message);
        }

        await PublishAsync(task, persist: true).ConfigureAwait(false);
    }

    private static string? CheckPrecondition(IDeviceInfo? device, RunningTask task)
    {
        if (device is null)
        {
            return "Device not found.";
        }

        if (device.Status == DeviceStatus.CertificateChanged)
        {
            return "The device certificate changed. Accept the new certificate before running tasks.";
        }

        return task.Registration.Plugin.CanRun(device)
            ? null
            : $"{task.Registration.DisplayName} cannot run on this device.";
    }

    private void PublishProgress(RunningTask task)
    {
        lock (task.PublishLock)
        {
            _feed.Publish(new TaskChange(TaskChangeKind.Updated, task.Snapshot()));
        }
    }

    private async Task PublishAsync(RunningTask task, bool persist)
    {
        PublishProgress(task);
        if (persist)
        {
            await PersistAsync(task).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the current snapshot, then the log entries not written yet, in order.</summary>
    private async Task PersistAsync(RunningTask task)
    {
        await task.PersistLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _store.UpdateAsync(task.Snapshot(), CancellationToken.None).ConfigureAwait(false);
            var log = task.TakeUnpersistedLog();
            if (log.Count > 0)
            {
                await _store.AppendLogAsync(task.Id, log, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A store failure must not stop the task; the feed still has the state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPersistFailed(ex, task.Id);
        }
        finally
        {
            task.PersistLock.Release();
        }
    }

    private void AddLog(RunningTask task, Guid? deviceId, TaskLogLevel level, string message) =>
        task.AddLog(new TaskLogEntry(_time.GetUtcNow(), deviceId, level, message));

    /// <summary>At most one store write per device and <see cref="TaskEngineOptions.ProgressPersistInterval"/>, in the background.</summary>
    private void PersistThrottled(RunningTask task, Guid deviceId)
    {
        if (task.TryBeginThrottledPersist(deviceId, _time.GetUtcNow(), _options.ProgressPersistInterval))
        {
            task.TrackWrite(Task.Run(() => PersistAsync(task)));
        }
    }

    private static async Task AwaitPendingWritesAsync(RunningTask task)
    {
        try
        {
            await task.WhenWritesDone().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // PersistAsync already logs; nothing may keep the task from finishing.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Receives progress, warnings and log entries from the plugin contexts of one task.</summary>
    private sealed class Sink(TaskEngine engine, RunningTask task) : ITaskExecutionSink
    {
        public void ReportProgress(Guid deviceId, int percent, string? message)
        {
            if (!task.ReportProgress(deviceId, percent, message))
            {
                return;
            }

            engine.PublishProgress(task);
            if (message is not null)
            {
                engine.PersistThrottled(task, deviceId);
            }
        }

        public void ReportWarning(Guid deviceId, string message)
        {
            if (!task.ReportWarning(deviceId, message))
            {
                return;
            }

            engine.AddLog(task, deviceId, TaskLogLevel.Warning, message);
            engine.PublishProgress(task);
            engine.PersistThrottled(task, deviceId);
        }

        public void Log(Guid deviceId, TaskLogLevel level, string message)
        {
            engine.AddLog(task, deviceId, level, message);
            engine.PersistThrottled(task, deviceId);
        }

        public void StepsChanged(Guid deviceId, TaskStepList steps)
        {
            var snapshot = steps.Snapshot();
            var running = snapshot.FirstOrDefault(s => s.State == TaskStepState.Running);
            task.SetStepMessage(deviceId, running);
            engine.PublishProgress(task);
            engine.PersistThrottled(task, deviceId);
        }

        public void MarkCredentialsInvalid(Guid deviceId)
        {
            var credentials = engine._credentials ?? throw new NotSupportedException("Stored credentials cannot be changed here.");
            engine.AddLog(task, deviceId, TaskLogLevel.Warning, "The device no longer accepts the stored credentials; they were removed.");
            engine.LogCredentialsInvalidated(task.Id, deviceId);
            task.TrackWrite(Task.Run(async () =>
            {
                try
                {
                    await credentials.InvalidateAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Logged; the task itself goes on.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    engine.LogCredentialsChangeFailed(ex, task.Id, deviceId);
                }
            }));
        }

        public async Task<IVapixClient> UpdateCredentialsAsync(Guid deviceId, string userName, string password, CancellationToken ct)
        {
            var credentials = engine._credentials ?? throw new NotSupportedException("Stored credentials cannot be changed here.");
            await credentials.UpdateAsync(deviceId, userName, password, ct).ConfigureAwait(false);
            engine.AddLog(task, deviceId, TaskLogLevel.Info, $"Stored credentials updated (user {userName}).");
            engine.LogCredentialsUpdated(task.Id, deviceId, userName);
            return await engine._vapix.CreateAsync(deviceId, ct).ConfigureAwait(false);
        }

        public Task<IVapixClient> CreateClientForAsync(Guid deviceId, string address, CancellationToken ct)
        {
            var addresses = engine._addresses ?? throw new NotSupportedException("Clients for other addresses cannot be created here.");
            return addresses.CreateClientAsync(deviceId, address, ct);
        }

        public async Task<(bool Updated, IVapixClient? Client)> UpdateDeviceAddressAsync(Guid deviceId, string newAddress, CancellationToken ct)
        {
            var addresses = engine._addresses ?? throw new NotSupportedException("The device address cannot be changed here.");
            DeviceAddressChangeResult result;
            try
            {
                result = await addresses.UpdateAddressAsync(deviceId, newAddress, ct).ConfigureAwait(false);
            }
            catch (DeviceIdentityException ex)
            {
                engine.AddLog(task, deviceId, TaskLogLevel.Warning, ex.Message);
                throw;
            }

            switch (result)
            {
                case DeviceAddressChangeResult.Updated:
                    engine.AddLog(task, deviceId, TaskLogLevel.Info, $"OADM device address changed to {newAddress}.");
                    engine.LogDeviceAddressUpdated(task.Id, deviceId, newAddress);
                    return (true, await engine._vapix.CreateAsync(deviceId, ct).ConfigureAwait(false));
                case DeviceAddressChangeResult.KeptHostName:
                    engine.AddLog(task, deviceId, TaskLogLevel.Info, "OADM reaches the device by host name; the host name is kept.");
                    return (false, null);
                default:
                    return (false, null);
            }
        }

        public async Task<IVapixClient> UpdateDeviceTlsAsync(Guid deviceId, string scheme, string? expectedFingerprintSha256, CancellationToken ct)
        {
            var addresses = engine._addresses ?? throw new NotSupportedException("The device connection cannot be changed here.");
            try
            {
                await addresses.UpdateTlsAsync(deviceId, scheme, expectedFingerprintSha256, ct).ConfigureAwait(false);
            }
            catch (DeviceIdentityException ex)
            {
                engine.AddLog(task, deviceId, TaskLogLevel.Warning, ex.Message);
                throw;
            }

            var https = string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            engine.AddLog(task, deviceId, TaskLogLevel.Info, https ? "OADM now connects over HTTPS and trusts the new certificate." : "OADM now connects over HTTP.");
            return await engine._vapix.CreateAsync(deviceId, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: GetTaskName failed; the display name is used")]
    private partial void LogTaskNameFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: task name has {Length} characters, more than {Max}; shown as '{Name}'")]
    private partial void LogTaskNameTooLong(string pluginId, int length, int max, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId}: device {DeviceId} now has the address {Address}")]
    private partial void LogDeviceAddressUpdated(Guid taskId, Guid deviceId, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId}: stored credentials of device {DeviceId} removed (no longer accepted)")]
    private partial void LogCredentialsInvalidated(Guid taskId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId}: stored credentials of device {DeviceId} updated (user {UserName})")]
    private partial void LogCredentialsUpdated(Guid taskId, Guid deviceId, string userName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Task {TaskId}: changing the stored credentials of device {DeviceId} failed")]
    private partial void LogCredentialsChangeFailed(Exception ex, Guid taskId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId} queued: plugin {PluginId}, device {DeviceId}, owner {Owner}")]
    private partial void LogTaskQueued(Guid taskId, string pluginId, Guid deviceId, string owner);

    [LoggerMessage(Level = LogLevel.Information, Message = "Batch {BatchId}: {Count} tasks queued: plugin {PluginId}, owner {Owner}")]
    private partial void LogBatchQueued(Guid batchId, string pluginId, int count, string owner);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task history retention deleted {Count} finished task(s) (max age {Days} days, max tasks {MaxTasks})")]
    private partial void LogPruned(int count, double? days, int? maxTasks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId} ({PluginId}) finished: {State}")]
    private partial void LogTaskFinished(Guid taskId, string pluginId, TaskState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task {TaskId}: plugin {PluginId} failed on device {DeviceId}")]
    private partial void LogDeviceFailed(Exception ex, Guid taskId, Guid deviceId, string pluginId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Task {TaskId}: engine fault")]
    private partial void LogEngineFault(Exception ex, Guid taskId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Task {TaskId}: persisting state failed")]
    private partial void LogPersistFailed(Exception ex, Guid taskId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task engine shutdown: {Count} task(s) did not stop in time")]
    private partial void LogShutdownTimeout(int count);
}
