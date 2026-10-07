namespace Oadm.Core.Tasks;

/// <summary>
/// Persistence for tasks and their per-device results. The engine writes full snapshots:
/// <see cref="AddAsync"/> once when a task is created, then <see cref="UpdateAsync"/> on every
/// state transition (task or device). Pure progress reports are not persisted, only published
/// on the change feed; the final snapshot always carries the final progress.
/// </summary>
public interface ITaskStore
{
    Task AddAsync(TaskRecord task, CancellationToken ct);

    /// <summary>Replaces the stored task, including its device results, with the given snapshot.</summary>
    Task UpdateAsync(TaskRecord task, CancellationToken ct);

    Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>All tasks, newest first.</summary>
    Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct);

    /// <summary>Deletes the task and its device results. Returns false when it did not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
