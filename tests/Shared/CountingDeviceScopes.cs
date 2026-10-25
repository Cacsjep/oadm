using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Tests.Shared;

/// <summary>
/// Device groups like the host's for scale tests (linked into plugin tests): membership by hash lookups, and every read of a
/// group's devices (<see cref="IDeviceRepository.ListAsync"/>) counted in <see cref="Reads"/>. <see cref="Move"/> changes a
/// group without raising <see cref="Changed"/> (the host raises it a moment later); <see cref="RaiseChanged"/> raises it.
/// </summary>
internal sealed class CountingDeviceScopes(IReadOnlyList<IDeviceInfo> devices) : IDeviceScopes
{
    private readonly Dictionary<string, HashSet<Guid>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private int _reads;

    public event EventHandler<string>? Changed;

    /// <summary>Reads of a group's devices so far.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public IReadOnlyList<IDeviceInfo> All => devices;

    /// <summary>Adds a group; returns its key.</summary>
    public string Add(IEnumerable<Guid> ids)
    {
        var key = Guid.NewGuid().ToString();
        lock (_groups)
        {
            _groups[key] = [.. ids];
        }

        return key;
    }

    public void Move(string key, Guid id, bool into)
    {
        lock (_groups)
        {
            _ = into ? _groups[key].Add(id) : _groups[key].Remove(id);
        }
    }

    public void Delete(string key)
    {
        lock (_groups)
        {
            _groups.Remove(key);
        }

        RaiseChanged(key);
    }

    public void RaiseChanged(string key) => Changed?.Invoke(this, key);

    public bool Exists(string scopeKey)
    {
        lock (_groups)
        {
            return scopeKey == ICallContext.AllDevicesScopeKey || _groups.ContainsKey(scopeKey);
        }
    }

    public bool Contains(string scopeKey, IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Contains(scopeKey, device.Id);
    }

    public bool Contains(string scopeKey, Guid deviceId)
    {
        lock (_groups)
        {
            return scopeKey == ICallContext.AllDevicesScopeKey || (_groups.TryGetValue(scopeKey, out var ids) && ids.Contains(deviceId));
        }
    }

    public IDeviceRepository Devices(string scopeKey) => new Repository(this, scopeKey);

    public string? NameOf(string scopeKey) => Exists(scopeKey) ? "Group" : null;

    private sealed class Repository(CountingDeviceScopes scopes, string key) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref scopes._reads);
            return Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. scopes.All.Where(d => scopes.Contains(key, d.Id))]);
        }

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(scopes.Contains(key, id) ? scopes.All.FirstOrDefault(d => d.Id == id) : null);
    }
}
