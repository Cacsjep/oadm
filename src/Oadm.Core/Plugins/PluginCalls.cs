using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Plugins;

/// <summary>
/// A plugin call (or plugin job) in progress, kept in <see cref="CallerContext.PluginCall"/> (docs/spec/device-scope.md,
/// Plugins): the full accesses of the plugin's context (<c>ctx.Devices</c>, <c>ctx.Vapix</c>, <c>ctx.Tasks</c>,
/// <c>ctx.EventStreams</c>, <c>ctx.System</c>) refuse while it is set for a user without all devices.
/// </summary>
/// <param name="PluginId">The core plugin whose method runs.</param>
/// <param name="Method">The method (or "job:" + kind).</param>
/// <param name="HasAllDevices">The user has all devices: nothing is refused.</param>
/// <param name="SystemAllowed">The method declared <see cref="ICorePlugin.SystemAccessReason"/>: <c>ctx.System</c> of this plugin works.</param>
public sealed record PluginCallMark(string PluginId, string Method, bool HasAllDevices, bool SystemAllowed)
{
    /// <summary>
    /// The caller's <see cref="ICallContext.DeviceScopeKey"/>: the only key <c>ctx.DeviceScopes</c> answers for in a group
    /// user's call (null: none).
    /// </summary>
    public string? ScopeKey { get; init; }
}

/// <summary>
/// The user's call of a plugin method as the server knows it: the authenticated caller and the caller's
/// <see cref="DeviceView"/>. The gRPC services make it (<c>new PluginCaller(AuthInterceptor.CallerOf(context), await
/// views.ForAsync(context))</c>) and hand it to <see cref="CorePluginHost"/>, which makes the plugin's
/// <see cref="ICallContext"/> from it.
/// </summary>
public sealed class PluginCaller
{
    public PluginCaller(AuthenticatedCaller caller, DeviceView view)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(view);
        if (view.IsSystem)
        {
            throw new ArgumentException("A user's call never gets the system view.", nameof(view));
        }

        if (view.HasAllDevices != caller.HasAllDevices)
        {
            throw new ArgumentException("The view does not belong to the caller.", nameof(view));
        }

        Caller = caller;
        View = view;
    }

    public AuthenticatedCaller Caller { get; }

    public DeviceView View { get; }

    public bool HasAllDevices => Caller.HasAllDevices;
}

/// <summary>
/// The <see cref="ICallContext"/> the host hands a plugin method: the user, the role's permissions and the devices of the
/// user's view. Device ids outside the view behave like unknown ids (<see cref="DevicesNotAvailableException"/>, nothing
/// sent). It holds the view explicitly, so it keeps working in a job after the call ended.
/// </summary>
internal sealed class HostCallContext : ICallContext
{
    /// <param name="viewWatcher">Ends a group user's event streams when their device leaves the view; without it a group user gets no event streams (fail closed).</param>
    public HostCallContext(PluginCaller caller, IVapixClientFactory vapix, ITaskRunner tasks, IDeviceEventStreams? eventStreams, DeviceViewWatcher? viewWatcher, CallTaskOwners taskOwners)
    {
        Caller = caller;
        var view = caller.View;
        Devices = new ViewDevices(view);
        Vapix = caller.HasAllDevices ? vapix : new ViewVapix(view, vapix);

        // The engine's runner of the view: the tasks belong to the caller (owner shown, queue check of the caller's view,
        // ITaskExecutionContext.Caller), also when a job starts them after the call ended.
        Tasks = tasks is Oadm.Core.Tasks.TaskEngine engine
            ? engine.ForView(view, Oadm.Core.Tasks.TaskCaller.Of(caller.Caller))
            : new ViewTasks(this, tasks, taskOwners);
        EventStreams = eventStreams is null ? null
            : caller.HasAllDevices ? eventStreams
            : viewWatcher is null ? null
            : new ViewedEventStreams(eventStreams, view, viewWatcher);
    }

    public PluginCaller Caller { get; }

    /// <summary>The user's id: the owner of jobs.</summary>
    public Guid UserId => Caller.Caller.UserId;

    public string UserName => Caller.Caller.UserName;

    public string SessionId => Caller.Caller.SessionId;

    public string DeviceScopeKey => Caller.View.ScopeKey;

    public bool HasAllDevices => Caller.HasAllDevices;

    public IDeviceRepository Devices { get; }

    public IVapixClientFactory Vapix { get; }

    public ITaskRunner Tasks { get; }

    public IDeviceEventStreams? EventStreams { get; }

    public bool Has(string permission) => Caller.Caller.Has(permission);

