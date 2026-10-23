using System.Collections.Concurrent;
using System.Threading.Channels;

using Oadm.Core.Collections;
using Oadm.Core.Sites;

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

    /// <summary>Publishes several changes as one batch (a site going offline or online: one change per device of it).</summary>
    void PublishMany(IReadOnlyList<DeviceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            Publish(change);
        }
    }

    /// <summary>
    /// Incremented by every single change (also within a batch); read it before reading devices that
    /// <see cref="PublishUnlessNewer"/> publishes later. 0 for feeds without it.
    /// </summary>
    long Sequence => 0;

    /// <summary>
    /// Publishes <paramref name="changes"/> as one batch, except the devices that had a change published after
    /// <paramref name="since"/> (a <see cref="Sequence"/> read before the devices were read): a copy read before a newer
    /// change never overwrites it. Returns how many were published.
    /// </summary>
    int PublishUnlessNewer(IReadOnlyList<DeviceChange> changes, long since)
    {
        ArgumentNullException.ThrowIfNull(changes);
        PublishMany(changes);
        return changes.Count;
    }
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

/// <summary>
/// The feed of the device table. Every published device shows its computed status: <see cref="Sdk.Devices.DeviceStatus.SiteOffline"/>
/// while its site is not online (<see cref="SiteDeviceStatus"/>), with its site's name.
/// </summary>
public sealed class DeviceChangeFeed(ISiteStates? sites = null) : IDeviceChangeFeed
{
    private readonly ConcurrentDictionary<DeviceChangeSubscription, byte> _subscribers = new();
    private readonly ISiteStates _sites = sites ?? NoSiteStates.Instance;

    // Stamping and the writes to the subscribers happen under one lock, so the subscribers get the changes of a device in
    // the order they were stamped and PublishUnlessNewer can compare. The Changed event runs outside it.
    private readonly Lock _publishing = new();
    private readonly Dictionary<Guid, long> _lastPublished = [];

    // A removed device keeps its stamp for a while (a stale copy read before the removal must not bring it back), but
    // never for good: at most TombstoneLimit removals are remembered, the oldest dropped first.
    private readonly Dictionary<Guid, long> _removed = [];
    private readonly Queue<(Guid Id, long Stamp)> _removedOrder = new();
    private long _version;
    private long _sequence;

    // What decides a device's membership in device groups, per device as last published: an update that changes none of it
    // (a status, a firmware version) leaves MembershipVersion as it is.
    private readonly Dictionary<Guid, MembershipFields> _membership = [];
    private long _membershipVersion;

    public event EventHandler<DeviceChange>? Changed;

    /// <summary>Incremented by every published change; caches derived from the device table compare it.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Incremented when a device is added or removed, or an update changes what device groups decide on (serial, site,
    /// tags, who added it); also for the first update of a device the feed has not seen yet. The allowed ids of the views
    /// are cached per this version, so a status change never makes them read again (docs/spec/device-scope.md, Sessions).
    /// </summary>
    public long MembershipVersion => Interlocked.Read(ref _membershipVersion);

    /// <inheritdoc />
    public long Sequence => Interlocked.Read(ref _sequence);

    public DeviceChangeSubscription Subscribe()
    {
        var subscription = new DeviceChangeSubscription(s => _subscribers.TryRemove(s, out _));
        _subscribers.TryAdd(subscription, 0);
        return subscription;
    }

    public void Publish(DeviceChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var delivered = WithSiteStatus(change);
        lock (_publishing)
        {
            Interlocked.Increment(ref _version);
            Stamp(delivered);
            if (MembershipMoved(delivered))
            {
                Interlocked.Increment(ref _membershipVersion);
            }

            Write(delivered);
        }

        Auth.CallerContext.Raise(Changed, this, delivered);
    }

    /// <summary>One batch: the version moves once, every subscriber gets the changes in order.</summary>
    public void PublishMany(IReadOnlyList<DeviceChange> changes) => PublishUnlessNewer(changes, long.MaxValue);

