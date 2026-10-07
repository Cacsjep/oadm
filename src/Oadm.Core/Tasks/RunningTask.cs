using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tasks;

/// <summary>Mutable in-memory state of a task while the engine executes it. Thread-safe.</summary>
internal sealed class RunningTask : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Guid[] _deviceOrder;
    private readonly Dictionary<Guid, DeviceSlot> _devices;
    private readonly CancellationTokenSource _cts;
    private readonly List<TaskLogEntry> _log = [];
    private readonly List<Task> _pendingWrites = [];
    private readonly int _maxLogEntries;
    private int _persistedLogCount;
    private TaskState _state = TaskState.Queued;
    private DateTimeOffset? _started;
    private DateTimeOffset? _finished;
    private bool _cancelRequested;
    private bool _disposed;
    private TaskStepList? _steps;

    public RunningTask(
        Guid id,
        RegisteredTaskPlugin registration,
        Guid[] deviceIds,
        string? payloadJson,
        string owner,
        DateTimeOffset created,
        CancellationToken shutdown,
        int maxLogEntries = 1000,
        Guid batchId = default)
    {
        BatchId = batchId == Guid.Empty ? id : batchId;
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLogEntries, 1);
        _maxLogEntries = maxLogEntries;
        Id = id;
        Registration = registration;
        PayloadJson = payloadJson;
        Owner = owner;
        Created = created;
        Name = registration.Plugin.DisplayName;
        _deviceOrder = deviceIds;
        _devices = deviceIds.ToDictionary(d => d, _ => new DeviceSlot());
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        Token = _cts.Token;
    }

    public Guid Id { get; }

    /// <summary>Shared by all tasks started by one RunAsync call (one task per device).</summary>
    public Guid BatchId { get; }

    public RegisteredTaskPlugin Registration { get; }

    public string Name { get; }

    /// <summary>Handed to the plugin only; never part of a <see cref="Snapshot"/> (may carry secrets).</summary>
    public string? PayloadJson { get; }

    public string Owner { get; }

    public DateTimeOffset Created { get; }

    public IReadOnlyList<Guid> DeviceIds => _deviceOrder;

    public CancellationToken Token { get; }

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Serializes store writes so snapshots are persisted in order.</summary>
    public SemaphoreSlim PersistLock { get; } = new(1, 1);

    /// <summary>Guards snapshot + feed publish so subscribers see changes in order.</summary>
    public Lock PublishLock { get; } = new();

    /// <summary>The step list of the running plugin context (one device per task). Set before the plugin starts.</summary>
    public void AttachSteps(TaskStepList steps)
    {
        lock (_sync)
        {
            _steps = steps;
        }
    }

    /// <summary>The step list of the plugin, if it started.</summary>
    public TaskStepList? Steps
    {
        get
        {
            lock (_sync)
            {
                return _steps;
            }
        }
    }

    /// <summary>
    /// The running step becomes the device message ("Upload firmware - 12 of 80 MB") so the tooltip and
    /// older clients show what the task is doing. Returns false when the device is not running.
    /// </summary>
    public bool SetStepMessage(Guid deviceId, TaskStepInfo? running)
    {
        lock (_sync)
        {
            var slot = _devices[deviceId];
            if (slot.State != TaskState.Running || running is null)
            {
                return false;
            }

            slot.Message = running.Detail is null ? running.Name : $"{running.Name} - {running.Detail}";
            return true;
        }
    }

    /// <summary>The last warning of the device, the final message of a "Done with warnings" device.</summary>
    public string? LastWarning(Guid deviceId)
    {
        lock (_sync)
        {
            return _devices[deviceId].LastWarning;
        }
    }

    public bool Cancel()
    {
        lock (_sync)
        {
            if (_disposed || _state.IsTerminal())
            {
                return false;
            }

            _cancelRequested = true;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public void MarkStarted(DateTimeOffset now)
    {
        lock (_sync)
        {
            _state = TaskState.Running;
            _started = now;
        }
    }

    /// <summary>Sets a device state. Ignored when the device already reached a terminal state.</summary>
    public bool SetDevice(Guid deviceId, TaskState state, string? message, int? progress)
    {
        lock (_sync)
        {
            var slot = _devices[deviceId];
            if (slot.State.IsTerminal())
            {
                return false;
            }

            slot.State = state;
            if (message is not null || state.IsTerminal())
            {
                slot.Message = message ?? slot.Message;
            }

            if (progress is { } p)
            {
                slot.Progress = Math.Clamp(p, 0, 100);
            }

            return true;
        }
    }

    /// <summary>Updates progress of a running device. Returns false when the device is not running.</summary>
    public bool ReportProgress(Guid deviceId, int percent, string? message)
    {
        lock (_sync)
        {
            var slot = _devices[deviceId];
            if (slot.State != TaskState.Running)
            {
                return false;
            }

            slot.Progress = Math.Clamp(percent, 0, 100);
            slot.ExplicitProgress = true;
            if (message is not null)
            {
                slot.Message = message;
            }

            return true;
        }
    }

    /// <summary>Marks a running device as "done with warnings" at its end and makes the warning its message.</summary>
    public bool ReportWarning(Guid deviceId, string message)
    {
        lock (_sync)
        {
            var slot = _devices[deviceId];
            if (slot.State != TaskState.Running)
            {
                return false;
            }

            slot.HasWarning = true;
            slot.LastWarning = message;
            slot.Message = message;
            return true;
        }
    }

    /// <summary>Done, or DoneWithWarnings when the plugin reported a warning for this device.</summary>
    public TaskState SuccessState(Guid deviceId)
    {
        lock (_sync)
        {
            return _devices[deviceId].HasWarning ? TaskState.DoneWithWarnings : TaskState.Done;
        }
    }

    /// <summary>
    /// True (and the time is remembered) when the device is running and its last throttled write is
    /// at least <paramref name="interval"/> ago.
    /// </summary>
    public bool TryBeginThrottledPersist(Guid deviceId, DateTimeOffset now, TimeSpan interval)
    {
        lock (_sync)
        {
            var slot = _devices[deviceId];
            if (_disposed || slot.State != TaskState.Running || (slot.LastPersist is { } last && now - last < interval))
            {
                return false;
            }

            slot.LastPersist = now;
            return true;
        }
    }

    /// <summary>Adds a log entry unless the cap is reached; the last kept entry says that entries were dropped.</summary>
    public bool AddLog(TaskLogEntry entry)
    {
        lock (_sync)
        {
            if (_disposed || _log.Count >= _maxLogEntries)
            {
                return false;
            }

            _log.Add(_log.Count == _maxLogEntries - 1
                ? new TaskLogEntry(entry.TimeUtc, null, TaskLogLevel.Warning, $"Log limit of {_maxLogEntries} entries reached; later entries are not kept.")
                : entry);
            return true;
        }
    }

    /// <summary>The whole log kept so far (persisted or not), oldest first.</summary>
    public IReadOnlyList<TaskLogEntry> LogSnapshot()
    {
        lock (_sync)
        {
            return [.. _log];
        }
    }

    /// <summary>Entries not handed out for persisting yet. Call under <see cref="PersistLock"/>.</summary>
    public IReadOnlyList<TaskLogEntry> TakeUnpersistedLog()
    {
        lock (_sync)
        {
            if (_persistedLogCount >= _log.Count)
            {
                return [];
            }

            var pending = _log.GetRange(_persistedLogCount, _log.Count - _persistedLogCount);
            _persistedLogCount = _log.Count;
            return pending;
        }
    }

    /// <summary>Remembers a background store write so the task does not finish (and dispose) before it.</summary>
    public void TrackWrite(Task write)
    {
        lock (_sync)
        {
            _pendingWrites.RemoveAll(t => t.IsCompleted);
            _pendingWrites.Add(write);
        }
    }

    /// <summary>Completes when all tracked background writes have completed.</summary>
    public Task WhenWritesDone()
    {
        lock (_sync)
        {
            return Task.WhenAll(_pendingWrites);
        }
    }

    /// <summary>Marks every device that has not finished as failed (used when the engine itself faults).</summary>
    public void FailUnfinished(string message)
    {
        lock (_sync)
        {
            foreach (var slot in _devices.Values.Where(s => !s.State.IsTerminal()))
            {
                slot.State = TaskState.Failed;
                slot.Message = message;
            }
        }
    }

    public void Finish(DateTimeOffset now)
    {
        lock (_sync)
        {
            var slots = _devices.Values;
            _state = slots.Any(s => s.State == TaskState.Cancelled) && (_cancelRequested || !slots.Any(s => s.State == TaskState.Failed))
                ? TaskState.Cancelled
                : slots.Any(s => s.State == TaskState.Failed)
                    ? TaskState.Failed
                    : slots.Any(s => s.State == TaskState.DoneWithWarnings)
                        ? TaskState.DoneWithWarnings
                        : TaskState.Done;
            _started ??= now;
            _finished = now;
        }
    }

    public TaskRecord Snapshot()
    {
        lock (_sync)
        {
            // Without explicit ReportProgress calls the progress of a running device follows its steps.
            var stepProgress = _steps?.Progress;
            var devices = _deviceOrder
                .Select(id =>
                {
                    var s = _devices[id];
                    var progress = s.State == TaskState.Running && !s.ExplicitProgress && stepProgress is { } p ? p : s.Progress;
                    return new TaskDeviceRecord(id, s.State, s.Message, progress);
                })
                .ToArray();

            var progress = _state.IsTerminal()
                ? 100
                : (int)Math.Round(devices.Average(d => d.State.IsTerminal() ? 100 : d.Progress));

            return new TaskRecord(
                Id,
                Registration.Plugin.Id,
                Name,
                _state,
                Owner,
                Created,
                _started,
                _finished,
                progress,
                null,
                devices,
                BatchId)
            {
                Steps = _steps?.Snapshot() ?? [],
            };
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Dispose();
        PersistLock.Dispose();
    }

    private sealed class DeviceSlot
    {
        public TaskState State { get; set; } = TaskState.Queued;

        public string? Message { get; set; }

        public int Progress { get; set; }

        public bool HasWarning { get; set; }

        public string? LastWarning { get; set; }

        /// <summary>The plugin called ReportProgress itself; its value wins over the step-derived progress.</summary>
        public bool ExplicitProgress { get; set; }

        /// <summary>Last throttled (progress, log, warning) store write for this device.</summary>
        public DateTimeOffset? LastPersist { get; set; }
    }
}