    public bool Contains(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Caller.View.Contains(device);
    }

    /// <summary>The device of the view, or <see cref="DevicesNotAvailableException"/> (unknown or outside: the same answer).</summary>
    internal static async Task RequireAsync(DeviceView view, Guid deviceId, CancellationToken ct)
    {
        if (await view.FindAsync(deviceId, ct).ConfigureAwait(false) is null)
        {
            throw new DevicesNotAvailableException();
        }
    }

    private sealed class ViewDevices(DeviceView view) : IDeviceRepository
    {
        public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
            [.. await view.ListAsync(ct).ConfigureAwait(false)];

        public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
            await view.FindAsync(id, ct).ConfigureAwait(false);
    }

    private sealed class ViewVapix(DeviceView view, IVapixClientFactory inner) : IVapixClientFactory
    {
        public async Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            await RequireAsync(view, deviceId, ct).ConfigureAwait(false);
            return await inner.CreateAsync(deviceId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The call's runner over another <see cref="ITaskRunner"/> than the engine (tests, tools).</summary>
    private sealed class ViewTasks(HostCallContext call, ITaskRunner inner, CallTaskOwners owners) : ITaskRunner
    {
        public async Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(deviceIds);

            // The whole run is refused when any device is outside the view (nothing starts in part). The owner is the caller,
            // never what the plugin says.
            _ = await call.Caller.View.GetAsync(deviceIds, ct).ConfigureAwait(false);
            var ids = await inner.RunAsync(pluginId, deviceIds, payloadJson, call.Caller.Caller.Owner, ct).ConfigureAwait(false);
            owners.Add(ids, call.UserId);
            return ids;
        }

        // Users with all devices may cancel every task, like the tasks pane; others only the tasks they started.
        public bool Cancel(Guid taskId) => (call.HasAllDevices || owners.IsOwner(taskId, call.UserId)) && inner.Cancel(taskId);

        public bool? IsActive(Guid taskId) => call.HasAllDevices || owners.IsOwner(taskId, call.UserId) ? inner.IsActive(taskId) : null;
    }
}

/// <summary>
/// Which user started a task through a call (<see cref="ICallContext.Tasks"/>): a user without all devices cancels and asks
/// about only those. Bounded (<see cref="Capacity"/>, oldest dropped).
/// </summary>
internal sealed class CallTaskOwners
{
    public const int Capacity = 50_000;

    private readonly ConcurrentDictionary<Guid, Guid> _owners = new();
    private readonly ConcurrentQueue<Guid> _order = new();

    public void Add(IReadOnlyList<Guid> taskIds, Guid userId)
    {
        foreach (var id in taskIds)
        {
            if (_owners.TryAdd(id, userId))
            {
                _order.Enqueue(id);
            }
        }

        while (_owners.Count > Capacity && _order.TryDequeue(out var oldest))
        {
            _owners.TryRemove(oldest, out _);
        }
    }

    public bool IsOwner(Guid taskId, Guid userId) => _owners.TryGetValue(taskId, out var owner) && owner == userId;
}

/// <summary>
/// The full accesses of a core plugin's context (<c>ctx.Devices</c>, <c>ctx.Vapix</c>, <c>ctx.Tasks</c>,
/// <c>ctx.EventStreams</c>, <c>ctx.System</c>): they work in background work and in the calls of users with all devices,
/// and refuse (fail closed, <see cref="DeviceAccessRefusedException"/>) during the call of a user with a device group, in
/// the work the plugin starts from it (<c>Task.Run</c>, timers keep the call) and in that user's jobs. <c>ctx.System</c>
/// works there only in the methods that declare <see cref="ICorePlugin.SystemAccessReason"/>.
/// </summary>
internal sealed partial class PluginCallGuard(string pluginId, ILogger logger)
{
    private readonly ConcurrentDictionary<(string Method, string Member), byte> _logged = new();

    public string PluginId { get; } = pluginId;

    /// <summary>True while a user without all devices calls (or owns the job that runs here).</summary>
    public static bool InGroupCall =>
        CallerContext.PluginCall is { HasAllDevices: false } || CallerContext.Current is { HasAllDevices: false };

