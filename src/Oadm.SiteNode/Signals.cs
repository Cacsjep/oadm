namespace Oadm.SiteNode;

/// <summary>
/// Raises the node's change events. A handler that throws never reaches the code that raised the event (a dial, a
/// config write, a card change) and never stops the other handlers: the raiser's state is already consistent when it
/// raises, and one bad listener must not break it.
/// </summary>
public static class NodeEvents
{
    public static void Raise(Action? handlers)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
#pragma warning disable CA1031 // a listener's failure is its own problem, never the raiser's
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}

/// <summary>
/// A wake-up for one waiting loop: <see cref="Set"/> may be called from any thread at any time, also after the loop
/// ended (nothing to dispose, so a late event handler never meets a disposed object). Several sets before a wait count
/// as one.
/// </summary>
public sealed class Wakeup
{
    private readonly Lock _lock = new();
    private TaskCompletionSource? _waiter;
    private bool _set;

    /// <summary>Wakes the waiting loop, or the next wait when none waits.</summary>
    public void Set()
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            _set = true;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }

    /// <summary>
    /// Returns once <see cref="Set"/> was called since the last wait (or drain). A cancelled wait consumes nothing: the
    /// next wait still sees the set.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_set)
                {
                    _set = false;
                    return;
                }

                _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = _waiter.Task;
            }

            await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Forgets a set that nobody waited for yet; true when there was one.</summary>
    public bool Drain()
    {
        lock (_lock)
        {
            var was = _set;
            _set = false;
            return was;
        }
    }
}
