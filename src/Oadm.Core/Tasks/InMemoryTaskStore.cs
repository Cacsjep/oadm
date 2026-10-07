using System.Collections.Concurrent;

namespace Oadm.Core.Tasks;

/// <summary>Volatile <see cref="ITaskStore"/> for tests and for running without a database.</summary>
public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly ConcurrentDictionary<Guid, TaskRecord> _tasks = new();
    private readonly ConcurrentDictionary<Guid, List<TaskLogEntry>> _logs = new();

    public Task AddAsync(TaskRecord task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_tasks.TryAdd(task.Id, task))
        {
            throw new InvalidOperationException($"Task {task.Id} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(TaskRecord task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        _tasks[task.Id] = task;
        return Task.CompletedTask;
    }

    public Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        return Task.FromResult(_tasks.TryGetValue(id, out var task) ? task : null);
    }

    public Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<TaskRecord> list = [.. _tasks.Values.OrderByDescending(t => t.CreatedUtc)];
        return Task.FromResult(list);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        _logs.TryRemove(id, out _);
        return Task.FromResult(_tasks.TryRemove(id, out _));
    }

    public Task AppendLogAsync(Guid taskId, IReadOnlyList<TaskLogEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var log = _logs.GetOrAdd(taskId, _ => []);
        lock (log)
        {
            log.AddRange(entries);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskLogEntry>> GetLogAsync(Guid taskId, CancellationToken ct)
    {
        if (!_logs.TryGetValue(taskId, out var log))
        {
            return Task.FromResult<IReadOnlyList<TaskLogEntry>>([]);
        }

        lock (log)
        {
            return Task.FromResult<IReadOnlyList<TaskLogEntry>>([.. log]);
        }
    }
}
