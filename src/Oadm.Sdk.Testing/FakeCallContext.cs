using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The user's call of a plugin method (<see cref="ICallContext"/>, SDK 0.4.0): a user with all devices
/// (<see cref="AllDevices"/>) or limited to a device group (<see cref="WithGroup"/>), with the role's permissions
/// (<see cref="Permissions"/>, or every permission), over a <see cref="FakeCoreContext"/>; or over a test's own device and
/// VAPIX fakes (<see cref="Over"/>, <see cref="OverGroup"/>). Devices outside the group behave like on the server:
/// <see cref="IDeviceRepository.FindAsync"/> answers null, VAPIX clients, task runs and event streams throw
/// <see cref="DevicesNotAvailableException"/>, nothing runs. <see cref="Refused"/> counts those refusals for the canary
/// tests of a plugin. Like the host, a group user's <see cref="ITaskRunner.IsActive"/> and <see cref="ITaskRunner.Cancel"/>
/// answer only the tasks that user started, <see cref="ITaskRunner.AnyActiveAsync"/> the tasks of the group's devices.
/// </summary>
public sealed class FakeCallContext : ICallContext
{
    private readonly FakeCoreContext? _core;
    private readonly IDeviceRepository? _devices;
    private readonly IVapixClientFactory? _vapix;
    private readonly ITaskRunner? _tasks;
    private readonly HashSet<Guid>? _group;
    private int _refused;

    private FakeCallContext(FakeCoreContext? core, Backing? backing, string userName, HashSet<Guid>? group, string scopeKey)
    {
        _core = core;
        _devices = backing?.Devices;
        _vapix = backing?.Vapix;
        _tasks = backing?.Tasks;
        _group = group;
        UserName = userName;
        DeviceScopeKey = scopeKey;
        Devices = new ScopedDevices(this);
        Vapix = new ScopedVapix(this);
        Tasks = new ScopedTasks(this);
        var streams = core is null ? backing?.EventStreams : ((ICorePluginContext)core).EventStreams;
        EventStreams = streams is null ? null : new ScopedEventStreams(this, streams);
    }

    /// <summary>A user with all devices (an administrator, or "All devices").</summary>
    public static FakeCallContext AllDevices(FakeCoreContext core, string userName = "admin")
    {
        ArgumentNullException.ThrowIfNull(core);
        return new FakeCallContext(core, null, userName, null, ICallContext.AllDevicesScopeKey);
    }

    /// <summary>A user limited to a device group that holds exactly <paramref name="deviceIds"/>.</summary>
    public static FakeCallContext WithGroup(FakeCoreContext core, IEnumerable<Guid> deviceIds, string userName = "operator", string? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(deviceIds);
        return new FakeCallContext(core, null, userName, [.. deviceIds], groupId ?? Guid.NewGuid().ToString());
    }

    /// <summary>
    /// A user with all devices over a test's own fakes: the call's devices are <paramref name="devices"/>, its VAPIX
    /// clients, event streams and task runs come from the given fakes (a missing one throws <see cref="NotSupportedException"/>).
    /// </summary>
    public static FakeCallContext Over(IDeviceRepository devices, IVapixClientFactory? vapix = null, IDeviceEventStreams? eventStreams = null, ITaskRunner? tasks = null, string userName = "admin")
    {
        ArgumentNullException.ThrowIfNull(devices);
        return new FakeCallContext(null, new Backing(devices, vapix, eventStreams, tasks), userName, null, ICallContext.AllDevicesScopeKey);
    }

    /// <summary>
    /// A user limited to a device group of <paramref name="deviceIds"/> over a test's own fakes (see <see cref="Over"/>):
    /// devices outside the group are refused before the fakes are asked.
    /// </summary>
    public static FakeCallContext OverGroup(
        IEnumerable<Guid> deviceIds,
        IDeviceRepository devices,
        IVapixClientFactory? vapix = null,
        IDeviceEventStreams? eventStreams = null,
        ITaskRunner? tasks = null,
        string userName = "operator",
        string? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(devices);
        return new FakeCallContext(null, new Backing(devices, vapix, eventStreams, tasks), userName, [.. deviceIds], groupId ?? Guid.NewGuid().ToString());
    }

    /// <inheritdoc />
    public string UserName { get; }

    /// <summary>The context the call belongs to (null for a call over a test's own fakes).</summary>
    internal FakeCoreContext? Core => _core;

    /// <inheritdoc />
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public string DeviceScopeKey { get; }