    /// <summary>Throws <see cref="DeviceAccessRefusedException"/> in a group user's call (see the class).</summary>
    public void Check(string member, bool system = false)
    {
        if (!InGroupCall)
        {
            return;
        }

        var mark = CallerContext.PluginCall;
        if (system && mark is { SystemAllowed: true } && string.Equals(mark.PluginId, PluginId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw Refused(member, mark);
    }

    /// <summary>
    /// <see cref="DeviceAccessRefusedException"/> in a group user's call for every scope key other than the caller's own
    /// (<c>ctx.DeviceScopes</c>); methods that declare <see cref="ICorePlugin.SystemAccessReason"/> reach every scope, like
    /// <c>ctx.System</c>.
    /// </summary>
    public void CheckScope(string? scopeKey)
    {
        if (!InGroupCall)
        {
            return;
        }

        var mark = CallerContext.PluginCall;
        if (mark is { SystemAllowed: true } && string.Equals(mark.PluginId, PluginId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Every group identity on the call must be that key: the plugin call's (also in jobs) and the gRPC caller's.
        var own = (mark is not { HasAllDevices: false } || IsKey(mark.ScopeKey, scopeKey))
            && (CallerContext.Current is not { HasAllDevices: false } current || IsKey(current.DeviceGroupId?.ToString(), scopeKey));
        if (!own)
        {
            throw Refused("DeviceScopes", mark);
        }

        static bool IsKey(string? own, string? key) => own is not null && key is not null && string.Equals(own, key, StringComparison.OrdinalIgnoreCase);
    }

    private DeviceAccessRefusedException Refused(string member, PluginCallMark? mark)
    {
        var method = mark?.Method ?? "?";
        if (_logged.TryAdd((method, member), 0))
        {
            LogRefused(PluginId, member, method);
        }

        return new DeviceAccessRefusedException();
    }

    public IDeviceScopes? DeviceScopes(IDeviceScopes? inner) => inner is null ? null : new GuardedDeviceScopes(this, inner);

    private sealed class GuardedDeviceScopes(PluginCallGuard guard, IDeviceScopes inner) : IDeviceScopes
    {
        public event EventHandler<string>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public bool Exists(string scopeKey)
        {
            guard.CheckScope(scopeKey);
            return inner.Exists(scopeKey);
        }

        public bool Contains(string scopeKey, IDeviceInfo device)
        {
            guard.CheckScope(scopeKey);
            return inner.Contains(scopeKey, device);
        }

        // Checked when made and when read: a repository made in background work is refused in another group's call.
        public IDeviceRepository Devices(string scopeKey)
        {
            guard.CheckScope(scopeKey);
            return new ScopeRepository(guard, scopeKey, inner.Devices(scopeKey));
        }

        public string? NameOf(string scopeKey)
        {
            guard.CheckScope(scopeKey);
            return inner.NameOf(scopeKey);
        }

        // Not a scope read: whether any group covers a device the plugin already has (the certificate name rule).
        public bool InAnyGroup(IDeviceInfo device) => inner.InAnyGroup(device);
    }

    private sealed class ScopeRepository(PluginCallGuard guard, string scopeKey, IDeviceRepository inner) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
        {
            guard.CheckScope(scopeKey);
            return inner.ListAsync(ct);
        }

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
        {
            guard.CheckScope(scopeKey);
            return inner.FindAsync(id, ct);
        }
    }

    public IDeviceRepository Devices(IDeviceRepository inner, bool system = false) => new GuardedDevices(this, inner, system);

    public IVapixClientFactory Vapix(IVapixClientFactory inner, bool system = false) => new GuardedVapix(this, inner, system);

    public ITaskRunner Tasks(ITaskRunner inner, bool system = false) => new GuardedTasks(this, inner, system);

    public IDeviceEventStreams? EventStreams(IDeviceEventStreams? inner, bool system = false) =>
        inner is null ? null : new GuardedEventStreams(this, inner, system);

    public IDeviceAutoAdd? AutoAdd(IDeviceAutoAdd? inner) => inner is null ? null : new GuardedAutoAdd(this, inner);

    private sealed class GuardedAutoAdd(PluginCallGuard guard, IDeviceAutoAdd inner) : IDeviceAutoAdd
    {
        public Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct)
        {
            guard.Check("AutoAdd");
            return inner.AddAsync(address, expectedSerial, source, ct);
        }

        public Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct)
        {
            guard.Check("AutoAdd");
            return inner.FollowAsync(serial, address, source, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Core plugin {PluginId} used ctx.{Member} in {Method} for a user with a device group: refused (use the call context)")]
    private partial void LogRefused(string pluginId, string member, string method);

    private sealed class GuardedDevices(PluginCallGuard guard, IDeviceRepository inner, bool system) : IDeviceRepository
    {
        private string Member => system ? "System.Devices" : "Devices";

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.ListAsync(ct);
        }

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.FindAsync(id, ct);
        }
    }

    private sealed class GuardedVapix(PluginCallGuard guard, IVapixClientFactory inner, bool system) : IVapixClientFactory
    {
        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            guard.Check(system ? "System.Vapix" : "Vapix", system);
            return inner.CreateAsync(deviceId, ct);
        }
    }

    private sealed class GuardedTasks(PluginCallGuard guard, ITaskRunner inner, bool system) : ITaskRunner
    {
        private string Member => system ? "System.Tasks" : "Tasks";

        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.RunAsync(pluginId, deviceIds, payloadJson, owner, ct);
        }

        public bool Cancel(Guid taskId)
        {
            guard.Check(Member, system);
            return inner.Cancel(taskId);
        }

        public bool? IsActive(Guid taskId)
        {
            guard.Check(Member, system);
            return inner.IsActive(taskId);
        }
    }

