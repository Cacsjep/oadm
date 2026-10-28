using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tasks;

/// <summary>
/// The user who started a task (docs/spec/device-scope.md, Tasks), kept in memory with the task, never persisted: a queued
/// task checks the user's current view again when it leaves the queue, and the plugin gets the user's call
/// (<see cref="ITaskExecutionContext.Caller"/>).
/// </summary>
/// <param name="UserId">The user's id (the current view is read for it).</param>
/// <param name="UserName">The user's name.</param>
/// <param name="Owner">The task owner shown in the task list ("user@machine").</param>
/// <param name="SessionId">The opaque session id (<see cref="ICallContext.SessionId"/>).</param>
/// <param name="Permissions">What the user's role allowed when the task was started.</param>
public sealed record TaskCaller(Guid UserId, string UserName, string Owner, string SessionId, PermissionSet Permissions)
{
    /// <summary>The caller of a gRPC call.</summary>
    public static TaskCaller Of(AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new(caller.UserId, caller.UserName, caller.Owner, caller.SessionId, caller.Permissions);
    }
}

/// <summary>
/// The user's call for task plugins (SDK 0.4.0 <see cref="ICallContext"/>): the task dialog's queries and
/// <see cref="ITaskExecutionContext.Caller"/> of a task run. Devices, VAPIX clients, task runs and event streams only for
/// devices of the user's view; an id outside it is answered like an unknown id (<see cref="DevicesNotAvailableException"/>,
/// <see cref="IDeviceRepository.FindAsync"/> null) and nothing runs. A small implementation for the task side; the plugin
/// host has its own for page calls.
/// </summary>
public sealed class TaskCallContext : ICallContext
{
    private readonly TaskCaller _caller;
    private readonly DeviceView _view;

    public TaskCallContext(TaskCaller caller, DeviceView view, IVapixClientFactory vapix, ITaskRunner tasks, IDeviceEventStreams? eventStreams = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(tasks);
        if (view.IsSystem)
        {
            throw new ArgumentException("A user's call never has the system view.", nameof(view));
        }

        _caller = caller;
        _view = view;
        Devices = new ViewDevices(view);
        Vapix = new ViewVapix(view, vapix);
        Tasks = tasks;
        EventStreams = eventStreams is null ? null : new ViewEventStreams(view, eventStreams);
    }

    /// <summary>The view the call reaches.</summary>
    public DeviceView View => _view;

    public string UserName => _caller.UserName;

    public string SessionId => _caller.SessionId;

    public string DeviceScopeKey => _view.ScopeKey;

    public bool HasAllDevices => _view.HasAllDevices;

    public bool CoversRoute(Guid? siteId) => _view.CoversRoute(siteId);

    public IDeviceRepository Devices { get; }

    public IVapixClientFactory Vapix { get; }

    public ITaskRunner Tasks { get; }

    public IDeviceEventStreams? EventStreams { get; }

    public bool Has(string permission) => _caller.Permissions.Has(permission);

    public bool Contains(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _view.Contains(device);
    }

    private sealed class ViewDevices(DeviceView view) : IDeviceRepository
    {
        public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => [.. await view.ListAsync(ct).ConfigureAwait(false)];

        public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => await view.FindAsync(id, ct).ConfigureAwait(false);
    }

    private sealed class ViewVapix(DeviceView view, IVapixClientFactory inner) : IVapixClientFactory
    {
        public async Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            await view.GetAsync(deviceId, ct).ConfigureAwait(false); // outside the view: refused before any request
            return await inner.CreateAsync(deviceId, ct).ConfigureAwait(false);
        }
    }

    private sealed class ViewEventStreams(DeviceView view, IDeviceEventStreams inner) : IDeviceEventStreams
    {
        public async Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            await view.GetAsync(deviceId, ct).ConfigureAwait(false);
            return await inner.OpenAsync(deviceId, ct).ConfigureAwait(false);
        }

        public async Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct)
        {
            await view.GetAsync(deviceId, ct).ConfigureAwait(false);
            return await inner.GetRtspPortAsync(deviceId, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Task runs for one user's view (<see cref="TaskEngine.ForView"/>, <c>call.Tasks</c> of the user's call): the whole run is
/// refused when any device is outside the view or unknown; for a user with a device group, cancel and the active check
/// reach only the user's own tasks.
/// </summary>
internal sealed class ViewTaskRunner(TaskEngine engine, DeviceView view, TaskCaller caller) : ITaskRunner
{
    public async Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var devices = await view.GetAsync(deviceIds, ct).ConfigureAwait(false);
        return await engine.RunAsync(pluginId, devices, payloadJson, caller, ct).ConfigureAwait(false);
    }

    public bool Cancel(Guid taskId) => view.HasAllDevices ? engine.Cancel(taskId) : engine.CancelOwn(taskId, caller.UserId);

    public bool? IsActive(Guid taskId) => view.HasAllDevices ? engine.IsActive(taskId) : engine.IsActiveOwn(taskId, caller.UserId);

    /// <summary>
    /// For a user with a device group: the tasks of devices in the view, whoever started them (a run of the Action Rule
    /// Editor may hold tasks of several users of the same group); an active task on a device outside the view: null.
    /// </summary>
    public async Task<bool?> AnyActiveAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (view.HasAllDevices)
        {
            return taskIds.Any(id => engine.IsActive(id) == true);
        }

        IReadOnlySet<Guid>? ids = null;
        var outside = false;
        foreach (var taskId in taskIds)
        {
            if (!engine.TryGetActiveDevice(taskId, out var deviceId))
            {
                continue;
            }

            ids ??= await view.IdsAsync(ct).ConfigureAwait(false);
            if (ids.Contains(deviceId))
            {
                return true;
            }

            outside = true;
        }

        return outside ? null : false;
    }

    /// <summary>For a user with a device group: the tasks of devices in the view (like <see cref="AnyActiveAsync"/>), else null.</summary>
    public async Task<TaskOutcome?> GetOutcomeAsync(Guid taskId, CancellationToken ct)
    {
        if (view.HasAllDevices)
        {
            return await engine.GetOutcomeAsync(taskId, ct).ConfigureAwait(false);
        }

        return engine.IsActive(taskId) == true ? null : TaskEngine.OutcomeOf(await engine.GetAsync(view, taskId, ct).ConfigureAwait(false));
    }
}