    /// <summary>The role's permissions; null (the default) = every permission.</summary>
    public IReadOnlySet<string>? Permissions { get; init; }

    /// <inheritdoc />
    public bool HasAllDevices => _group is null;

    /// <summary>The group names "This server" (<see cref="CoversRoute"/>); false by default.</summary>
    public bool ThisServer { get; set; }

    /// <summary>The sites the group names (<see cref="CoversRoute"/>); none by default.</summary>
    public IReadOnlySet<Guid> Sites { get; set; } = new HashSet<Guid>();

    /// <inheritdoc />
    public bool CoversRoute(Guid? siteId) => HasAllDevices || (siteId is { } site ? Sites.Contains(site) : ThisServer);

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

    /// <summary>In the call's devices: over a context the device must exist there; over a test's fakes they decide.</summary>
    private bool Contains(Guid id) =>
        (_group is null || _group.Contains(id)) && (_core is null || _core.DeviceList.Exists(d => d.Id == id));

    private void RequireAll(IReadOnlyCollection<Guid> ids)
    {
        var missing = ids.Distinct().Count(id => !Contains(id));
        if (missing > 0)
        {
            Interlocked.Increment(ref _refused);
            throw new DevicesNotAvailableException(missing);
        }
    }

    private static NotSupportedException Missing(string what) => new($"The test gave this call no {what}.");

    private sealed record Backing(IDeviceRepository Devices, IVapixClientFactory? Vapix, IDeviceEventStreams? EventStreams, ITaskRunner? Tasks);

    private sealed class ScopedDevices(FakeCallContext call) : IDeviceRepository
    {
        public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
            call._core is { } core
                ? [.. core.DeviceList.Where(call.Contains)]
                : [.. (await call._devices!.ListAsync(ct).ConfigureAwait(false)).Where(call.Contains)];

        public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
        {
            if (!call.Contains(id))
            {
                return null;
            }

            return call._core is { } core ? core.DeviceList.Find(d => d.Id == id) : await call._devices!.FindAsync(id, ct).ConfigureAwait(false);
        }
    }

    private sealed class ScopedVapix(FakeCallContext call) : IVapixClientFactory
    {
        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            call.RequireAll([deviceId]);
            return call._core is { } core ? core.ClientFor(deviceId) : (call._vapix ?? throw Missing("VAPIX clients")).CreateAsync(deviceId, ct);
        }
    }

    private sealed class ScopedTasks(FakeCallContext call) : ITaskRunner
    {
        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(deviceIds);
            call.RequireAll(deviceIds);
            return call._core is { } core
                ? core.Run(pluginId, deviceIds, payloadJson, owner, call.UserName)
                : (call._tasks ?? throw Missing("task runner")).RunAsync(pluginId, deviceIds, payloadJson, owner, ct);
        }

        public bool Cancel(Guid taskId)
        {
            if (call._core is not { } core)
            {
                return (call._tasks ?? throw Missing("task runner")).Cancel(taskId);
            }

            return Own(core, taskId) && core.EndTask(taskId);
        }

        public bool? IsActive(Guid taskId)
        {
            if (call._core is not { } core)
            {
                return (call._tasks ?? throw Missing("task runner")).IsActive(taskId);
            }

            return Own(core, taskId) ? core.TaskActive(taskId) : null;
        }

        public Task<bool?> AnyActiveAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(taskIds);
            if (call._core is not { } core)
            {
                return (call._tasks ?? throw Missing("task runner")).AnyActiveAsync(taskIds, ct);
            }

            // Like the host: unknown and ended tasks are not active; an active task on a device outside the group, null.
            if (call.HasAllDevices)
            {
                return Task.FromResult<bool?>(taskIds.Any(id => core.TaskActive(id) == true));
            }

            var outside = false;
            foreach (var id in taskIds)
            {
                if (core.TaskOf(id) is not { } task || core.TaskActive(id) != true)
                {
                    continue;
                }

                if (call.Contains(task.DeviceId))
                {
                    return Task.FromResult<bool?>(true);
                }

                outside = true;
            }

            return Task.FromResult<bool?>(outside ? null : false);
        }

        // Users with all devices: every task; a group user: only the tasks of its own calls.
        private bool Own(FakeCoreContext core, Guid taskId) =>
            call.HasAllDevices || (core.TaskOf(taskId) is { StartedBy: { } user } && string.Equals(user, call.UserName, StringComparison.OrdinalIgnoreCase));
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
