using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;

namespace Oadm.Core.Tasks;

/// <summary>
/// The task feed of one watcher's view (gRPC TaskService.Watch, docs/spec/device-scope.md, Tasks): for a user with all
/// devices every change as before; for a user with a device group only the tasks of devices in the view. Membership
/// follows the device feed (each changed device is checked against the group's compiled rules, a few hash lookups), the tag
/// and site catalog and the group's rules (the view's ids are compared again, like <see cref="DeviceViewFeed"/>). When a
/// device leaves the view, its sent tasks are sent as Removed at once; when one enters it, its tasks are read like the
/// snapshot (the active ones and the newest up to the snapshot limit, one query per batch of devices) and sent as Added.
/// Made by <see cref="TaskEngine.SubscribeWithSnapshotAsync(DeviceView, int?, CancellationToken)"/>.
/// </summary>
public sealed class TaskViewSubscription : IDisposable
{
    private static readonly IReadOnlySet<Guid> NoIds = new HashSet<Guid>();

    private readonly TaskChangeSubscription _inner;
    private readonly DeviceView? _view;
    private readonly Func<IReadOnlySet<Guid>, CancellationToken, Task<IReadOnlyList<TaskRecord>>>? _readEntering;
    private readonly IDeviceChangeFeed? _devices;
    private readonly DeviceScopeCatalog? _catalog;
    private readonly DeviceGroupStore? _groups;

    // Membership seen on the device feed since the ids were read: (inside, sequence number of the change).
    private readonly ConcurrentDictionary<Guid, (bool Inside, long Seq)> _changed = new();

    // Devices whose membership changed; the reader sends their Removed or Added.
    private readonly ConcurrentQueue<Guid> _moved = new();

    // The tasks the watcher has (reader thread only): the last record sent per task, and the tasks per device.
    private readonly Dictionary<Guid, TaskRecord> _sent = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _sentByDevice = [];
    private IReadOnlySet<Guid> _ids = NoIds;
    private long _seq;
    private int _resync;
    private CancellationTokenSource? _wake;
    private int _disposed;

    private TaskViewSubscription(
        TaskChangeSubscription inner,
        DeviceView? view,
        Func<IReadOnlySet<Guid>, CancellationToken, Task<IReadOnlyList<TaskRecord>>>? readEntering)
    {
        _inner = inner;
        _view = view;
        _readEntering = readEntering;
        if (view is not null)
        {
            // Subscribed before the ids are read: every device, catalog or rule change after that read is seen here.
            _devices = view.Factory.Feed;
            _devices.Changed += OnDeviceChanged;
            _catalog = view.Factory.Catalog;
            _catalog.Changed += OnCatalogChanged;
            _groups = view.Factory.Groups;
            _groups.Changed += OnGroupChanged;
            _groups.Deleted += OnGroupDeleted;
        }
    }

    /// <summary>True when every change passes (a user with all devices).</summary>
    public bool PassesEverything => _view is null;

    /// <summary>The number of tasks the watcher has now (tests; 0 for a user with all devices, whose tasks are not kept).</summary>
    public int SentCount => _sent.Count;

