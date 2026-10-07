using System.Threading.Channels;

namespace Oadm.Core.Collections;

/// <summary>
/// Change channel for one subscriber of a change feed, keyed by entity id. While the reader keeps up
/// (fewer than <see cref="CoalesceAbove"/> changes waiting) every change is delivered in order. Under a
/// burst a new change of an entity that still has a change waiting is merged into that waiting change
/// (it keeps its position), so a slow reader never loses the final state of an entity, memory stays
/// bounded by <see cref="CoalesceAbove"/> plus the number of distinct entities, and the writer never
/// blocks. Every change carries a full snapshot, so the latest one is all a reader needs.
/// </summary>
/// <remarks>
/// Replaces a bounded <c>DropOldest</c> channel, which silently lost changes when 5,000 devices or
/// tasks changed in one burst (e.g. "Refresh all", a Run on 5,000 devices, Delete all): a client then
/// kept stale rows until it reconnected.
/// </remarks>
public sealed class KeyedCoalescingChannel<TKey, T>
    where TKey : notnull
{
    /// <summary>Default backlog above which changes are coalesced.</summary>
    public const int DefaultCoalesceAbove = 1024;

    private readonly Lock _sync = new();
    private readonly Queue<Slot> _queue = new();
    private readonly Dictionary<TKey, Slot> _lastPending;
    private readonly Func<T, TKey> _keyOf;
    private readonly Func<T, T, T> _merge;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _waiter;
    private bool _completed;

    /// <param name="keyOf">Entity id of a change.</param>
    /// <param name="merge">Combines a waiting change (first argument) with a newer one of the same entity; default: the newer wins.</param>
    /// <param name="coalesceAbove">Backlog from which changes of the same entity are merged; 0 merges always.</param>
    public KeyedCoalescingChannel(Func<T, TKey> keyOf, Func<T, T, T>? merge = null, int coalesceAbove = DefaultCoalesceAbove, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentOutOfRangeException.ThrowIfNegative(coalesceAbove);
        _keyOf = keyOf;
        _merge = merge ?? ((_, newer) => newer);
        CoalesceAbove = coalesceAbove;
        _lastPending = new Dictionary<TKey, Slot>(comparer);
        Reader = new CoalescingReader(this);
    }

    public ChannelReader<T> Reader { get; }

    public int CoalesceAbove { get; }

    /// <summary>Changes waiting to be read.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Queues a change; false after <see cref="Complete"/>.</summary>
    public bool TryWrite(T item)
    {
        TaskCompletionSource<bool>? wake;
        lock (_sync)
        {
            if (_completed)
            {
                return false;
            }

            var key = _keyOf(item);
            if (_queue.Count >= CoalesceAbove && _lastPending.TryGetValue(key, out var waiting))
            {
                waiting.Value = _merge(waiting.Value, item);
                return true;
            }

            var slot = new Slot(key, item);
            _queue.Enqueue(slot);
            _lastPending[key] = slot;
            wake = _waiter;
            _waiter = null;
        }

        wake?.TrySetResult(true);
        return true;
    }

    /// <summary>No more writes; the reader still drains what is waiting.</summary>
    public void Complete()
    {
        TaskCompletionSource<bool>? wake;
        bool drained;
        lock (_sync)
        {
            _completed = true;
            drained = _queue.Count == 0;
            wake = _waiter;
            _waiter = null;
        }

        wake?.TrySetResult(false);
        if (drained)
        {
            _completion.TrySetResult();
        }
    }

    private bool TryRead(out T item)
    {
        bool drained = false;
        lock (_sync)
        {
            if (_queue.TryDequeue(out var slot))
            {
                if (_lastPending.TryGetValue(slot.Key, out var last) && ReferenceEquals(last, slot))
                {
                    _lastPending.Remove(slot.Key);
                }

                item = slot.Value;
                drained = _completed && _queue.Count == 0;
            }
            else
            {
                item = default!;
                return false;
            }
        }

        if (drained)
        {
            _completion.TrySetResult();
        }

        return true;
    }

    private async ValueTask<bool> WaitToReadAsync(CancellationToken ct)
    {
        while (true)
        {
            Task<bool> wait;
            lock (_sync)
            {
                if (_queue.Count > 0)
                {
                    return true;
                }

                if (_completed)
                {
                    return false;
                }

                _waiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class Slot(TKey key, T value)
    {
        public TKey Key { get; } = key;

        public T Value { get; set; } = value;
    }

    private sealed class CoalescingReader(KeyedCoalescingChannel<TKey, T> owner) : ChannelReader<T>
    {
        public override bool CanCount => true;

        public override int Count => owner.Count;

        public override Task Completion => owner._completion.Task;

        public override bool TryRead(out T item) => owner.TryRead(out item);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            owner.WaitToReadAsync(cancellationToken);
    }
}
