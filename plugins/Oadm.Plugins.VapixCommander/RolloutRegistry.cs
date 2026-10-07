using System.Collections.Concurrent;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// Coordinates the tasks of one rollout (one task per device) for "Stop on first error": the first failing
/// device aborts the rollout, tasks that have not started yet are cancelled, running ones stop after their
/// current command. State lives in server memory and is dropped when every task finished (or after a day).
/// </summary>
internal sealed class RolloutRegistry(TimeProvider? time = null)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    private readonly ConcurrentDictionary<Guid, Rollout> _rollouts = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public int Count => _rollouts.Count;

    /// <summary>The rollout with this id, created on first use (a task started without the page knows only its payload).</summary>
    public Rollout Get(Guid rolloutId, bool stopOnFirstError)
    {
        Sweep();
        return _rollouts.GetOrAdd(rolloutId, id => new Rollout(this, id, stopOnFirstError, _time.GetUtcNow()));
    }

    public bool TryGet(Guid rolloutId, out Rollout? rollout) => _rollouts.TryGetValue(rolloutId, out rollout);

    private void Remove(Guid rolloutId) => _rollouts.TryRemove(rolloutId, out _);

    private void Sweep()
    {
        var limit = _time.GetUtcNow() - MaxAge;
        foreach (var (id, rollout) in _rollouts)
        {
            if (rollout.CreatedUtc < limit)
            {
                _rollouts.TryRemove(id, out _);
            }
        }
    }

    internal sealed class Rollout(RolloutRegistry owner, Guid id, bool stopOnFirstError, DateTimeOffset createdUtc)
    {
        private readonly Lock _sync = new();
        private readonly HashSet<Guid> _started = [];
        private readonly HashSet<Guid> _finished = [];
        private List<Guid>? _taskIds;

        public Guid Id { get; } = id;

        public bool StopOnFirstError { get; } = stopOnFirstError;

        public DateTimeOffset CreatedUtc { get; } = createdUtc;

        public string? AbortReason { get; private set; }

        public bool IsAborted
        {
            get
            {
                lock (_sync)
                {
                    return AbortReason is not null;
                }
            }
        }

        /// <summary>A task starts working. False when the rollout was already stopped: the task must not touch its device.</summary>
        public bool Join(Guid taskId)
        {
            lock (_sync)
            {
                _started.Add(taskId);
                return AbortReason is null;
            }
        }

        /// <summary>
        /// Stops the rollout (first caller wins). Returns the tasks to cancel: known tasks that have not started.
        /// Tasks registered later through <see cref="SetTaskIds"/> are returned there.
        /// </summary>
        public IReadOnlyList<Guid> Abort(string reason)
        {
            lock (_sync)
            {
                if (AbortReason is not null)
                {
                    return [];
                }

                AbortReason = reason;
                return _taskIds is null ? [] : CancelNotStarted();
            }
        }

        /// <summary>The task ids of the rollout (after they were created). Returns the ones to cancel when it was already stopped.</summary>
        public IReadOnlyList<Guid> SetTaskIds(IReadOnlyList<Guid> taskIds)
        {
            lock (_sync)
            {
                _taskIds = [.. taskIds];
                IReadOnlyList<Guid> cancel = AbortReason is null ? [] : CancelNotStarted();
                RemoveWhenDone();
                return cancel;
            }
        }

        public void Finish(Guid taskId)
        {
            lock (_sync)
            {
                _finished.Add(taskId);
                RemoveWhenDone();
            }
        }

        /// <summary>Not started tasks are cancelled by the caller and never run, so they count as finished.</summary>
        private List<Guid> CancelNotStarted()
        {
            var cancel = _taskIds!.Where(t => !_started.Contains(t)).ToList();
            _finished.UnionWith(cancel);
            RemoveWhenDone();
            return cancel;
        }

        private void RemoveWhenDone()
        {
            if (_taskIds is not null && _taskIds.All(_finished.Contains))
            {
                owner.Remove(Id);
            }
        }
    }
}
