using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The device groups of a <see cref="FakeCoreContext"/> (<see cref="IDeviceScopes"/>): <see cref="Add"/> a group with its
/// devices, <see cref="Delete"/> it (its key then exists no more and covers nothing, like on the server).
/// </summary>
public sealed class FakeDeviceScopes(FakeCoreContext core) : IDeviceScopes
{
    private readonly FakeCoreContext _owner = core;
    private readonly Dictionary<string, (string Name, HashSet<Guid> Devices)> _groups = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event EventHandler<string>? Changed;

    /// <summary>Adds a group with <paramref name="deviceIds"/>; returns its key (as <see cref="ICallContext.DeviceScopeKey"/>).</summary>
    public string Add(string name, IEnumerable<Guid> deviceIds, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        key ??= Guid.NewGuid().ToString();
        lock (_groups)
        {
            _groups[key] = (name, [.. deviceIds]);
        }

        return key;
    }

    /// <summary>Deletes a group and raises <see cref="Changed"/>.</summary>
    public void Delete(string key)
    {
        lock (_groups)
        {
            _groups.Remove(key);
        }

        Changed?.Invoke(this, key);
    }

    /// <inheritdoc />
    public bool Exists(string scopeKey)
    {
        if (scopeKey == ICallContext.AllDevicesScopeKey)
        {
            return true;
        }

        lock (_groups)
        {
            return _groups.ContainsKey(scopeKey);
        }
    }

    /// <inheritdoc />
    public bool Contains(string scopeKey, IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (scopeKey == ICallContext.AllDevicesScopeKey)
        {
            return _owner.DeviceList.Exists(d => d.Id == device.Id);
        }

        lock (_groups)
        {
            return _groups.TryGetValue(scopeKey, out var group) && group.Devices.Contains(device.Id);
        }
    }

    /// <inheritdoc />
    public IDeviceRepository Devices(string scopeKey) => new Scoped(this, scopeKey);

    /// <inheritdoc />
    public string? NameOf(string scopeKey)
    {
        if (scopeKey == ICallContext.AllDevicesScopeKey)
        {
            return "All devices";
        }

        lock (_groups)
        {
            return _groups.TryGetValue(scopeKey, out var group) ? group.Name : null;
        }
    }

    private sealed class Scoped(FakeDeviceScopes scopes, string key) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. scopes._owner.DeviceList.Where(d => scopes.Contains(key, d))]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(scopes._owner.DeviceList.Find(d => d.Id == id && scopes.Contains(key, d)));
    }
}
