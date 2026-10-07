using Microsoft.EntityFrameworkCore;

namespace Oadm.Core.Persistence;

/// <summary>Persistence of tasks and their per-device results. All returned objects are detached.</summary>
public interface ITaskRecordStore
{
    /// <summary>Inserts a task with its <see cref="TaskRecord.Results"/>. Sets CreatedUtc when default.</summary>
    Task<TaskRecord> CreateAsync(TaskRecord task, CancellationToken ct);

    Task<TaskRecord?> GetAsync(Guid taskId, CancellationToken ct);

    /// <summary>All tasks with results, newest first.</summary>
    Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Sets the task status. Running stamps StartedUtc (once); a terminal status stamps FinishedUtc
    /// and, for Done, forces Progress to 100. Returns the updated task or null if not found.
    /// </summary>
    Task<TaskRecord?> UpdateStatusAsync(Guid taskId, TaskRecordStatus status, CancellationToken ct);

    /// <summary>Sets overall progress (clamped 0-100). Returns false if the task does not exist.</summary>
    Task<bool> UpdateProgressAsync(Guid taskId, int progress, CancellationToken ct);

    /// <summary>Inserts or updates the result row of one device. Progress is clamped 0-100.</summary>
    Task<bool> UpdateDeviceResultAsync(Guid taskId, Guid deviceId, TaskRecordStatus status, int progress, string? message, CancellationToken ct);

    /// <summary>Deletes the task and its results. Returns false if it did not exist.</summary>
    Task<bool> DeleteAsync(Guid taskId, CancellationToken ct);
}

public sealed class TaskRecordStore(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider timeProvider) : ITaskRecordStore
{
    public async Task<TaskRecord> CreateAsync(TaskRecord task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Id == Guid.Empty)
        {
            task.Id = Guid.NewGuid();
        }

        if (task.CreatedUtc == default)
        {
            task.CreatedUtc = timeProvider.GetUtcNow().UtcDateTime;
        }

        task.Progress = Math.Clamp(task.Progress, 0, 100);
        foreach (var result in task.Results)
        {
            result.TaskId = task.Id;
            result.Progress = Math.Clamp(result.Progress, 0, 100);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return (await GetAsync(task.Id, ct).ConfigureAwait(false))!;
    }

    public async Task<TaskRecord?> GetAsync(Guid taskId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Tasks.AsNoTracking().Include(t => t.Results)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Tasks.AsNoTracking().Include(t => t.Results)
            .OrderByDescending(t => t.CreatedUtc)
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<TaskRecord?> UpdateStatusAsync(Guid taskId, TaskRecordStatus status, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var task = await db.Tasks.Include(t => t.Results).FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        task.Status = status;
        if (status == TaskRecordStatus.Running)
        {
            task.StartedUtc ??= now;
        }

        if (TaskRecord.IsTerminal(status))
        {
            task.FinishedUtc ??= now;
            if (status == TaskRecordStatus.Done)
            {
                task.Progress = 100;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return task;
    }

    public async Task<bool> UpdateProgressAsync(Guid taskId, int progress, CancellationToken ct)
    {
        var clamped = Math.Clamp(progress, 0, 100);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Progress, clamped), ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> UpdateDeviceResultAsync(
        Guid taskId, Guid deviceId, TaskRecordStatus status, int progress, string? message, CancellationToken ct)
    {
        var clamped = Math.Clamp(progress, 0, 100);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Tasks.AnyAsync(t => t.Id == taskId, ct).ConfigureAwait(false))
        {
            return false;
        }

        var row = await db.TaskDeviceResults.FirstOrDefaultAsync(r => r.TaskId == taskId && r.DeviceId == deviceId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new TaskDeviceResult { TaskId = taskId, DeviceId = deviceId };
            db.TaskDeviceResults.Add(row);
        }

        row.Status = status;
        row.Progress = clamped;
        row.Message = message;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid taskId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.TaskDeviceResults.Where(r => r.TaskId == taskId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var rows = await db.Tasks.Where(t => t.Id == taskId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }
}
