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
/// with bounded parallelism (<see cref="TaskEngineOptions.MaxParallelDevicesPerTask"/>).
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
    private readonly ConcurrentDictionary<Guid, RunningTask> _active = new();
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
        IUploadedFiles? uploadedFiles = null)
    {
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
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxParallelDevicesPerTask, 1);
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
    /// <paramref name="ct"/> only cancels the submission, not the task. Use <see cref="Cancel"/>.
    /// Throws <see cref="ArgumentException"/> for an unknown plugin id or an empty device list.
    /// </remarks>
    public async Task<Guid> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
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

        var task = new RunningTask(
            Guid.NewGuid(),
            registration,
            distinct,
            payloadJson,
            owner ?? string.Empty,
            _time.GetUtcNow(),
            _shutdown.Token,
            _options.MaxLogEntriesPerTask);

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

        LogTaskQueued(task.Id, pluginId, distinct.Length, task.Owner);
        _ = Task.Run(() => ExecuteTaskAsync(task), CancellationToken.None);
        return task.Id;
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
        foreach (var live in _active.Values)
        {
            if (seen.Add(live.Id))
            {
                result.Insert(0, live.Snapshot());
            }
        }

        return result;
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
        foreach (var record in await _store.ListAsync(ct).ConfigureAwait(false))
        {
            if (record.State.IsTerminal() || _active.ContainsKey(record.Id))
            {
                continue;
            }

            var devices = record.Devices
                .Select(d => d.State.IsTerminal() ? d : d with { State = TaskState.Failed, Message = Message })
                .ToArray();
            var recovered = record with { State = TaskState.Failed, FinishedUtc = now, Progress = 100, Devices = devices };
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
        try
        {
            task.MarkStarted(_time.GetUtcNow());
            await PublishAsync(task, persist: true).ConfigureAwait(false);

            var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelDevicesPerTask };
            await Parallel.ForEachAsync(task.DeviceIds, parallel, (deviceId, _) => new ValueTask(ExecuteDeviceAsync(task, deviceId)))
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The engine must never crash the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEngineFault(ex, task.Id);
            task.FailUnfinished(ex.Message);
        }
        finally
        {
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
                    new Sink(this, task));

                await plugin.ExecuteAsync(context, device!, task.PayloadJson, ct).ConfigureAwait(false);
                task.SetDevice(deviceId, task.SuccessState(deviceId), null, 100);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            task.SetDevice(deviceId, TaskState.Cancelled, "Cancelled.", null);
            AddLog(task, deviceId, TaskLogLevel.Info, "Cancelled.");
        }
#pragma warning disable CA1031 // Plugin failures are isolated per device.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDeviceFailed(ex, task.Id, deviceId, plugin.Id);
            task.SetDevice(deviceId, TaskState.Failed, ex.Message, null);
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
            : $"{task.Name} cannot run on this device.";
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
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId} queued: plugin {PluginId}, {DeviceCount} device(s), owner {Owner}")]
    private partial void LogTaskQueued(Guid taskId, string pluginId, int deviceCount, string owner);

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
