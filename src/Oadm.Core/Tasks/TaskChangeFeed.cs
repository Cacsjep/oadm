using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Oadm.Core.Collections;

namespace Oadm.Core.Tasks;

/// <summary>
/// Fan-out of task changes to any number of subscribers (gRPC Watch streams). Each subscriber
/// gets its own channel; a slow subscriber never blocks the engine. Since every change carries a
/// full snapshot, the latest one wins: under a burst (more than 1,024 waiting) changes are coalesced
/// per task (a waiting change of the same
/// task is replaced, an Added stays Added), so a burst of 5,000 tasks (one Run, Delete all, retention)
/// never drops the final state of a task and a subscriber buffers at most one change per task.
/// </summary>
public sealed partial class TaskChangeFeed
{
    private readonly Lock _sync = new();
    private readonly List<Subscription> _subscribers = [];
    private readonly ILogger? _logger;
    private bool _completed;

    /// <param name="capacity">Kept for source compatibility (must be at least 1); subscriptions coalesce instead of dropping.</param>
    public TaskChangeFeed(int capacity = 4096, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _logger = logger;
    }

    /// <summary>Raised synchronously for every change. Handlers must be fast and must not throw.</summary>
    public event EventHandler<TaskChange>? Changed;

    /// <summary>Starts buffering changes. Dispose the subscription to stop.</summary>
    public TaskChangeSubscription Subscribe()
    {
        var channel = new KeyedCoalescingChannel<Guid, TaskChange>(c => c.Task.Id, Merge);
        var subscription = new Subscription(this, channel);
        lock (_sync)
        {
            if (_completed)
            {
                channel.Complete();
            }
            else
            {
                _subscribers.Add(subscription);
            }
        }

        return subscription;
    }

    public void Publish(TaskChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_sync)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Channel.TryWrite(change);
            }
        }

        try
        {
            Changed?.Invoke(this, change);
        }
#pragma warning disable CA1031 // A faulty subscriber must not break task execution.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (_logger is not null)
            {
                LogHandlerFailed(_logger, ex);
            }
        }
    }

    /// <summary>Completes all subscriber channels; later subscriptions complete immediately.</summary>
    public void Complete()
    {
        lock (_sync)
        {
            _completed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Channel.Complete();
            }

            _subscribers.Clear();
        }
    }

    /// <summary>An Added not read yet stays Added with the newer snapshot; otherwise the newer change wins.</summary>
    internal static TaskChange Merge(TaskChange waiting, TaskChange newer) =>
        waiting.Kind == TaskChangeKind.Added && newer.Kind == TaskChangeKind.Updated
            ? newer with { Kind = TaskChangeKind.Added }
            : newer;

    [LoggerMessage(Level = LogLevel.Error, Message = "Task change handler threw")]
    private static partial void LogHandlerFailed(ILogger logger, Exception ex);

    private void Remove(Subscription subscription)
    {
        lock (_sync)
        {
            _subscribers.Remove(subscription);
        }

        subscription.Channel.Complete();
    }

    private sealed class Subscription(TaskChangeFeed owner, KeyedCoalescingChannel<Guid, TaskChange> channel) : TaskChangeSubscription
    {
        public KeyedCoalescingChannel<Guid, TaskChange> Channel { get; } = channel;

        public override ChannelReader<TaskChange> Reader => Channel.Reader;

        public override void Dispose() => owner.Remove(this);
    }
}

/// <summary>A live subscription to the task change feed.</summary>
public abstract class TaskChangeSubscription : IDisposable
{
    public abstract ChannelReader<TaskChange> Reader { get; }

    public abstract void Dispose();
}
