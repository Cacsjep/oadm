using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>
/// One subscriber of the published events (<see cref="IEventSubscriptions"/>): a bounded queue the rule engine writes to
/// without ever waiting, and a loop that hands the events to the handler one at a time. A full queue drops the oldest
/// event; drops are logged at most once a minute.
/// </summary>
internal sealed partial class EventSubscription : IDisposable
{
    /// <summary>Events waiting for a slow handler before the oldest are dropped.</summary>
    public const int QueueCapacity = 10_000;

    private static readonly TimeSpan DropLogInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<PublishedEvent> _queue;
    private readonly Action<PublishedEvent> _handler;
    private readonly Action<EventSubscription> _onDispose;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _owner;
    private readonly Task _loop;
    private long _dropped;
    private long _droppedLogged;
    private long _lastDropLog = long.MinValue;
    private int _disposed;

    public EventSubscription(string owner, Action<PublishedEvent> handler, Action<EventSubscription> onDispose, TimeProvider? time = null,
        ILogger? logger = null, int capacity = QueueCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        _owner = owner;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _onDispose = onDispose ?? throw new ArgumentNullException(nameof(onDispose));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _queue = Channel.CreateBounded<PublishedEvent>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
            _ => Interlocked.Increment(ref _dropped));
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Events dropped because the handler did not keep up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Whether the subscription ended.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The loop that hands events to the handler (completes after <see cref="Dispose"/>).</summary>
    internal Task Completion => _loop;

    /// <summary>Queues an event; never waits. Called by the rule engine's loop only.</summary>
    public void Post(PublishedEvent published)
    {
        if (!IsDisposed)
        {
            _queue.Writer.TryWrite(published);
            LogDropsIfDue();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _onDispose(this);
    }

    private async Task LoopAsync()
    {
        await foreach (var published in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                _handler(published);
            }
#pragma warning disable CA1031 // A plugin's handler must never end its subscription or reach the engine.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogHandlerFailed(ex, _owner, published.TypeId);
            }
        }
    }

    private void LogDropsIfDue()
    {
        var dropped = Dropped;
        if (dropped == _droppedLogged)
        {
            return;
        }

        var now = _time.GetTimestamp();
        if (_lastDropLog != long.MinValue && _time.GetElapsedTime(_lastDropLog, now) < DropLogInterval)
        {
            return;
        }

        _lastDropLog = now;
        LogDropped(_owner, dropped - _droppedLogged);
        _droppedLogged = dropped;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The event subscription of {Owner} did not keep up: {Count} events were dropped.")]
    private partial void LogDropped(string owner, long count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The event subscription of {Owner} failed on an event of {TypeId}.")]
    private partial void LogHandlerFailed(Exception exception, string owner, string typeId);
}
