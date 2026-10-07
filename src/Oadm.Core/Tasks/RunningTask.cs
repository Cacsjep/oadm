using Oadm.Core.Plugins;

namespace Oadm.Core.Tasks;

/// <summary>Mutable in-memory state of a task while the engine executes it. Thread-safe.</summary>
internal sealed class RunningTask : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Guid[] _deviceOrder;
    private readonly Dictionary<Guid, DeviceSlot> _devices;
    private readonly CancellationTokenSource _cts;
    private TaskState _state = TaskState.Queued;
    private DateTimeOffset? _started;
    private DateTimeOffset? _finished;
    private bool _cancelRequested;
    private bool _disposed;

    public RunningTask(
        Guid id,
        RegisteredTaskPlugin registration,
        Guid[] deviceIds,
        string? payloadJson,
        string owner,
        DateTimeOffset created,
        CancellationToken shutdown)
    {
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

    public RegisteredTaskPlugin Registration { get; }

    public string Name { get; }

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
            if (message is not null)
            {
                slot.Message = message;
            }

            return true;
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
                    : TaskState.Done;
            _started ??= now;
            _finished = now;
        }
    }

    public TaskRecord Snapshot()
    {
        lock (_sync)
        {
            var devices = _deviceOrder
                .Select(id =>
                {
                    var s = _devices[id];
                    return new TaskDeviceRecord(id, s.State, s.Message, s.Progress);
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
                PayloadJson,
                devices);
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
    }
}
