using System.Collections.Concurrent;

namespace Oadm.Client.Infrastructure;

/// <summary>
/// Coalescing hand-off from a background stream to the UI thread: items are queued without blocking and
/// at most one drain is waiting on the dispatcher at any time; the drain applies everything queued so far
/// as one batch, in order. Scale: 5,000 device or task changes arriving within a second become a few UI
/// updates instead of 5,000 dispatcher posts, each with its own grid, filter and summary work.
/// </summary>
public sealed class ChangeBatcher<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly IUiDispatcher _ui;
    private readonly Action<IReadOnlyList<T>> _apply;
    private int _scheduled;

    public ChangeBatcher(IUiDispatcher ui, Action<IReadOnlyList<T>> apply)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(apply);
        _ui = ui;
        _apply = apply;
    }

    /// <summary>Number of drains that applied at least one item (diagnostics and tests).</summary>
    public int Batches { get; private set; }

    /// <summary>Queues an item; any thread.</summary>
    public void Add(T item)
    {
        _queue.Enqueue(item);
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
        {
            _ui.Post(Drain);
        }
    }

    private void Drain()
    {
        // Cleared first: an item queued while this drain runs schedules the next drain.
        Volatile.Write(ref _scheduled, 0);
        var batch = new List<T>();
        while (_queue.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count > 0)
        {
            Batches++;
            _apply(batch);
        }
    }
}
