using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>
/// The caller's devices at the start of a call: the stored access plus the revision of the group's rules at that time
/// (streams compare it right after they registered, like the role).
/// </summary>
/// <param name="Access">What is stored for the user.</param>
/// <param name="GroupRevision">The revision of the group's rules (0 without a group or for an unknown group).</param>
public sealed record CallerDevices(DeviceScope Access, long GroupRevision)
{
    /// <summary>No device: the default of a caller that was built without its devices (fail closed).</summary>
    public static CallerDevices None { get; } = new(DeviceScope.None, 0);

    /// <summary>Every device.</summary>
    public static CallerDevices All { get; } = new(DeviceScope.All, 0);
}

/// <summary>The authenticated user of the current gRPC call.</summary>
/// <param name="ClientAddress">Remote IP address of the client.</param>
/// <param name="ClientMachine">Machine name the client sent (oadm-client-machine), shortened and cleaned.</param>
/// <param name="TokenHash">SHA-256 of the session token (Logout revokes it).</param>
/// <param name="Permissions">What the caller's role allows, at the start of the call.</param>
public sealed record AuthenticatedCaller(Guid UserId, string UserName, Guid RoleId, PermissionSet Permissions, string ClientAddress, string ClientMachine, string TokenHash)
{
    /// <summary>Task owner: "&lt;user&gt;@&lt;client machine&gt;" (the user is authenticated, the machine is what the client says).</summary>
    public string Owner => string.IsNullOrEmpty(ClientMachine) ? UserName : UserName + "@" + ClientMachine;

    /// <summary>The caller has the Administrator role (every permission, the Administrator-only areas).</summary>
    public bool IsAdmin => BuiltInRoles.IsAdministrator(RoleId);

    /// <summary>Admin for the Administrator role, Operator for every other.</summary>
    public UserRole Role => IsAdmin ? UserRole.Admin : UserRole.Operator;

    /// <summary>
    /// The caller's devices (set by the auth interceptor). The default is <see cref="CallerDevices.None"/>: a caller built
    /// without them reaches no device (fail closed).
    /// </summary>
    public CallerDevices Devices { get; init; } = CallerDevices.None;

    /// <summary>
    /// <see cref="AccessEpochs.Capture"/> before the interceptor read the session: jobs started in this call are refused when
    /// the user's access changed after it. 0 (a caller built elsewhere) counts as the oldest (fail closed).
    /// </summary>
    public long AccessEpoch { get; init; }

    /// <summary>The caller may reach every device: the Administrator role, or a user with "All devices".</summary>
    public bool HasAllDevices => IsAdmin || Devices.Access.AllDevices;

    /// <summary>The caller's device group, null for callers with all devices (and for a user without devices).</summary>
    public Guid? DeviceGroupId => HasAllDevices ? null : Devices.Access.GroupId;

    /// <summary>An opaque id of the caller's session for plugins and jobs (a hash of the token hash, never the token).</summary>
    public string SessionId => SessionIds.Of(TokenHash);

    public bool Has(string permission) => Permissions.Has(permission);
}

/// <summary>Opaque session ids for plugins (<see cref="Oadm.Sdk.Plugins.ICallContext.SessionId"/>): never the token or its stored hash.</summary>
public static class SessionIds
{
    /// <summary>The first 16 bytes of SHA-256("oadm session" + token hash), lower-case hex.</summary>
    public static string Of(string tokenHash)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("oadm session " + (tokenHash ?? string.Empty)));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }
}

/// <summary>
/// The caller of the current call, set by the server's auth interceptor for the duration of the call. A backstop only
/// (docs/spec/device-scope.md, Enforcement): services take the caller from the call explicitly (<c>ServerCallContext</c>)
/// and get their devices from a <c>DeviceView</c>. Null outside calls and in background work: the host clears it where it
/// starts task runs, plugin jobs, timers and the handlers of its events (<see cref="Suppress"/>), so a user never flows into
/// work that outlives the call.
/// </summary>
public static class CallerContext
{
    private static readonly AsyncLocal<AuthenticatedCaller?> CurrentCaller = new();

    private static readonly AsyncLocal<Plugins.PluginCallMark?> CurrentPluginCall = new();

    public static AuthenticatedCaller? Current
    {
        get => CurrentCaller.Value;
        set => CurrentCaller.Value = value;
    }

    /// <summary>
    /// The plugin call (or plugin job) running here: the core plugin host sets it around a user's call of a plugin method and
    /// for the jobs it starts, so <c>ctx.Devices</c> and the other full accesses refuse in a group user's call and in every
    /// task the plugin starts from it (<c>Task.Run</c> keeps it). <see cref="Suppress"/> clears it like the caller.
    /// </summary>
    public static Plugins.PluginCallMark? PluginCall => CurrentPluginCall.Value;

    /// <summary>Sets <paramref name="call"/> until the returned scope is disposed (the core plugin host).</summary>
    public static IDisposable UsePluginCall(Plugins.PluginCallMark? call)
    {
        var previous = CurrentPluginCall.Value;
        CurrentPluginCall.Value = call;
        return new PluginCallScope(previous);
    }

    /// <summary>Sets <paramref name="caller"/> until the returned scope is disposed (tests, background calls on behalf of a user).</summary>
    public static IDisposable Use(AuthenticatedCaller? caller)
    {
        var previous = CurrentCaller.Value;
        CurrentCaller.Value = caller;
        return new Scope(previous);
    }

    /// <summary>
    /// The background boundary: no caller until the returned scope is disposed. Work started inside the scope
    /// (<c>Task.Run</c>, timers, event handlers that start work) captures no caller. Use it around event raises and where
    /// background work starts.
    /// </summary>
    public static IDisposable Suppress()
    {
        var caller = Use(null);
        var plugin = UsePluginCall(null);
        return new Both(plugin, caller);
    }

    /// <summary>Starts <paramref name="work"/> on the thread pool without the caller (the background boundary).</summary>
    public static Task RunDetached(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using (Suppress())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }

    /// <summary>Runs <paramref name="create"/> without the caller, e.g. a timer created during a call (its callbacks see no user).</summary>
    public static T Detached<T>(Func<T> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        using (Suppress())
        {
            return create();
        }
    }

    /// <summary>Raises an event without the caller: the handlers and the work they start never see the user.</summary>
    public static void Raise<T>(EventHandler<T>? handler, object? sender, T args)
    {
        if (handler is null)
        {
            return;
        }

        using (Suppress())
        {
            handler(sender, args);
        }
    }

    /// <inheritdoc cref="Raise{T}(EventHandler{T}?, object?, T)"/>
    public static void Raise(EventHandler? handler, object? sender, EventArgs args)
    {
        if (handler is null)
        {
            return;
        }

        using (Suppress())
        {
            handler(sender, args);
        }
    }

    private sealed class Scope(AuthenticatedCaller? previous) : IDisposable
    {
        public void Dispose() => CurrentCaller.Value = previous;
    }

    private sealed class PluginCallScope(Plugins.PluginCallMark? previous) : IDisposable
    {
        public void Dispose() => CurrentPluginCall.Value = previous;
    }

    private sealed class Both(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }
}
