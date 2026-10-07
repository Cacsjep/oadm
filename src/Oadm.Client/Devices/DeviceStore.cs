using System.Collections.ObjectModel;

using Oadm.Contracts.V1;

namespace Oadm.Client.Devices;

/// <summary>In-memory mirror of the server's device table. Must be used from the UI thread.</summary>
public sealed class DeviceStore
{
    private readonly Dictionary<string, DeviceRowViewModel> _byId = new(StringComparer.Ordinal);

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    /// <summary>Raised after any add, remove or status change. Used to refresh runnable task plugins.</summary>
    public event EventHandler? Changed;

    public DeviceRowViewModel? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Reconciles with a full snapshot: updates rows in place, removes missing ones.</summary>
    public void Reset(IEnumerable<Device> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Device device in snapshot)
        {
            seen.Add(device.Id);
            Upsert(device);
        }

        foreach (DeviceRowViewModel stale in Devices.Where(d => !seen.Contains(d.Id)).ToList())
        {
            Devices.Remove(stale);
            _byId.Remove(stale.Id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Apply(DeviceChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Device is null)
        {
            return;
        }

        if (change.Kind == DeviceChanged.Types.Kind.Removed)
        {
            if (_byId.Remove(change.Device.Id, out DeviceRowViewModel? row))
            {
                Devices.Remove(row);
            }
        }
        else
        {
            Upsert(change.Device);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Upsert(Device device)
    {
        if (_byId.TryGetValue(device.Id, out DeviceRowViewModel? row))
        {
            row.Update(device);
        }
        else
        {
            row = new DeviceRowViewModel(device);
            _byId[device.Id] = row;
            Devices.Add(row);
        }
    }
}
