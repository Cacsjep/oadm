using Oadm.Core.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The device feed of one view (docs/spec/device-scope.md, Streams), made by <see cref="DeviceView.Subscribe"/>: subscribed at
/// once, so a snapshot read after it misses nothing. The reader marks the snapshot's devices with <see cref="Sent"/>; from
/// then on <see cref="ReadAsync"/> delivers only changes of the view and remembers the ids it delivered: a device that leaves
/// the view (a tag or site change) becomes <see cref="DeviceChangeKind.Removed"/>, one that enters it
/// <see cref="DeviceChangeKind.Added"/>, and after a change of the tag and site catalog (a deleted tag, a removed site) the
/// ids are compared with the view again. A view with all devices passes every change through unchanged (today's feed).
/// A change of the group's rules ends the user's streams anyway (<c>CallerStreams</c>).
/// </summary>
public sealed class DeviceViewFeed : IDisposable
{
    private readonly DeviceView _view;
    private readonly DeviceChangeSubscription _subscription;
    private readonly DeviceScopeCatalog? _catalog;
    private readonly HashSet<Guid> _sent = [];
    private readonly Queue<Guid> _entering = new();
    private CancellationTokenSource? _wake;
    private int _resync;

    internal DeviceViewFeed(DeviceView view, IDeviceChangeFeed feed, DeviceScopeCatalog catalog)
    {
        _view = view;
        _subscription = feed.Subscribe();
        if (!view.HasAllDevices)
        {
            _catalog = catalog;
            _catalog.Changed += OnCatalogChanged;
        }
    }

    /// <summary>The view the feed follows.</summary>
    public DeviceView View => _view;

    /// <summary>The number of devices the reader has now (tests; 0 for a view with all devices, which keeps no ids).</summary>
    public int SentCount => _sent.Count;

    /// <summary>The reader has <paramref name="deviceId"/> (it sent it in its snapshot).</summary>
    public void Sent(Guid deviceId)
    {
        if (_catalog is not null)
        {
            _sent.Add(deviceId);
        }
    }

    /// <summary>
    /// The next changes for the reader, at most <paramref name="max"/>, in order; waits while there are none. Null once the
    /// feed ended. Not thread safe: one reader.
    /// </summary>
    public async ValueTask<IReadOnlyList<DeviceChange>?> ReadAsync(int max, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var reader = _subscription.Reader;
        var batch = new List<DeviceChange>();
        while (true)
        {
            if (_catalog is not null && Interlocked.Exchange(ref _resync, 0) == 1)
            {
                await CompareAsync(batch, ct).ConfigureAwait(false);
            }

            if (_entering.Count > 0)
            {
                await AddEnteringAsync(batch, max, ct).ConfigureAwait(false);
            }

            while (batch.Count < max && reader.TryRead(out var next))
            {
                if (Deliver(next) is { } delivered)
                {
                    batch.Add(delivered);
                }
            }

            if (batch.Count > 0)
            {
                return batch;
            }

            if (!await WaitAsync(reader, ct).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_catalog is not null)
        {
            _catalog.Changed -= OnCatalogChanged;
        }

        _subscription.Dispose();
    }

    /// <summary>What the reader gets of <paramref name="change"/>; null when it is not for this view.</summary>
    private DeviceChange? Deliver(DeviceChange change)
    {
        if (_catalog is null)
        {
            return change;
        }

        if (change.Kind == DeviceChangeKind.Removed || change.Device is null)
        {
            return _sent.Remove(change.DeviceId) ? change with { Device = null, Kind = DeviceChangeKind.Removed } : null;
        }

        var inside = _view.Contains(change.Device);
        if (_sent.Contains(change.DeviceId))
        {
            if (inside)
            {
                return change;
            }

            _sent.Remove(change.DeviceId);
            return new DeviceChange(DeviceChangeKind.Removed, change.DeviceId, null);
        }

        if (!inside)
        {
            return null;
        }

        _sent.Add(change.DeviceId);
        return change with { Kind = DeviceChangeKind.Added };
    }

    /// <summary>The catalog changed: Removed for the ids that left the view; the ones that entered are read in pages.</summary>
    private async Task CompareAsync(List<DeviceChange> batch, CancellationToken ct)
    {
        var now = await _view.IdsAsync(ct).ConfigureAwait(false);
        foreach (var id in _sent.Where(id => !now.Contains(id)).ToList())
        {
            _sent.Remove(id);
            batch.Add(new DeviceChange(DeviceChangeKind.Removed, id, null));
        }

        _entering.Clear();
        foreach (var id in now)
        {
            if (!_sent.Contains(id))
            {
                _entering.Enqueue(id);
            }
        }
    }

    private async Task AddEnteringAsync(List<DeviceChange> batch, int max, CancellationToken ct)
    {
        var take = new List<Guid>();
        while (take.Count < Math.Max(1, max - batch.Count) && _entering.TryDequeue(out var id))
        {
            if (!_sent.Contains(id))
            {
                take.Add(id);
            }
        }

        if (take.Count == 0)
        {
            return;
        }

        // A device removed meanwhile is not there (its Removed is in the feed); a device that left again is left out.
        foreach (var device in await _view.Factory.Devices.GetManyAsync(take, ct).ConfigureAwait(false))
        {
            if (_view.Contains(device) && _sent.Add(device.Id))
            {
                batch.Add(new DeviceChange(DeviceChangeKind.Added, device.Id, device));
            }
        }
    }

    /// <summary>Waits for a change, or for a catalog change (a view with a group).</summary>
    private async ValueTask<bool> WaitAsync(System.Threading.Channels.ChannelReader<DeviceChange> reader, CancellationToken ct)
    {
        if (_catalog is null)
        {
            return await reader.WaitToReadAsync(ct).ConfigureAwait(false);
        }

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _wake, wake);
        try
        {
            if (Volatile.Read(ref _resync) == 1)
            {
                return true;
            }

            return await reader.WaitToReadAsync(wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return true; // woken by a catalog change
        }
        finally
        {
            Interlocked.CompareExchange(ref _wake, null, wake);
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        Volatile.Write(ref _resync, 1);
        try
        {
            _ = Volatile.Read(ref _wake)?.CancelAsync(); // the reader runs on the thread pool, never on the raising thread
        }
        catch (ObjectDisposedException)
        {
            // The wait ended meanwhile; the flag is read before the next one.
        }
    }
}