    /// <summary>A subscription for <paramref name="view"/>; reads the view's device ids for a group.</summary>
    /// <param name="inner">The engine's task feed.</param>
    /// <param name="view">The watcher's view.</param>
    /// <param name="readEntering">Reads the tasks of devices that entered the view, like the snapshot (its limit).</param>
    /// <param name="ct">Cancellation.</param>
    internal static async Task<TaskViewSubscription> CreateAsync(
        TaskChangeSubscription inner,
        DeviceView view,
        Func<IReadOnlySet<Guid>, CancellationToken, Task<IReadOnlyList<TaskRecord>>> readEntering,
        CancellationToken ct)
    {
        var subscription = view.HasAllDevices ? new TaskViewSubscription(inner, null, null) : new TaskViewSubscription(inner, view, readEntering);
        try
        {
            if (subscription._view is { } scoped)
            {
                subscription._ids = await scoped.IdsAsync(ct).ConfigureAwait(false);
            }

            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    /// <summary>The device ids of the view when the subscription started (the snapshot's filter).</summary>
    internal IReadOnlySet<Guid> Ids => _ids;

    /// <summary>Whether a task of <paramref name="deviceId"/> is for this watcher now.</summary>
    public bool InView(Guid deviceId) =>
        _view is null || (_changed.TryGetValue(deviceId, out var change) ? change.Inside : _ids.Contains(deviceId));

    /// <summary>The snapshot's tasks were sent.</summary>
    internal void MarkSent(IEnumerable<TaskRecord> tasks)
    {
        if (_view is null)
        {
            return;
        }

        foreach (var task in tasks)
        {
            Remember(task);
        }
    }

    /// <summary>The live changes for this watcher, in order (one reader).</summary>
    public async IAsyncEnumerable<TaskChange> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var reader = _inner.Reader;
        while (true)
        {
            if (_view is not null)
            {
                foreach (var change in await TakeMovesAsync(ct).ConfigureAwait(false))
                {
                    yield return change;
                }
            }

            while (reader.TryRead(out var next))
            {
                if (Map(next) is { } mapped)
                {
                    yield return mapped;
                }

                if (_view is not null && (Volatile.Read(ref _resync) == 1 || !_moved.IsEmpty))
                {
                    break; // a device moved: its Removed or Added goes out before more changes
                }
            }

            if (!await WaitAsync(reader, ct).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    /// <summary>The change as this watcher gets it; null when it is not for the watcher.</summary>
    internal TaskChange? Map(TaskChange change)
    {
        if (_view is null)
        {
            return change;
        }

        var task = change.Task;
        if (change.Kind == TaskChangeKind.Removed)
        {
            return Forget(task.Id) ? change : null;
        }

        if (InView(task.DeviceId))
        {
            return Remember(task) && change.Kind == TaskChangeKind.Updated ? change with { Kind = TaskChangeKind.Added } : change;
        }

        // The device left the view: the watcher drops the task (once).
        return Forget(task.Id) ? new TaskChange(TaskChangeKind.Removed, task) : null;
    }

    /// <summary>
    /// The Removed of the sent tasks of devices that left the view and the Added of the tasks of devices that entered it,
    /// after device changes (<see cref="_moved"/>) and catalog or rule changes (<see cref="_resync"/>: the ids again).
    /// </summary>
    private async Task<IReadOnlyList<TaskChange>> TakeMovesAsync(CancellationToken ct)
    {
        if (_moved.IsEmpty && Volatile.Read(ref _resync) == 0)
        {
            return [];
        }

        var moved = new HashSet<Guid>();
        while (_moved.TryDequeue(out var id))
        {
            moved.Add(id);
        }

        if (Interlocked.Exchange(ref _resync, 0) == 1)
        {
            // Device changes up to here are in the ids read now; later ones (checked with the new rules) stay on top.
            var seq = Interlocked.Read(ref _seq);
            var now = await _view!.IdsAsync(ct).ConfigureAwait(false);
            foreach (var id in now)
            {
                if (!InView(id))
                {
                    moved.Add(id);
                }
            }

            foreach (var id in _sentByDevice.Keys)
            {
                if (!now.Contains(id))
                {
                    moved.Add(id);
                }
            }

            _ids = now;
            foreach (var (id, change) in _changed)
            {
                if (change.Seq <= seq)
                {
                    _changed.TryRemove(new KeyValuePair<Guid, (bool, long)>(id, change));
                }
            }
        }

        if (moved.Count == 0)
        {
            return [];
        }

        var changes = new List<TaskChange>();
        var entering = new HashSet<Guid>();
        foreach (var deviceId in moved)
        {
            if (InView(deviceId))
            {
                entering.Add(deviceId);
            }
            else if (_sentByDevice.TryGetValue(deviceId, out var tasks))
            {
                foreach (var taskId in tasks.ToList())
                {
                    var record = _sent[taskId];
                    Forget(taskId);
                    changes.Add(new TaskChange(TaskChangeKind.Removed, record));
                }
            }
        }

        if (entering.Count > 0)
        {
            // Oldest first, like a change feed: the client keeps the newest on top.
            var records = await _readEntering!(entering, ct).ConfigureAwait(false);
            for (var i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                if (InView(record.DeviceId) && Remember(record))
                {
                    changes.Add(new TaskChange(TaskChangeKind.Added, record));
                }
            }
        }

        return changes;
    }

    /// <summary>Keeps <paramref name="task"/> as sent; true when the watcher did not have it yet.</summary>
    private bool Remember(TaskRecord task)
    {
        if (!_sent.TryAdd(task.Id, task))
        {
            _sent[task.Id] = task;
            return false;
        }

        if (!_sentByDevice.TryGetValue(task.DeviceId, out var tasks))
        {
            _sentByDevice[task.DeviceId] = tasks = [];
        }

        tasks.Add(task.Id);
        return true;
    }

    /// <summary>Drops <paramref name="taskId"/>; true when the watcher had it.</summary>
    private bool Forget(Guid taskId)
    {
        if (!_sent.Remove(taskId, out var task))
        {
            return false;
        }

        if (_sentByDevice.TryGetValue(task.DeviceId, out var tasks) && tasks.Remove(taskId) && tasks.Count == 0)
        {
            _sentByDevice.Remove(task.DeviceId);
        }

        return true;
    }

    /// <summary>Waits for a task change, or for a membership change of a device, the catalog or the rules.</summary>
    private async ValueTask<bool> WaitAsync(ChannelReader<TaskChange> reader, CancellationToken ct)
    {
        if (_view is null)
        {
            return await reader.WaitToReadAsync(ct).ConfigureAwait(false);
        }

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _wake, wake);
        try
        {
            if (Volatile.Read(ref _resync) == 1 || !_moved.IsEmpty)
            {
                return true;
            }

            return await reader.WaitToReadAsync(wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return true; // woken by a membership change
        }
        finally
        {
            Interlocked.CompareExchange(ref _wake, null, wake);
        }
    }

    private void Wake()
    {
        try
        {
            _ = Volatile.Read(ref _wake)?.CancelAsync(); // the reader runs on the thread pool, never on the raising thread
        }
        catch (ObjectDisposedException)
        {
            // The wait ended meanwhile; the flags are read before the next one.
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChange change)
    {
        var inside = change.Kind != DeviceChangeKind.Removed && change.Device is { } device && _view!.Contains(device);
        var before = InView(change.DeviceId);
        _changed[change.DeviceId] = (inside, Interlocked.Increment(ref _seq));
        if (before != inside)
        {
            _moved.Enqueue(change.DeviceId);
            Wake();
        }
    }

    private void Resync()
    {
        Volatile.Write(ref _resync, 1);
        Wake();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => Resync();

    private void OnGroupChanged(object? sender, Guid groupId)
    {
        if (groupId == _view!.GroupId)
        {
            Resync();
        }
    }

    private void OnGroupDeleted(object? sender, DeviceGroupDeleteResult deleted)
    {
        if (deleted.Id == _view!.GroupId)
        {
            Resync();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_devices is not null)
        {
            _devices.Changed -= OnDeviceChanged;
        }

        if (_catalog is not null)
        {
            _catalog.Changed -= OnCatalogChanged;
        }

        if (_groups is not null)
        {
            _groups.Changed -= OnGroupChanged;
            _groups.Deleted -= OnGroupDeleted;
        }

        _inner.Dispose();
    }
}
