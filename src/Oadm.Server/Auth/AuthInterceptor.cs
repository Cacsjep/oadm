using System.Net;

using Grpc.Core;
using Grpc.Core.Interceptors;

using Oadm.Contracts.Security;
using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Devices;

namespace Oadm.Server.Auth;

/// <summary>
/// Checks every gRPC call: a valid bearer token (UNAUTHENTICATED without), the rule of <see cref="AccessPolicy"/> against
/// the permissions of the caller's role and, for <see cref="AccessRule.AllDevices"/>, the caller's devices
/// (PERMISSION_DENIED with what is missing), then runs the call with the caller in the call's
/// <see cref="ServerCallContext.UserState"/> (<see cref="CallerOf"/>, the explicit way) and in <see cref="CallerContext"/>
/// (the backstop). Streaming calls are registered in <see cref="CallerStreams"/>, which ends them when a role or device
/// change no longer allows them. A <see cref="DevicesNotAvailableException"/> of any service answers NOT_FOUND with its
/// message ("3 devices are no longer available."), a <see cref="Sdk.Plugins.DeviceAccessRefusedException"/> (plugin code a
/// user with a device group may not reach) PERMISSION_DENIED.
/// </summary>
public sealed partial class AuthInterceptor(AuthTokenStore tokens, RoleStore roles, CallerStreams streams, DeviceViewFactory views, AccessEpochs epochs, ILogger<AuthInterceptor> logger) : Interceptor
{
    /// <summary>The session stream reports role changes itself; it is never ended by one.</summary>
    private const string WatchSessionMethod = "/oadm.v1.AuthService/WatchSession";

    /// <summary>The key of the caller in <see cref="ServerCallContext.UserState"/>.</summary>
    internal const string CallerKey = "oadm.caller";

    public const string NotLoggedInMessage = "Log in to the server first.";
    public const string AdminOnlyMessage = "Only administrators can do this.";

