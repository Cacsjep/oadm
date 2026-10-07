using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace Oadm.Core.Tasks;

/// <summary>
/// Fan-out of task changes to any number of subscribers (gRPC Watch streams). Each subscriber
/// gets its own bounded channel; a slow subscriber loses its oldest buffered changes, never
/// blocks the engine. Since every change carries a full snapshot, the latest one wins.
/// </summary>
public sealed partial class TaskChangeFeed
{
    private readonly Lock _sync = new();
    private readonly List<Subscription> _subscribers = [];
    private readonly int _capacity;
    private readonly ILogger? _logger;
    private bool _completed;

    public TaskChangeFeed(int capacity = 4096, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _logger = logger;
    }

    /// <summary>Raised synchronously for every change. Handlers must be fast and must not throw.</summary>
    public event EventHandler<TaskChange>? Changed;

    /// <summary>Starts buffering changes. Dispose the subscription to stop.</summary>
    public TaskChangeSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<TaskChange>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var subscription = new Subscription(this, channel);
        lock (_sync)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
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
                subscriber.Channel.Writer.TryWrite(change);
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
                subscriber.Channel.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Task change handler threw")]
    private static partial void LogHandlerFailed(ILogger logger, Exception ex);

    private void Remove(Subscription subscription)
    {
        lock (_sync)
        {
            _subscribers.Remove(subscription);
        }

        subscription.Channel.Writer.TryComplete();
    }

    private sealed class Subscription(TaskChangeFeed owner, Channel<TaskChange> channel) : TaskChangeSubscription
    {
        public Channel<TaskChange> Channel { get; } = channel;

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
