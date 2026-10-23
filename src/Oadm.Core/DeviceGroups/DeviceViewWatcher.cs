using System.Collections.Concurrent;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// Ends sessions bound to one device (live video, the web interface, device event streams, I/O and metadata monitors) when
/// that device leaves the user's view (docs/spec/device-scope.md, Streams): removed, a tag or site change that takes it out
/// of the group, a changed or deleted group, a deleted tag or a removed site. A session takes a <see cref="DeviceViewLease"/>
/// and links its work to <see cref="DeviceViewLease.Left"/>. Views with all devices never end this way (today's behaviour).
/// </summary>
public sealed class DeviceViewWatcher : IDisposable
{
    private readonly IDeviceChangeFeed _feed;
    private readonly DeviceScopeCatalog _catalog;
    private readonly DeviceGroupStore _groups;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<long, DeviceViewLease>> _byDevice = new();
    private long _next;

    public DeviceViewWatcher(IDeviceChangeFeed feed, DeviceScopeCatalog catalog, DeviceGroupStore groups)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(groups);
        _feed = feed;
        _catalog = catalog;
        _groups = groups;
        _feed.Changed += OnDeviceChanged;
        _catalog.Changed += OnCatalogChanged;
        _groups.Changed += OnGroupChanged;
        _groups.Deleted += OnGroupDeleted;
    }

    /// <summary>The leases open now (tests).</summary>
    public int Count => _byDevice.Values.Sum(d => d.Count);

    /// <summary>
    /// A lease that ends (<see cref="DeviceViewLease.Left"/>) when the one device of <paramref name="one"/> leaves the view it
    /// came from. Registered first, then checked again: a device that is no longer in the view throws
    /// <see cref="DevicesNotAvailableException"/>, so no change between the session's check and the lease is missed. A view
    /// with all devices gets a lease that never ends.
    /// </summary>
    public async Task<DeviceViewLease> WatchAsync(ViewedDevices one, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(one);
        if (one.Count != 1)
        {
            throw new ArgumentException("Exactly one device is needed.", nameof(one));
        }

        var view = one.View;
        var deviceId = one[0].Id;
        if (view.HasAllDevices)
        {
            return DeviceViewLease.Never;
        }

        var lease = new DeviceViewLease(this, Interlocked.Increment(ref _next), view, deviceId);
        _byDevice.GetOrAdd(deviceId, _ => new ConcurrentDictionary<long, DeviceViewLease>())[lease.Key] = lease;
        try
        {
            if (await view.FindAsync(deviceId, ct).ConfigureAwait(false) is null)
            {
                throw new DevicesNotAvailableException(1);
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        return lease;
    }

    public void Dispose()
    {
        _feed.Changed -= OnDeviceChanged;
        _catalog.Changed -= OnCatalogChanged;
        _groups.Changed -= OnGroupChanged;
        _groups.Deleted -= OnGroupDeleted;
    }

    internal void Release(DeviceViewLease lease)
    {
        if (_byDevice.TryGetValue(lease.DeviceId, out var leases))
        {
            leases.TryRemove(lease.Key, out _);
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChange change)
    {
        if (!_byDevice.TryGetValue(change.DeviceId, out var leases) || leases.IsEmpty)
        {
            return;
        }

        foreach (var lease in leases.Values)
        {
            if (change.Kind == DeviceChangeKind.Removed || change.Device is null || !lease.View.Contains(change.Device))
            {
                End(lease);
            }
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => CheckAll(_ => true);

    private void OnGroupChanged(object? sender, Guid groupId) => CheckAll(l => l.View.GroupId == groupId);

    private void OnGroupDeleted(object? sender, DeviceGroupDeleteResult e) => CheckAll(l => l.View.GroupId == e.Id);

    /// <summary>Checks the open leases again (outside the raising thread: it reads the device rows).</summary>
    private void CheckAll(Func<DeviceViewLease, bool> affected)
    {
        var leases = _byDevice.Values.SelectMany(d => d.Values).Where(affected).ToList();
        if (leases.Count == 0)
        {
            return;
        }

        _ = CallerContext.RunDetached(async () =>
        {
            foreach (var lease in leases)
            {
                try
                {
                    if (await lease.View.FindAsync(lease.DeviceId, CancellationToken.None).ConfigureAwait(false) is null)
                    {
                        End(lease);
                    }
                }
#pragma warning disable CA1031 // A failed read ends the session: fail closed.
                catch (Exception)
#pragma warning restore CA1031
                {
                    End(lease);
                }
            }
        });
    }

    private void End(DeviceViewLease lease)
    {
        Release(lease);
        lease.End();
    }
}

/// <summary>
/// A session's hold on one device of a view (<see cref="DeviceViewWatcher.WatchAsync"/>): <see cref="Left"/> is cancelled when
/// the device leaves the view. Dispose it when the session ends.
/// </summary>
public sealed class DeviceViewLease : IDisposable
{
    private readonly DeviceViewWatcher? _owner;
    private readonly CancellationTokenSource? _left;
    private int _disposed;

    private DeviceViewLease()
    {
    }

    internal DeviceViewLease(DeviceViewWatcher owner, long key, DeviceView view, Guid deviceId)
    {
        _owner = owner;
        _left = new CancellationTokenSource();
        Key = key;
        View = view;
        DeviceId = deviceId;
    }

    /// <summary>The lease of a view with all devices: it never ends.</summary>
    internal static DeviceViewLease Never { get; } = new();

    internal long Key { get; }

    internal DeviceView View { get; } = null!;

    /// <summary>The device the session is bound to.</summary>
    public Guid DeviceId { get; }

    /// <summary>Cancelled when the device left the view (never for a view with all devices).</summary>
    public CancellationToken Left => _left?.Token ?? CancellationToken.None;

    /// <summary>True once the device left the view.</summary>
    public bool HasLeft => _left?.IsCancellationRequested ?? false;

    internal void End()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            // The session's callbacks run on the thread pool, never on the thread that published the change.
            _ = Task.Run(() =>
            {
                try
                {
                    _left!.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The session ended meanwhile.
                }
            }, CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_owner is null || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.Release(this);
        _left!.Dispose();
    }
}