    /// <summary>A stream whose devices changed between its check and its registration: the client connects again.</summary>
    public const string DevicesChangedMessage = DeviceGroupTexts.DevicesChanged;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, _) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException ex)
        {
            throw NotAvailable(ex);
        }
        catch (Sdk.Plugins.DeviceAccessRefusedException ex)
        {
            throw Refused(ex);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, rule) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        using var stream = await OpenStreamAsync(caller, rule, context).ConfigureAwait(false);
        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException ex)
        {
            throw NotAvailable(ex);
        }
        catch (Sdk.Plugins.DeviceAccessRefusedException ex)
        {
            throw Refused(ex);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, rule) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        using var stream = await OpenStreamAsync(caller, rule, context).ConfigureAwait(false);
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException ex)
        {
            throw NotAvailable(ex);
        }
        catch (Sdk.Plugins.DeviceAccessRefusedException ex)
        {
            throw Refused(ex);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, rule) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        using var stream = await OpenStreamAsync(caller, rule, context).ConfigureAwait(false);
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException ex)
        {
            throw NotAvailable(ex);
        }
        catch (Sdk.Plugins.DeviceAccessRefusedException ex)
        {
            throw Refused(ex);
        }
    }

    /// <summary>NOT_FOUND with the shared text: the answer for unknown devices and for devices outside the caller's view.</summary>
    public static RpcException NotAvailable(DevicesNotAvailableException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new RpcException(new Status(StatusCode.NotFound, ex.Message));
    }

    /// <summary>PERMISSION_DENIED with the refusal's text: plugin code a user with a device group may not reach.</summary>
    public static RpcException Refused(Sdk.Plugins.DeviceAccessRefusedException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
    }

    /// <summary>
    /// The authenticated caller of <paramref name="context"/> (set by this interceptor in the call's user state), else
    /// UNAUTHENTICATED. The explicit way for services: never read the caller from <see cref="CallerContext"/>.
    /// </summary>
    public static AuthenticatedCaller CallerOf(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.UserState.TryGetValue(CallerKey, out var value) && value is AuthenticatedCaller caller
            ? caller
            : throw new RpcException(new Status(StatusCode.Unauthenticated, NotLoggedInMessage));
    }

    /// <summary>Remote address of the call ("local" for the in-process test server).</summary>
    public static string ClientAddressOf(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ip = context.GetHttpContext().Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "local";
        }

        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    /// <summary>True for a client on the server computer (loopback address; the in-process test server has none).</summary>
    public static bool IsLoopback(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ip = context.GetHttpContext().Connection.RemoteIpAddress;
        return ip is null || IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    }

    /// <summary>The caller of an authenticated call (set by this interceptor), else UNAUTHENTICATED.</summary>
    public static AuthenticatedCaller RequireCaller() =>
        CallerContext.Current ?? throw new RpcException(new Status(StatusCode.Unauthenticated, NotLoggedInMessage));

    /// <summary>
    /// The check inside a call (<see cref="Access.InCall"/>): PERMISSION_DENIED with what is missing unless the caller has
    /// <paramref name="permission"/>. Without a caller UNAUTHENTICATED (fail closed: never "no caller = allow").
    /// </summary>
    public static void Require(string permission, PermissionCatalog catalog)
    {
        if (!RequireCaller().Has(permission))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, AccessPolicy.DeniedMessage(permission, catalog)));
        }
    }

    /// <summary>Like <see cref="Require(string, PermissionCatalog)"/> for what only the Administrator role may do.</summary>
    public static void RequireAdmin()
    {
        if (!RequireCaller().IsAdmin)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, AdminOnlyMessage));
        }
    }

    /// <summary>
    /// PERMISSION_DENIED "Only users with all devices can do this." unless <paramref name="caller"/> has all devices (the
    /// check inside a call whose rule depends on the request, e.g. a plugin method the plugin declares).
    /// </summary>
    public static void RequireAllDevices(AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.HasAllDevices)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, DeviceGroupTexts.AllDevicesOnly));
        }
    }

    /// <summary>
    /// Registers the stream, then checks once more: a role change, a change of the caller's devices (another group, new
    /// rules of the group) or a logout between the check and the registration would otherwise leave the stream open with
    /// the old rights.
    /// </summary>
    private async Task<IDisposable?> OpenStreamAsync(AuthenticatedCaller? caller, AccessRule rule, ServerCallContext context)
    {
        if (caller is null)
        {
            return null;
        }

        var http = context.GetHttpContext();
        var registration = streams.Open(caller, rule, context.Method, http.Abort);
        try
        {
            var session = await tokens.ValidateHashAsync(caller.TokenHash, context.CancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Your session has ended. Log in again."));
            }

            var permissions = await roles.PermissionsOfAsync(session.RoleId, context.CancellationToken).ConfigureAwait(false);
            var devices = await views.CallerDevicesAsync(session.DeviceAccess, context.CancellationToken).ConfigureAwait(false);
            var now = caller with { RoleId = session.RoleId, Permissions = permissions, Devices = devices };
            if (context.Method != WatchSessionMethod)
            {
                if (!AccessPolicy.Allows(rule, now))
                {
                    throw new RpcException(new Status(StatusCode.PermissionDenied, AccessPolicy.DeniedMessage(rule, roles.Catalog, now)));
                }

                if (devices != caller.Devices)
                {
                    throw new RpcException(new Status(StatusCode.Unavailable, DevicesChangedMessage));
                }
            }

            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    private async Task<(AuthenticatedCaller? Caller, AccessRule Rule)> AuthorizeAsync(ServerCallContext context)
    {
        var rule = AccessPolicy.For(context.Method);
        if (rule.Access == Access.Open)
        {
            return (null, rule);
        }

        var header = context.RequestHeaders.GetValue(OadmMetadata.Authorization);
        string? token = header is not null && header.StartsWith(OadmMetadata.BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[OadmMetadata.BearerPrefix.Length..].Trim()
            : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, NotLoggedInMessage));
        }

        var epoch = epochs.Capture(); // before the session is read: a change after it refuses the call's jobs
        var session = await tokens.ValidateAsync(token, context.CancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Your session has ended. Log in again."));
        }

        var permissions = await roles.PermissionsOfAsync(session.RoleId, context.CancellationToken).ConfigureAwait(false);
        var devices = await views.CallerDevicesAsync(session.DeviceAccess, context.CancellationToken).ConfigureAwait(false);
        var caller = new AuthenticatedCaller(session.UserId, session.UserName, session.RoleId, permissions, ClientAddressOf(context),
            CleanMachineName(context.RequestHeaders.GetValue(OadmMetadata.ClientMachine)), session.TokenHash)
        {
            Devices = devices,
            AccessEpoch = epoch,
        };
        if (!AccessPolicy.Allows(rule, caller))
        {
            LogDenied(caller.UserName, context.Method);
            throw new RpcException(new Status(StatusCode.PermissionDenied, AccessPolicy.DeniedMessage(rule, roles.Catalog, caller)));
        }

        context.UserState[CallerKey] = caller;
        return (caller, rule);
    }

    /// <summary>Letters, digits, '-', '_' and '.', at most 64 characters (it ends up in the task owner).</summary>
    internal static string CleanMachineName(string? value)
    {
        var clean = new string((value ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(64).ToArray());
        return clean;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission denied: {UserName} called {Method}")]
    private partial void LogDenied(string userName, string method);
}
