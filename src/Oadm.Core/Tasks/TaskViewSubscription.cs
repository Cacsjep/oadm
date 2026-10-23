using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;

namespace Oadm.Core.Tasks;

/// <summary>
/// The task feed of one watcher's view (gRPC TaskService.Watch, docs/spec/device-scope.md, Tasks): for a user with all
/// devices every change as before; for a user with a device group only the tasks of devices in the view. A task whose device
/// leaves the view is sent as Removed once, one whose device enters it as Added. Membership follows the device feed (each
/// changed device is checked against the group's compiled rules, a few hash lookups); a change of the group itself ends the
/// stream (<c>CallerStreams</c>). Made by <see cref="TaskEngine.SubscribeWithSnapshotAsync(DeviceView, int?, CancellationToken)"/>.
/// </summary>
public sealed class TaskViewSubscription : IDisposable
{
    private static readonly IReadOnlySet<Guid> NoIds = new HashSet<Guid>();

    private readonly TaskChangeSubscription _inner;
    private readonly DeviceView? _view;
    private readonly IDeviceChangeFeed? _devices;
    private readonly ConcurrentDictionary<Guid, bool> _changed = new();
    private readonly HashSet<Guid> _sent = [];
    private IReadOnlySet<Guid> _ids = NoIds;
    private int _disposed;

    private TaskViewSubscription(TaskChangeSubscription inner, DeviceView? view)
    {
        _inner = inner;
        _view = view;
        if (view is not null)
        {
            // Subscribed before the ids are read: every device change after that read is seen here.
            _devices = view.Factory.Feed;
            _devices.Changed += OnDeviceChanged;
        }
    }

    /// <summary>True when every change passes (a user with all devices).</summary>
    public bool PassesEverything => _view is null;

    /// <summary>A subscription for <paramref name="view"/>; reads the view's device ids for a group.</summary>
    internal static async Task<TaskViewSubscription> CreateAsync(TaskChangeSubscription inner, DeviceView view, CancellationToken ct)
    {
        var subscription = new TaskViewSubscription(inner, view.HasAllDevices ? null : view);
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
        _view is null || (_changed.TryGetValue(deviceId, out var inside) ? inside : _ids.Contains(deviceId));

    /// <summary>The snapshot's tasks were sent.</summary>
    internal void MarkSent(IEnumerable<TaskRecord> tasks)
    {
        if (_view is null)
        {
            return;
        }

        foreach (var task in tasks)
        {
            _sent.Add(task.Id);
        }
    }

    /// <summary>The live changes for this watcher, in order (one reader).</summary>
    public async IAsyncEnumerable<TaskChange> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var change in _inner.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (Map(change) is { } mapped)
            {
                yield return mapped;
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

        var id = change.Task.Id;
        if (change.Kind == TaskChangeKind.Removed)
        {
            return _sent.Remove(id) ? change : null;
        }

        if (InView(change.Task.DeviceId))
        {
            return _sent.Add(id) && change.Kind == TaskChangeKind.Updated ? change with { Kind = TaskChangeKind.Added } : change;
        }

        // The device left the view: the watcher drops the task (once).
        return _sent.Remove(id) ? new TaskChange(TaskChangeKind.Removed, change.Task) : null;
    }

    private void OnDeviceChanged(object? sender, DeviceChange change)
    {
        _changed[change.DeviceId] = change.Kind != DeviceChangeKind.Removed && change.Device is { } device && _view!.Contains(device);
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

        _inner.Dispose();
    }
}
