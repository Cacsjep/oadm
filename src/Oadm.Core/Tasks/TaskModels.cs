namespace Oadm.Core.Tasks;

/// <summary>Lifecycle state of a task and of each device inside it. Mirrors the TaskState proto enum.</summary>
public enum TaskState
{
    Queued = 1,
    Running = 2,
    Done = 3,
    Failed = 4,
    Cancelled = 5,
}

public static class TaskStateExtensions
{
    public static bool IsTerminal(this TaskState state) => state is TaskState.Done or TaskState.Failed or TaskState.Cancelled;
}

/// <summary>Immutable snapshot of a task, as persisted by <see cref="ITaskStore"/> and published on the change feed.</summary>
public sealed record TaskRecord(
    Guid Id,
    string PluginId,
    string Name,
    TaskState State,
    string Owner,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    int Progress,
    string? PayloadJson,
    IReadOnlyList<TaskDeviceRecord> Devices);

/// <summary>Per-device result inside a task.</summary>
public sealed record TaskDeviceRecord(Guid DeviceId, TaskState State, string? Message, int Progress);

public enum TaskChangeKind
{
    Added = 1,
    Updated = 2,
    Removed = 3,
}

/// <summary>One entry of the task change feed. Always carries the full task snapshot.</summary>
public sealed record TaskChange(TaskChangeKind Kind, TaskRecord Task);
