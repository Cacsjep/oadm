using Oadm.Client.Tags;
using Oadm.Contracts.V1;

namespace Oadm.Client.Devices;

/// <summary>What changed in the <see cref="DeviceStore"/>: a full reset, or the ids of added, updated and removed devices.</summary>
public sealed class DeviceStoreChangedEventArgs : EventArgs
{
    public static readonly DeviceStoreChangedEventArgs ResetAll = new(isReset: true, []);

    public DeviceStoreChangedEventArgs(bool isReset, IReadOnlyCollection<string> deviceIds)
    {
        IsReset = isReset;
        DeviceIds = deviceIds;
    }

    /// <summary>The whole table may have changed (snapshot after a (re)connect).</summary>
    public bool IsReset { get; }

    /// <summary>Devices added, changed or removed (empty for a reset).</summary>
    public IReadOnlyCollection<string> DeviceIds { get; }
}

/// <summary>
/// In-memory mirror of the server's device table. Must be used from the UI thread. Scale (5,000 devices):
/// lookups by id are O(1), a change costs O(1) plus the changed row, a batch of changes raises one
/// collection notification and one <see cref="Changed"/>, and a change that alters nothing raises nothing.
/// </summary>
public sealed class DeviceStore
{
    private readonly Dictionary<string, DeviceRowViewModel> _byId = new(StringComparer.Ordinal);

    /// <param name="tags">The tag definitions the rows resolve their tag names with (one shared store per client).</param>
    public DeviceStore(TagStore? tags = null)
    {
        Tags = tags ?? new TagStore();
    }

    /// <summary>The client's tag definitions (TagService.Watch), shared by every row's chips.</summary>
    public TagStore Tags { get; }

    public RangeObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    /// <summary>
    /// Raised once per reset or batch that changed something (added, removed or changed rows), with the
    /// affected ids. Used to refresh runnable task plugins and task device labels.
    /// </summary>
    public event EventHandler<DeviceStoreChangedEventArgs>? Changed;

    public DeviceRowViewModel? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Reconciles with a full snapshot: updates rows in place, appends new ones, removes missing ones; one
    /// collection notification at most.
    /// </summary>
    public void Reset(IEnumerable<Device> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<DeviceRowViewModel>();
        foreach (Device device in snapshot)
        {
            if (!seen.Add(device.Id))
            {
                continue;
            }

            if (_byId.TryGetValue(device.Id, out DeviceRowViewModel? row))
            {
                row.Apply(device);
            }
            else
            {
                row = new DeviceRowViewModel(device, Tags);
                _byId[device.Id] = row;
                added.Add(row);
            }
        }

        bool anyStale = _byId.Count > seen.Count;
        if (anyStale)
        {
            foreach (string id in _byId.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _byId.Remove(id);
            }
        }

        if (anyStale || added.Count > 0)
        {
            var rows = new List<DeviceRowViewModel>(_byId.Count);
            rows.AddRange(Devices.Where(d => seen.Contains(d.Id)));
            rows.AddRange(added);
            Devices.ReplaceAll(rows);
        }

        Changed?.Invoke(this, DeviceStoreChangedEventArgs.ResetAll);
    }

    public void Apply(DeviceChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ApplyBatch([change]);
    }

    /// <summary>
    /// Applies changes in order. Rows are updated in place; additions are appended and removals taken out
    /// in one collection operation each; <see cref="Changed"/> is raised once with the ids that changed.
    /// SNAPSHOT_END markers and changes without a device are ignored.
    /// </summary>
    public void ApplyBatch(IReadOnlyList<DeviceChanged> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<DeviceRowViewModel>();
        var removed = new HashSet<DeviceRowViewModel>();
        foreach (DeviceChanged change in changes)
        {
            if (change.Device is null || change.Kind == DeviceChanged.Types.Kind.SnapshotEnd)
            {
                continue;
            }

            string id = change.Device.Id;
            if (change.Kind == DeviceChanged.Types.Kind.Removed)
            {
                if (_byId.Remove(id, out DeviceRowViewModel? row))
                {
                    changedIds.Add(id);
                    if (!added.Remove(row))
                    {
                        removed.Add(row);
                    }
                }
            }
            else if (_byId.TryGetValue(id, out DeviceRowViewModel? row))
            {
                if (row.Apply(change.Device))
                {
                    changedIds.Add(id);
                }
            }
            else
            {
                row = new DeviceRowViewModel(change.Device, Tags);
                _byId[id] = row;
                added.Add(row);
                changedIds.Add(id);
            }
        }

        if (removed.Count > 0)
        {
            Devices.RemoveAll(removed.Contains);
        }

        Devices.AddRange(added);
        if (changedIds.Count > 0)
        {
            Changed?.Invoke(this, new DeviceStoreChangedEventArgs(isReset: false, changedIds));
        }
    }
}
