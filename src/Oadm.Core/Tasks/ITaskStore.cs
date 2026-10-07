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

    // Scale (5,000 tasks per Run, 50,000 in the history). The defaults keep simple stores (tests) working;
    // EfTaskStore implements each as one query or one transaction.

    /// <summary>Adds the tasks of one Run. Default: one <see cref="AddAsync"/> per task.</summary>
    async Task AddRangeAsync(IReadOnlyList<TaskRecord> tasks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        foreach (var task in tasks)
        {
            await AddAsync(task, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One page of the history, newest first, with the total number of tasks.</summary>
    async Task<TaskPage> ListPageAsync(int offset, int limit, CancellationToken ct)
    {
        var all = await ListAsync(ct).ConfigureAwait(false);
        return new TaskPage([.. all.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit))], all.Count);
    }

    /// <summary>Tasks that are not finished (Queued or Running), newest first.</summary>
    async Task<IReadOnlyList<TaskRecord>> ListActiveAsync(CancellationToken ct) =>
        [.. (await ListAsync(ct).ConfigureAwait(false)).Where(t => !t.State.IsTerminal())];

    /// <summary>Ids of every task, newest first.</summary>
    async Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct) =>
        [.. (await ListAsync(ct).ConfigureAwait(false)).Select(t => t.Id)];

    /// <summary>
    /// Finished tasks the retention removes: finished (or, without a finish time, created) before
    /// <paramref name="finishedBefore"/>, and every finished task beyond the newest <paramref name="keepNewest"/>
    /// tasks. Null disables a rule. Active tasks are never returned.
    /// </summary>
    async Task<IReadOnlyList<Guid>> ListExpiredAsync(DateTimeOffset? finishedBefore, int? keepNewest, CancellationToken ct)
    {
        var all = await ListAsync(ct).ConfigureAwait(false);
        return [.. all.Select((t, i) => (Task: t, Index: i))
            .Where(x => x.Task.State.IsTerminal()
                && ((finishedBefore is { } before && (x.Task.FinishedUtc ?? x.Task.CreatedUtc) < before)
                    || (keepNewest is { } keep && x.Index >= keep)))
            .Select(x => x.Task.Id)];
    }

    /// <summary>
    /// Deletes many tasks with their results, steps and logs (one transaction in <c>EfTaskStore</c>).
    /// Returns the deleted tasks (steps may be omitted) for the Removed changes.
    /// </summary>
    async Task<IReadOnlyList<TaskRecord>> DeleteManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var deleted = new List<TaskRecord>(ids.Count);
        foreach (var id in ids)
        {
            var existing = await GetAsync(id, ct).ConfigureAwait(false);
            if (await DeleteAsync(id, ct).ConfigureAwait(false) && existing is not null)
            {
                deleted.Add(existing);
            }
        }

        return deleted;
    }
}

/// <summary>One page of the task history, newest first; <see cref="TotalCount"/> counts every stored task.</summary>
public sealed record TaskPage(IReadOnlyList<TaskRecord> Tasks, int TotalCount);