    /// <inheritdoc />
    public int PublishUnlessNewer(IReadOnlyList<DeviceChange> changes, long since)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return 0;
        }

        var prepared = changes.Select(WithSiteStatus).ToList();
        var delivered = new List<DeviceChange>(prepared.Count);
        var membership = false;
        lock (_publishing)
        {
            foreach (var change in prepared)
            {
                if ((_lastPublished.TryGetValue(change.DeviceId, out var last) || _removed.TryGetValue(change.DeviceId, out last)) && last > since)
                {
                    continue; // a newer change of this device went out after the caller read it
                }

                Stamp(change);
                membership |= MembershipMoved(change);
                Write(change);
                delivered.Add(change);
            }

            if (delivered.Count > 0)
            {
                Interlocked.Increment(ref _version);
            }

            if (membership)
            {
                Interlocked.Increment(ref _membershipVersion);
            }
        }

        // The background boundary: the handlers (event rules, compliance, sites) never see the user whose call published.
        using (Auth.CallerContext.Suppress())
        {
            foreach (var change in delivered)
            {
                Changed?.Invoke(this, change);
            }
        }

        return delivered.Count;
    }

    /// <summary>How many removed devices keep their stamp (default: the device limit, one whole table).</summary>
    internal int TombstoneLimit { get; init; } = Sdk.Devices.DeviceLimits.MaxDevices;

    /// <summary>Devices with a stamp now, removed ones included (tests).</summary>
    internal int StampCount
    {
        get
        {
            lock (_publishing)
            {
                return _lastPublished.Count + _removed.Count;
            }
        }
    }

    private void Stamp(DeviceChange change)
    {
        var stamp = Interlocked.Increment(ref _sequence);
        if (change.Kind != DeviceChangeKind.Removed)
        {
            _lastPublished[change.DeviceId] = stamp;
            _removed.Remove(change.DeviceId);
            return;
        }

        _lastPublished.Remove(change.DeviceId);
        _removed[change.DeviceId] = stamp;
        _removedOrder.Enqueue((change.DeviceId, stamp));
        while (_removedOrder.Count > TombstoneLimit)
        {
            var (id, old) = _removedOrder.Dequeue();
            if (_removed.TryGetValue(id, out var current) && current == old)
            {
                _removed.Remove(id);
            }
        }
    }

    /// <summary>True when the change can move the device into or out of a group (called under the publishing lock).</summary>
    private bool MembershipMoved(DeviceChange change)
    {
        if (change.Kind == DeviceChangeKind.Removed || change.Device is not { } device)
        {
            _membership.Remove(change.DeviceId);
            return true;
        }

        var fields = MembershipFields.Of(device);
        if (change.Kind == DeviceChangeKind.Updated && _membership.TryGetValue(change.DeviceId, out var known) && known == fields)
        {
            return false;
        }

        _membership[change.DeviceId] = fields;
        return true;
    }

    private readonly record struct MembershipFields(string? Serial, Guid? SiteId, string Tags, bool AddedByAllDevices, Guid? AddedByGroupId)
    {
        public static MembershipFields Of(Device device) =>
            new(device.Serial, device.SiteId, string.Join('\u0000', device.Tags ?? []), device.AddedByAllDevices, device.AddedByGroupId);
    }

    private void Write(DeviceChange change)
    {
        foreach (var subscriber in _subscribers.Keys)
        {
            subscriber.Write(change);
        }
    }

    /// <summary>A device at a site gets its site's name and, while the site is not online, Site offline (on a copy).</summary>
    private DeviceChange WithSiteStatus(DeviceChange change)
    {
        if (change.Device is not { SiteId: not null } device)
        {
            return change;
        }

        var copy = device.Clone();
        SiteDeviceStatus.Apply(copy, _sites);
        return change with { Device = copy };
    }
}
