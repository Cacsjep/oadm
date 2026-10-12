using System.Net;

using Grpc.Core;
using Grpc.Core.Interceptors;

using Oadm.Contracts.Security;
using Oadm.Core.Auth;
using Oadm.Sdk.Plugins;

namespace Oadm.Server.Auth;

/// <summary>
/// Checks every gRPC call: a valid bearer token (UNAUTHENTICATED without), the rule of <see cref="AccessPolicy"/> against
/// the permissions of the caller's role (PERMISSION_DENIED with what is missing), then runs the call with the caller in
/// <see cref="CallerContext"/> (task owner, audit log, the checks inside plugin and task calls). Streaming calls are
/// registered in <see cref="CallerStreams"/>, which ends them when a role change no longer allows them.
/// </summary>
public sealed partial class AuthInterceptor(AuthTokenStore tokens, RoleStore roles, CallerStreams streams, ILogger<AuthInterceptor> logger) : Interceptor
{
    /// <summary>The session stream reports role changes itself; it is never ended by one.</summary>
    private const string WatchSessionMethod = "/oadm.v1.AuthService/WatchSession";

    public const string NotLoggedInMessage = "Log in to the server first.";
    public const string AdminOnlyMessage = "Only administrators can do this.";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, _) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, rule) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        using var stream = OpenStream(caller, rule, context);
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, rule) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        using var stream = OpenStream(caller, rule, context);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, rule) = await AuthorizeAsync(context).ConfigureAwait(false);
        using var scope = CallerContext.Use(caller);
        using var stream = OpenStream(caller, rule, context);
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
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
    /// <paramref name="permission"/>. Without a caller (in-process calls that set none) it allows.
    /// </summary>
    public static void Require(string permission, PermissionCatalog catalog)
    {
        if (CallerContext.Current is { } caller && !caller.Has(permission))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, AccessPolicy.DeniedMessage(permission, catalog)));
        }
    }

    /// <summary>Like <see cref="Require(string, PermissionCatalog)"/> for what only the Administrator role may do.</summary>
    public static void RequireAdmin()
    {
        if (CallerContext.Current is { IsAdmin: false })
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, AdminOnlyMessage));
        }
    }

    private IDisposable? OpenStream(AuthenticatedCaller? caller, AccessRule rule, ServerCallContext context)
    {
        if (caller is null || context.Method == WatchSessionMethod)
        {
            return null;
        }

        var http = context.GetHttpContext();
        return streams.Open(caller, rule, context.Method, http.Abort);
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

        var session = await tokens.ValidateAsync(token, context.CancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Your session has ended. Log in again."));
        }

        var permissions = await roles.PermissionsOfAsync(session.RoleId, context.CancellationToken).ConfigureAwait(false);
        var caller = new AuthenticatedCaller(session.UserId, session.UserName, session.RoleId, permissions, ClientAddressOf(context),
            CleanMachineName(context.RequestHeaders.GetValue(OadmMetadata.ClientMachine)), session.TokenHash);
        if (!AccessPolicy.Allows(rule, caller))
        {
            LogDenied(caller.UserName, context.Method);
            throw new RpcException(new Status(StatusCode.PermissionDenied, AccessPolicy.DeniedMessage(rule, roles.Catalog)));
        }

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