    private sealed class GuardedEventStreams(PluginCallGuard guard, IDeviceEventStreams inner, bool system) : IDeviceEventStreams
    {
        private string Member => system ? "System.EventStreams" : "EventStreams";

        public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.OpenAsync(deviceId, ct);
        }

        public Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.GetRtspPortAsync(deviceId, ct);
        }

        public Task SetRtspPortAsync(Guid deviceId, int? port, CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.SetRtspPortAsync(deviceId, port, ct);
        }

        public Task<IReadOnlyDictionary<Guid, int>> GetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.GetRtspPortsAsync(deviceIds, ct);
        }

        public Task SetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, int? port, IReadOnlyCollection<Guid>? managed, CancellationToken ct)
        {
            guard.Check(Member, system);
            return inner.SetRtspPortsAsync(deviceIds, port, managed, ct);
        }
    }
}

/// <summary><see cref="ICorePluginContext.System"/> of the host: every device, guarded like the context (see <see cref="PluginCallGuard"/>).</summary>
internal sealed class HostSystemAccess(PluginCallGuard guard, IDeviceRepository devices, IVapixClientFactory vapix, ITaskRunner tasks, IDeviceEventStreams? eventStreams) : ISystemAccess
{
    public IDeviceRepository Devices { get; } = guard.Devices(devices, system: true);

    public IVapixClientFactory Vapix { get; } = guard.Vapix(vapix, system: true);

    public ITaskRunner Tasks { get; } = guard.Tasks(tasks, system: true);

    public IDeviceEventStreams? EventStreams { get; } = guard.EventStreams(eventStreams, system: true);
}

/// <summary>
/// The host's rules for a user's call of plugin code (docs/spec/device-scope.md, Plugins), shared by the core plugin host
/// and the task plugin queries: a user with a device group may call only packages built for the call context, and gets an
/// <see cref="ICallContext"/> scoped to the user's view.
/// </summary>
public static class PluginCallRules
{
    /// <summary>True for a user with all devices, or for a package whose <c>minSdkVersion</c> is at least 0.4.0.</summary>
    public static bool IsCallable(PluginCaller caller, PluginOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(origin);
        return caller.HasAllDevices || origin.SupportsCallContext;
    }

    /// <summary>
    /// <see cref="DeviceAccessRefusedException"/> ("Only users with all devices can use this plugin.") unless
    /// <see cref="IsCallable"/>.
    /// </summary>
    public static void RequireCallable(PluginCaller caller, PluginOrigin origin)
    {
        if (!IsCallable(caller, origin))
        {
            throw new DeviceAccessRefusedException(DeviceAccessRefusedException.PluginNeedsAllDevices);
        }
    }

    /// <summary>
    /// The plugin's call for <paramref name="caller"/> over the given services (unguarded, site rules applied): devices, VAPIX
    /// clients, task runs and event streams only of the caller's view. For task plugin queries (no task runs: pass a runner
    /// that refuses) and other host code that calls plugins for a user.
    /// </summary>
    public static ICallContext CreateCall(PluginCaller caller, IVapixClientFactory vapix, ITaskRunner tasks, IDeviceEventStreams? eventStreams, DeviceViewWatcher? viewWatcher = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(tasks);
        return new HostCallContext(caller, vapix, tasks, eventStreams, viewWatcher, new CallTaskOwners());
    }

    /// <summary>The mark of a user's call of <paramref name="pluginId"/> (see <see cref="CallerContext.UsePluginCall"/>).</summary>
    public static PluginCallMark MarkOf(PluginCaller caller, string pluginId, string method)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new PluginCallMark(pluginId, method, caller.HasAllDevices, SystemAllowed: false) { ScopeKey = caller.View.ScopeKey };
    }
}
