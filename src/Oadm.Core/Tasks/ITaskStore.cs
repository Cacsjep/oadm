namespace Oadm.Core.Tasks;

/// <summary>
/// Persistence for tasks and their per-device results. The engine writes full snapshots:
/// <see cref="AddAsync"/> once when a task is created, then <see cref="UpdateAsync"/> on every
/// state transition (task or device) and, throttled to one write per second per device, when a
/// progress report carries a new message. Other progress reports are only published on the change
/// feed; the final snapshot always carries the final progress. Snapshots never carry the payload.
/// </summary>
public interface ITaskStore
{
    Task AddAsync(TaskRecord task, CancellationToken ct);

    /// <summary>Replaces the stored task, including its device results, with the given snapshot.</summary>
    Task UpdateAsync(TaskRecord task, CancellationToken ct);

    Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>All tasks, newest first.</summary>
    Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct);

    /// <summary>Deletes the task, its device results and its log. Returns false when it did not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Appends entries to the task's log, in order. The engine caps a task at <see cref="TaskEngineOptions.MaxLogEntriesPerTask"/>.</summary>
    Task AppendLogAsync(Guid taskId, IReadOnlyList<TaskLogEntry> entries, CancellationToken ct);

    /// <summary>The task's log, oldest first; empty for an unknown task.</summary>
    Task<IReadOnlyList<TaskLogEntry>> GetLogAsync(Guid taskId, CancellationToken ct);
}
