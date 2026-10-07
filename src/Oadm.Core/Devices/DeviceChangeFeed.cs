using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Oadm.Core.Devices;

public enum DeviceChangeKind
{
    Added = 1,
    Updated = 2,
    Removed = 3,
}

/// <summary>One change of the device table. <see cref="Device"/> is a detached snapshot, null for <see cref="DeviceChangeKind.Removed"/>.</summary>
public sealed record DeviceChange(DeviceChangeKind Kind, Guid DeviceId, Device? Device);

/// <summary>
/// Live feed of device table changes, consumed by the gRPC DeviceService.Watch stream.
/// Subscribe first, then take the snapshot, then stream the reader, so no change is lost.
/// </summary>
public interface IDeviceChangeFeed
{
    /// <summary>Plain .NET event, raised synchronously on the publishing thread.</summary>
    event EventHandler<DeviceChange>? Changed;

    /// <summary>Creates a subscription that is active immediately. Dispose it to unsubscribe.</summary>
    DeviceChangeSubscription Subscribe();

    void Publish(DeviceChange change);
}

/// <summary>A per-subscriber buffered channel of changes.</summary>
public sealed class DeviceChangeSubscription : IDisposable
{
    private readonly Channel<DeviceChange> _channel;
    private readonly Action<DeviceChangeSubscription> _onDispose;
    private int _disposed;

    internal DeviceChangeSubscription(int capacity, Action<DeviceChangeSubscription> onDispose)
    {
        // A stuck subscriber must never block the publisher or grow memory unbounded:
        // when the buffer is full the oldest change is dropped.
        _channel = Channel.CreateBounded<DeviceChange>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _onDispose = onDispose;
    }

    public ChannelReader<DeviceChange> Reader => _channel.Reader;

    internal void Write(DeviceChange change) => _channel.Writer.TryWrite(change);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
            _onDispose(this);
        }
    }
}

public sealed class DeviceChangeFeed : IDeviceChangeFeed
{
    /// <summary>Per-subscriber buffer size.</summary>
    public const int SubscriberCapacity = 4096;

    private readonly ConcurrentDictionary<DeviceChangeSubscription, byte> _subscribers = new();

    public event EventHandler<DeviceChange>? Changed;

    public DeviceChangeSubscription Subscribe()
    {
        var subscription = new DeviceChangeSubscription(SubscriberCapacity, s => _subscribers.TryRemove(s, out _));
        _subscribers.TryAdd(subscription, 0);
        return subscription;
    }

    public void Publish(DeviceChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        foreach (var subscriber in _subscribers.Keys)
        {
            subscriber.Write(change);
        }

        Changed?.Invoke(this, change);
    }
}
