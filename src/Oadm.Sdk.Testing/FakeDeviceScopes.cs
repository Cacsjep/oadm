using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The device groups of a <see cref="FakeCoreContext"/> or of a test's own device list (<see cref="IDeviceScopes"/>):
/// <see cref="Add"/> a group with its devices, <see cref="Delete"/> it (its key then exists no more and covers nothing,
/// like on the server). A check by device id covers only devices that exist.
/// </summary>
public sealed class FakeDeviceScopes : IDeviceScopes
{
    private readonly Func<IReadOnlyList<IDeviceInfo>> _devices;
    private readonly Dictionary<string, (string Name, HashSet<Guid> Devices)> _groups = new(StringComparer.Ordinal);

    /// <summary>The groups of <paramref name="core"/>'s devices.</summary>
    public FakeDeviceScopes(FakeCoreContext core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _devices = () => core.DeviceList;
    }

    /// <summary>The groups of a test's own devices (the list is read on every check).</summary>
    public FakeDeviceScopes(Func<IReadOnlyList<IDeviceInfo>> devices) => _devices = devices ?? throw new ArgumentNullException(nameof(devices));

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
            return Exists(device.Id);
        }

        lock (_groups)
        {
            return _groups.TryGetValue(scopeKey, out var group) && group.Devices.Contains(device.Id);
        }
    }

    /// <inheritdoc />
    public bool Contains(string scopeKey, Guid deviceId)
    {
        if (!Exists(deviceId))
        {
            return false;
        }

        if (scopeKey == ICallContext.AllDevicesScopeKey)
        {
            return true;
        }

        lock (_groups)
        {
            return _groups.TryGetValue(scopeKey, out var group) && group.Devices.Contains(deviceId);
        }
    }

    /// <inheritdoc />
    public IDeviceRepository Devices(string scopeKey) => new Scoped(this, scopeKey);

    /// <inheritdoc />
    public bool InAnyGroup(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_groups)
        {
            return _groups.Values.Any(g => g.Devices.Contains(device.Id));
        }
    }

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

    private bool Exists(Guid id) => _devices().Any(d => d.Id == id);

    private sealed class Scoped(FakeDeviceScopes scopes, string key) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. scopes._devices().Where(d => scopes.Contains(key, d))]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(scopes._devices().FirstOrDefault(d => d.Id == id && scopes.Contains(key, d)));
    }
}
