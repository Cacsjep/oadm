namespace Oadm.Core.Tasks;

/// <summary>
/// The running slots of one task plugin: a FIFO gate like a semaphore whose limit is read live
/// (the server setting <c>Tasks.MaxParallelPerPlugin</c>, capped by the plugin's MaxParallelDevices)
/// each time a waiting task could start. A lowered limit lets running tasks finish and starts no new
/// ones until fewer run; a raised limit starts waiting tasks on the next <see cref="Pump"/>, release
/// or wait.
/// </summary>
internal sealed class PluginSlots(Func<int> limit)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = new();
    private int _running;

    /// <summary>Tasks holding a slot now.</summary>
    public int Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>Tasks waiting for a slot.</summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>Completes when the caller holds a slot; it must call <see cref="Release"/> once afterwards.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_waiting.Count == 0 && _running < CurrentLimit())
            {
                _running++;
                return Task.CompletedTask;
            }

            node = _waiting.AddLast(waiter);
        }

        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(() =>
            {
                lock (_lock)
                {
                    if (node.List is null)
                    {
                        return; // already granted a slot
                    }

                    _waiting.Remove(node);
                }

                waiter.TrySetCanceled(ct);
            });
            _ = waiter.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return waiter.Task;
    }

    /// <summary>Frees a slot and starts the next waiting tasks that fit under the current limit.</summary>
    public void Release()
    {
        lock (_lock)
        {
            _running--;
        }

        Pump();
    }

    /// <summary>Starts waiting tasks while fewer than the current limit run.</summary>
    public void Pump()
    {
        List<TaskCompletionSource>? granted = null;
        lock (_lock)
        {
            if (_waiting.Count == 0)
            {
                return;
            }

            var max = CurrentLimit();
            while (_waiting.First is { } first && _running < max)
            {
                _waiting.RemoveFirst();
                _running++;
                (granted ??= []).Add(first.Value);
            }
        }

        if (granted is null)
        {
            return;
        }

        foreach (var waiter in granted)
        {
            waiter.TrySetResult();
        }
    }

    private int CurrentLimit() => Math.Max(1, limit());
}
