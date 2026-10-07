using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

/// <summary>
/// In-memory mirror of the server's task table, newest first. Must be used from the UI thread.
/// Task rows name their devices through the <see cref="DeviceStore"/> and follow its changes.
/// Scale (50,000 tasks on the server, 5,000 per Run): the mirror keeps at most <see cref="MaxTasks"/>
/// tasks (all active ones plus the newest finished ones; the server sends that many in the Watch
/// snapshot), the active count is kept up to date per change in O(1), a device change re-labels only
/// the tasks of that device, and a batch of changes raises one collection notification.
/// </summary>
public sealed class TaskStore
{
    /// <summary>Most tasks kept (and asked for in the Watch snapshot); older finished ones are dropped.</summary>
    public const int MaxTasks = 10_000;

    private readonly Dictionary<string, TaskRowViewModel> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<TaskRowViewModel>> _byDevice = new(StringComparer.Ordinal);
    private readonly DeviceStore _devices;
    private int _activeCount;

    public TaskStore(DeviceStore devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        devices.Changed += OnDevicesChanged;
    }

    public RangeObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    /// <summary>Queued and running tasks, maintained per change (O(1)).</summary>
    public int ActiveCount => _activeCount;

    /// <summary>Most tasks kept; <see cref="MaxTasks"/> unless a test lowers it.</summary>
    public int Capacity { get; init; } = MaxTasks;

    /// <summary>Raised after any change, with the task that changed (null for a reset or a batch of several tasks).</summary>
    public event EventHandler<TaskRowViewModel?>? Changed;

    public TaskRowViewModel? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Replaces the mirror with a snapshot (any order); one collection notification.</summary>
    public void Reset(IEnumerable<TaskInfo> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TaskRowViewModel>();
        foreach (TaskInfo task in snapshot)
        {
            if (task is null || !seen.Add(task.Id))
            {
                continue;
            }

            rows.Add(Upsert(task, out _));
        }

        foreach (TaskRowViewModel stale in _byId.Values.Where(r => !seen.Contains(r.Id)).ToList())
        {
            Forget(stale);
        }

        rows.Sort(NewestFirst);
        Tasks.ReplaceAll(Trim(rows));
        Changed?.Invoke(this, null);
    }

    public void Apply(TaskChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ApplyBatch([change]);
    }

    /// <summary>
    /// Applies changes in order: rows update in place, new tasks go to the top (newest first) and removed
    /// ones leave in one collection operation each; then the oldest finished tasks beyond the capacity are
    /// dropped. SNAPSHOT_END markers and changes without a task are ignored.
    /// </summary>
    public void ApplyBatch(IReadOnlyList<TaskChanged> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var added = new List<TaskRowViewModel>();
        var removed = new HashSet<TaskRowViewModel>();
        TaskRowViewModel? single = null;
        int touched = 0;
        foreach (TaskChanged change in changes)
        {
            if (change.Task is null || change.Kind == TaskChanged.Types.Kind.SnapshotEnd)
            {
                continue;
            }

            touched++;
            if (change.Kind == TaskChanged.Types.Kind.Removed)
            {
                if (_byId.TryGetValue(change.Task.Id, out TaskRowViewModel? row))
                {
                    Forget(row);
                    if (!added.Remove(row))
                    {
                        removed.Add(row);
                    }

                    single = row;
                }
            }
            else
            {
                TaskRowViewModel row = Upsert(change.Task, out bool isNew);
                if (isNew)
                {
                    added.Add(row);
                }

                single = row;
            }
        }

        if (touched == 0)
        {
            return;
        }

        if (removed.Count > 0)
        {
            Tasks.RemoveAll(removed.Contains);
        }

        if (added.Count > 0)
        {
            added.Sort(NewestFirst);
            Tasks.InsertRange(0, added);
        }

        if (Tasks.Count > Capacity)
        {
            HashSet<TaskRowViewModel> dropped = [.. Tasks.Except(Trim(Tasks))];
            foreach (TaskRowViewModel row in dropped)
            {
                Forget(row);
            }

            Tasks.RemoveAll(dropped.Contains);
        }

        Changed?.Invoke(this, touched == 1 ? single : null);
    }

    /// <summary>Rows sorted newest first, without the oldest finished ones beyond the capacity.</summary>
    private List<TaskRowViewModel> Trim(IReadOnlyList<TaskRowViewModel> newestFirst)
    {
        if (newestFirst.Count <= Capacity)
        {
            return [.. newestFirst];
        }

        int finishedToKeep = Math.Max(0, Capacity - newestFirst.Count(r => r.IsActive));
        var kept = new List<TaskRowViewModel>(Capacity);
        foreach (TaskRowViewModel row in newestFirst)
        {
            if (row.IsActive)
            {
                kept.Add(row);
            }
            else if (finishedToKeep > 0)
            {
                kept.Add(row);
                finishedToKeep--;
            }
            else if (_byId.ContainsKey(row.Id))
            {
                Forget(row);
            }
        }

        return kept;
    }

    private static int NewestFirst(TaskRowViewModel a, TaskRowViewModel b) => b.Created.CompareTo(a.Created);

    private TaskRowViewModel Upsert(TaskInfo task, out bool isNew)
    {
        if (_byId.TryGetValue(task.Id, out TaskRowViewModel? row))
        {
            bool wasActive = row.IsActive;
            string oldDevice = row.DeviceId;
            row.Update(task);
            _activeCount += (row.IsActive ? 1 : 0) - (wasActive ? 1 : 0);
            if (oldDevice != row.DeviceId)
            {
                Unindex(oldDevice, row);
                Index(row);
            }

            isNew = false;
        }
        else
        {
            row = new TaskRowViewModel(task, _devices);
            _byId[task.Id] = row;
            _activeCount += row.IsActive ? 1 : 0;
            Index(row);
            isNew = true;
        }

        return row;
    }

    /// <summary>Drops a row from the indexes and the active count (not from <see cref="Tasks"/>).</summary>
    private void Forget(TaskRowViewModel row)
    {
        if (_byId.Remove(row.Id))
        {
            _activeCount -= row.IsActive ? 1 : 0;
            Unindex(row.DeviceId, row);
        }
    }

    private void Index(TaskRowViewModel row)
    {
        if (row.DeviceId.Length == 0)
        {
            return;
        }

        if (!_byDevice.TryGetValue(row.DeviceId, out HashSet<TaskRowViewModel>? rows))
        {
            _byDevice[row.DeviceId] = rows = [];
        }

        rows.Add(row);
    }

    private void Unindex(string deviceId, TaskRowViewModel row)
    {
        if (deviceId.Length > 0 && _byDevice.TryGetValue(deviceId, out HashSet<TaskRowViewModel>? rows) && rows.Remove(row) && rows.Count == 0)
        {
            _byDevice.Remove(deviceId);
        }
    }

    /// <summary>Re-labels the tasks of the changed devices only (all tasks after a device reset).</summary>
    private void OnDevicesChanged(object? sender, DeviceStoreChangedEventArgs e)
    {
        if (e.IsReset)
        {
            foreach (TaskRowViewModel row in Tasks)
            {
                row.ResolveDevices();
            }

            return;
        }

        foreach (string id in e.DeviceIds)
        {
            if (_byDevice.TryGetValue(id, out HashSet<TaskRowViewModel>? rows))
            {
                foreach (TaskRowViewModel row in rows)
                {
                    row.ResolveDevices();
                }
            }
        }
    }
}
