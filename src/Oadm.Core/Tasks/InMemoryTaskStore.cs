using System.Collections.Concurrent;

namespace Oadm.Core.Tasks;

/// <summary>Volatile <see cref="ITaskStore"/> for tests and for running without a database.</summary>
public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly ConcurrentDictionary<Guid, TaskRecord> _tasks = new();

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
        return Task.FromResult(_tasks.TryRemove(id, out _));
    }
}
