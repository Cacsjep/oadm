using Microsoft.EntityFrameworkCore;

using Oadm.Core.Tasks;

namespace Oadm.Core.Persistence;

/// <summary>
/// The task engine's <see cref="ITaskStore"/> over the Tasks, TaskDeviceResults and TaskLogEntries tables.
/// Maps the engine's immutable <see cref="Tasks.TaskRecord"/> snapshots to <see cref="TaskEntity"/> rows.
/// </summary>
public sealed class EfTaskStore(IDbContextFactory<OadmDbContext> dbFactory) : ITaskStore
{
    public async Task AddAsync(TaskRecord task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        var entity = new TaskEntity { Id = task.Id };
        Apply(task, entity);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.Tasks.AnyAsync(t => t.Id == task.Id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Task {task.Id} already exists.");
        }

        db.Tasks.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(TaskRecord task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Tasks.Include(t => t.Results).FirstOrDefaultAsync(t => t.Id == task.Id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new TaskEntity { Id = task.Id };
            db.Tasks.Add(entity);
        }

        Apply(task, entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Tasks.AsNoTracking().Include(t => t.Results)
            .FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Tasks.AsNoTracking().Include(t => t.Results)
            .OrderByDescending(t => t.CreatedUtc)
            .AsSplitQuery()
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. entities.Select(ToRecord)];
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.TaskLogEntries.Where(l => l.TaskId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.TaskDeviceResults.Where(r => r.TaskId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var rows = await db.Tasks.Where(t => t.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task AppendLogAsync(Guid taskId, IReadOnlyList<TaskLogEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.TaskLogEntries.AddRange(entries.Select(e => new TaskLogEntryEntity
        {
            TaskId = taskId,
            DeviceId = e.DeviceId,
            TimeUtc = e.TimeUtc.UtcDateTime,
            Level = e.Level,
            Message = e.Message.Length > OadmDbContext.TaskLogEntryMaxLength ? e.Message[..OadmDbContext.TaskLogEntryMaxLength] : e.Message,
        }));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskLogEntry>> GetLogAsync(Guid taskId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.TaskLogEntries.AsNoTracking()
            .Where(l => l.TaskId == taskId)
            .OrderBy(l => l.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new TaskLogEntry(ToOffset(r.TimeUtc), r.DeviceId, r.Level, r.Message))];
    }

    /// <summary>Copies a snapshot onto a (tracked) entity, syncing the device result rows.</summary>
    internal static void Apply(TaskRecord task, TaskEntity entity)
    {
        entity.PluginId = task.PluginId;
        entity.Name = task.Name;
        entity.Status = task.State;
        entity.Owner = task.Owner;
        entity.CreatedUtc = task.CreatedUtc.UtcDateTime;
        entity.StartedUtc = task.StartedUtc?.UtcDateTime;
        entity.FinishedUtc = task.FinishedUtc?.UtcDateTime;
        entity.Progress = Math.Clamp(task.Progress, 0, 100);
        // Payloads may carry secrets (passwords): they live in memory only and are never persisted.
        entity.PayloadJson = null;

        var wanted = task.Devices.DistinctBy(d => d.DeviceId).ToDictionary(d => d.DeviceId);
        entity.Results.RemoveAll(r => !wanted.ContainsKey(r.DeviceId));
        foreach (var device in wanted.Values)
        {
            var row = entity.Results.Find(r => r.DeviceId == device.DeviceId);
            if (row is null)
            {
                row = new TaskDeviceResultEntity { TaskId = entity.Id, DeviceId = device.DeviceId };
                entity.Results.Add(row);
            }

            row.Status = device.State;
            row.Message = device.Message;
            row.Progress = Math.Clamp(device.Progress, 0, 100);
        }
    }

    internal static TaskRecord ToRecord(TaskEntity entity)
    {
        return new TaskRecord(
            entity.Id,
            entity.PluginId,
            entity.Name,
            entity.Status,
            entity.Owner,
            ToOffset(entity.CreatedUtc),
            entity.StartedUtc is { } s ? ToOffset(s) : null,
            entity.FinishedUtc is { } f ? ToOffset(f) : null,
            entity.Progress,
            null,
            [.. entity.Results.OrderBy(r => r.DeviceId).Select(r => new TaskDeviceRecord(r.DeviceId, r.Status, r.Message, r.Progress))]);
    }

    private static DateTimeOffset ToOffset(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);
}
