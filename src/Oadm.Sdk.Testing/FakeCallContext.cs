using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The user's call of a plugin method (<see cref="ICallContext"/>, SDK 0.4.0) over a <see cref="FakeCoreContext"/>: a user
/// with all devices (<see cref="AllDevices"/>) or limited to a device group (<see cref="WithGroup"/>), with the role's
/// permissions (<see cref="Permissions"/>, or every permission). Devices outside the group behave like on the server:
/// <see cref="IDeviceRepository.FindAsync"/> answers null, VAPIX clients, task runs and event streams throw
/// <see cref="DevicesNotAvailableException"/>, nothing runs. <see cref="Refused"/> counts those refusals for the canary
/// tests of a plugin.
/// </summary>
public sealed class FakeCallContext : ICallContext
{
    private readonly FakeCoreContext _core;
    private readonly HashSet<Guid>? _group;
    private int _refused;

    private FakeCallContext(FakeCoreContext core, string userName, HashSet<Guid>? group, string scopeKey)
    {
        _core = core;
        _group = group;
        UserName = userName;
        DeviceScopeKey = scopeKey;
        Devices = new ScopedDevices(this);
        Vapix = new ScopedVapix(this);
        Tasks = new ScopedTasks(this);
        EventStreams = ((ICorePluginContext)core).EventStreams is { } streams ? new ScopedEventStreams(this, streams) : null;
    }

    /// <summary>A user with all devices (an administrator, or "All devices").</summary>
    public static FakeCallContext AllDevices(FakeCoreContext core, string userName = "admin")
    {
        ArgumentNullException.ThrowIfNull(core);
        return new FakeCallContext(core, userName, null, ICallContext.AllDevicesScopeKey);
    }

    /// <summary>A user limited to a device group that holds exactly <paramref name="deviceIds"/>.</summary>
    public static FakeCallContext WithGroup(FakeCoreContext core, IEnumerable<Guid> deviceIds, string userName = "operator", string? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(deviceIds);
        return new FakeCallContext(core, userName, [.. deviceIds], groupId ?? Guid.NewGuid().ToString());
    }

    /// <inheritdoc />
    public string UserName { get; }

    /// <summary>The context the call belongs to.</summary>
    internal FakeCoreContext Core => _core;

    /// <inheritdoc />
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public string DeviceScopeKey { get; }

    /// <summary>The role's permissions; null (the default) = every permission.</summary>
    public IReadOnlySet<string>? Permissions { get; init; }

    /// <inheritdoc />
    public bool HasAllDevices => _group is null;

    /// <inheritdoc />
    public IDeviceRepository Devices { get; }

    /// <inheritdoc />
    public IVapixClientFactory Vapix { get; }

    /// <inheritdoc />
    public ITaskRunner Tasks { get; }

    /// <inheritdoc />
    public IDeviceEventStreams? EventStreams { get; }

    /// <summary>How many requests named devices outside the group (each was refused before anything ran).</summary>
    public int Refused => Volatile.Read(ref _refused);

    /// <inheritdoc />
    public bool Has(string permission) => Permissions is null || Permissions.Contains(permission);

    /// <inheritdoc />
    public bool Contains(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Contains(device.Id);
    }

    private bool Contains(Guid id) => _group is null ? _core.DeviceList.Exists(d => d.Id == id) : _group.Contains(id) && _core.DeviceList.Exists(d => d.Id == id);

    private void RequireAll(IReadOnlyCollection<Guid> ids)
    {
        var missing = ids.Distinct().Count(id => !Contains(id));
        if (missing > 0)
        {
            Interlocked.Increment(ref _refused);
            throw new DevicesNotAvailableException(missing);
        }
    }

    private sealed class ScopedDevices(FakeCallContext call) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. call._core.DeviceList.Where(call.Contains)]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(call.Contains(id) ? call._core.DeviceList.Find(d => d.Id == id) : null);
    }

    private sealed class ScopedVapix(FakeCallContext call) : IVapixClientFactory
    {
        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            call.RequireAll([deviceId]);
            return call._core.ClientFor(deviceId);
        }
    }

    private sealed class ScopedTasks(FakeCallContext call) : ITaskRunner
    {
        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(deviceIds);
            call.RequireAll(deviceIds);
            return call._core.Run(pluginId, deviceIds, payloadJson, owner);
        }

        public bool Cancel(Guid taskId) => call._core.Tasks.Cancel(taskId);

        public bool? IsActive(Guid taskId) => call._core.Tasks.IsActive(taskId);
    }

    private sealed class ScopedEventStreams(FakeCallContext call, IDeviceEventStreams inner) : IDeviceEventStreams
    {
        public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            call.RequireAll([deviceId]);
            return inner.OpenAsync(deviceId, ct);
        }

        public Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct)
        {
            call.RequireAll([deviceId]);
            return inner.GetRtspPortAsync(deviceId, ct);
        }
    }
}
