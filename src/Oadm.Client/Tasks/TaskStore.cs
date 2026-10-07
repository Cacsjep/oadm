using System.Collections.ObjectModel;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

/// <summary>
/// In-memory mirror of the server's task table, newest first. Must be used from the UI thread.
/// Task rows name their devices through the <see cref="DeviceStore"/> and follow its changes.
/// </summary>
public sealed class TaskStore
{
    private readonly Dictionary<string, TaskRowViewModel> _byId = new(StringComparer.Ordinal);
    private readonly DeviceStore _devices;

    public TaskStore(DeviceStore devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        devices.Changed += (_, _) =>
        {
            foreach (TaskRowViewModel row in Tasks)
            {
                row.ResolveDevices();
            }
        };
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    public int ActiveCount => Tasks.Count(t => t.IsActive);

    /// <summary>Raised after any change, with the task that changed (null for a full reset).</summary>
    public event EventHandler<TaskRowViewModel?>? Changed;

    public TaskRowViewModel? Find(string id) => _byId.GetValueOrDefault(id);

    public void Reset(IEnumerable<TaskInfo> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TaskInfo task in snapshot.OrderBy(t => t.Created?.ToDateTime() ?? DateTime.MinValue))
        {
            seen.Add(task.Id);
            Upsert(task);
        }

        foreach (TaskRowViewModel stale in Tasks.Where(t => !seen.Contains(t.Id)).ToList())
        {
            Tasks.Remove(stale);
            _byId.Remove(stale.Id);
        }

        Changed?.Invoke(this, null);
    }

    public void Apply(TaskChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Task is null)
        {
            return;
        }

        if (change.Kind == TaskChanged.Types.Kind.Removed)
        {
            if (_byId.Remove(change.Task.Id, out TaskRowViewModel? row))
            {
                Tasks.Remove(row);
                Changed?.Invoke(this, row);
            }
        }
        else
        {
            TaskRowViewModel row = Upsert(change.Task);
            Changed?.Invoke(this, row);
        }
    }

    private TaskRowViewModel Upsert(TaskInfo task)
    {
        if (_byId.TryGetValue(task.Id, out TaskRowViewModel? row))
        {
            row.Update(task);
        }
        else
        {
            row = new TaskRowViewModel(task, _devices);
            _byId[task.Id] = row;
            Tasks.Insert(0, row);
        }

        return row;
    }
}
