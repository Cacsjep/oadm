namespace Oadm.Core.Tasks;

/// <summary>Lifecycle state of a task and of each device inside it. Mirrors the TaskState proto enum.</summary>
public enum TaskState
{
    Queued = 1,
    Running = 2,
    Done = 3,
    Failed = 4,
    Cancelled = 5,

    /// <summary>Finished, but the plugin reported at least one warning (<c>ReportWarning</c>).</summary>
    DoneWithWarnings = 6,
}

public static class TaskStateExtensions
{
    public static bool IsTerminal(this TaskState state) => state is TaskState.Done or TaskState.DoneWithWarnings or TaskState.Failed or TaskState.Cancelled;
}

/// <summary>
/// Immutable snapshot of a task, as persisted by <see cref="ITaskStore"/> and published on the change feed.
/// <see cref="PayloadJson"/> is always null in engine snapshots: payloads may carry secrets and live only
/// in memory while the task runs (never persisted, never logged).
/// </summary>
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

/// <summary>Per-device result inside a task. <see cref="Message"/> is the last progress message, warning or error.</summary>
public sealed record TaskDeviceRecord(Guid DeviceId, TaskState State, string? Message, int Progress);

/// <summary>One entry of a task's log (<c>ITaskExecutionContext.Log</c>, warnings, failures). Null device: task level.</summary>
public sealed record TaskLogEntry(DateTimeOffset TimeUtc, Guid? DeviceId, Oadm.Sdk.Plugins.TaskLogLevel Level, string Message);

public enum TaskChangeKind
{
    Added = 1,
    Updated = 2,
    Removed = 3,
}

/// <summary>One entry of the task change feed. Always carries the full task snapshot.</summary>
public sealed record TaskChange(TaskChangeKind Kind, TaskRecord Task);
