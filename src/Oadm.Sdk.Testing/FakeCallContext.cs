using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
/// tests of a plugin. The call's <see cref="Tasks"/> answer like the host, also over a test's own runner: unknown and ended
/// tasks are not active (false); a group user cancels only the tasks that user started and gets null from
/// <see cref="ITaskRunner.IsActive"/> for another user's active task; <see cref="ITaskRunner.AnyActiveAsync"/> answers for the
/// tasks of the group's devices, whoever started them (null for an active task outside the group). Unlike the host, a
/// cancelled task of a <see cref="FakeCoreContext"/> ends at once; on the server it stays active until its run stops.
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
    /// devices outside the group are refused before the fakes are asked. Task cancels and questions are filtered like the
    /// host's: the user's own tasks are those its calls started over the same runner; a task the fake cannot place on a
    /// device counts as outside the group.
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
        public async Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(deviceIds);
            call.RequireAll(deviceIds);
            if (call._core is { } core)
            {
                return await core.Run(pluginId, deviceIds, payloadJson, owner, call.UserName).ConfigureAwait(false);
            }

            var runner = call._tasks ?? throw Missing("task runner");
            var ids = await runner.RunAsync(pluginId, deviceIds, payloadJson, owner, ct).ConfigureAwait(false);
            var started = StartedOver(runner);
            for (var i = 0; i < ids.Count && i < deviceIds.Count; i++)
            {
                started[ids[i]] = new StartedTask(deviceIds[i], call.UserName);
            }

            return ids;
        }

        // Like the host (TaskEngine.Cancel / CancelOwn): users with all devices cancel every task, a group user only its own.
        public bool Cancel(Guid taskId) =>
            call._core is { } core
                ? Own(taskId) && core.EndTask(taskId)
                : Own(taskId) && Runner.Cancel(taskId);

        // Like the host (TaskEngine.IsActive / IsActiveOwn): unknown and ended tasks are not active (false); a group user
        // learns nothing about another user's active task (null).
        public bool? IsActive(Guid taskId)
        {
            var active = call._core is { } core ? core.TaskActive(taskId) : Runner.IsActive(taskId);
            return call.HasAllDevices || Own(taskId) || active == false ? active : null;
        }

        public Task<bool?> AnyActiveAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(taskIds);
            if (call._core is null && call.HasAllDevices)
            {
                return Runner.AnyActiveAsync(taskIds, ct);
            }

            // Like the host: unknown and ended tasks are not active; a group user asks about the tasks of the group's devices,
            // whoever started them; an active task on a device outside the group (or one the fake cannot place), null.
            var outside = false;
            foreach (var id in taskIds)
            {
                var active = call._core is { } core ? core.TaskActive(id) : Runner.IsActive(id);
                if (active == false)
                {
                    continue;
                }

                if (active == true && (call.HasAllDevices || (DeviceOf(id) is { } device && call.Contains(device))))
                {
                    return Task.FromResult<bool?>(true);
                }

                outside = true;
            }

            return Task.FromResult<bool?>(outside ? null : false);
        }

        private ITaskRunner Runner => call._tasks ?? throw Missing("task runner");

        private Guid? DeviceOf(Guid taskId) =>
            call._core is { } core ? core.TaskOf(taskId)?.DeviceId
            : StartedOver(Runner).TryGetValue(taskId, out var task) ? task.DeviceId
            : null;

        // Users with all devices: every task; a group user: only the tasks its calls started (by user name, like the host's user id).
        private bool Own(Guid taskId)
        {
            if (call.HasAllDevices)
            {
                return true;
            }

            var user = call._core is { } core ? core.TaskOf(taskId)?.StartedBy
                : StartedOver(Runner).TryGetValue(taskId, out var task) ? task.StartedBy
                : null;
            return user is not null && string.Equals(user, call.UserName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The tasks calls started over a test's own runner (<see cref="Over"/>, <see cref="OverGroup"/>), shared by every call over it.</summary>
    private static ConcurrentDictionary<Guid, StartedTask> StartedOver(ITaskRunner runner) => StartedByRunner.GetValue(runner, _ => new());

    private static readonly ConditionalWeakTable<ITaskRunner, ConcurrentDictionary<Guid, StartedTask>> StartedByRunner = new();

    private sealed record StartedTask(Guid DeviceId, string StartedBy);

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
