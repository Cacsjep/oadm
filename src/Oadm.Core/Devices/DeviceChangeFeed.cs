using System.Collections.Concurrent;
using System.Threading.Channels;

using Oadm.Core.Collections;

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

/// <summary>
/// A per-subscriber channel of changes, in order while the reader keeps up; under a burst (more than
/// 1,024 waiting) coalesced per device: a change of a device still waiting to be
/// read replaces the waiting one (an Added stays Added), so a slow subscriber never blocks the publisher,
/// never loses the final state of a device and buffers at most one change per device.
/// </summary>
public sealed class DeviceChangeSubscription : IDisposable
{
    private readonly KeyedCoalescingChannel<Guid, DeviceChange> _channel;
    private readonly Action<DeviceChangeSubscription> _onDispose;
    private int _disposed;

    internal DeviceChangeSubscription(Action<DeviceChangeSubscription> onDispose)
    {
        _channel = new KeyedCoalescingChannel<Guid, DeviceChange>(c => c.DeviceId, Merge);
        _onDispose = onDispose;
    }

    public ChannelReader<DeviceChange> Reader => _channel.Reader;

    internal void Write(DeviceChange change) => _channel.TryWrite(change);

    /// <summary>An Added not read yet stays Added with the newer data; otherwise the newer change wins.</summary>
    internal static DeviceChange Merge(DeviceChange waiting, DeviceChange newer) =>
        waiting.Kind == DeviceChangeKind.Added && newer.Kind == DeviceChangeKind.Updated
            ? newer with { Kind = DeviceChangeKind.Added }
            : newer;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Complete();
            _onDispose(this);
        }
    }
}

public sealed class DeviceChangeFeed : IDeviceChangeFeed
{
    private readonly ConcurrentDictionary<DeviceChangeSubscription, byte> _subscribers = new();
    private long _version;

    public event EventHandler<DeviceChange>? Changed;

    /// <summary>Incremented by every published change; caches derived from the device table compare it.</summary>
    public long Version => Interlocked.Read(ref _version);

    public DeviceChangeSubscription Subscribe()
    {
        var subscription = new DeviceChangeSubscription(s => _subscribers.TryRemove(s, out _));
        _subscribers.TryAdd(subscription, 0);
        return subscription;
    }

    public void Publish(DeviceChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Interlocked.Increment(ref _version);
        foreach (var subscriber in _subscribers.Keys)
        {
            subscriber.Write(change);
        }

        Changed?.Invoke(this, change);
    }
}
