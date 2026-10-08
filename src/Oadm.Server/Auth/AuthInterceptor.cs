using System.Net;

using Grpc.Core;
using Grpc.Core.Interceptors;

using Oadm.Contracts.Security;
using Oadm.Core.Auth;
using Oadm.Sdk.Plugins;

namespace Oadm.Server.Auth;

/// <summary>
/// Checks every gRPC call: a valid bearer token (UNAUTHENTICATED without), the role of <see cref="AccessPolicy"/>
/// (PERMISSION_DENIED), then runs the call with the caller in <see cref="CallerContext"/> (task owner, audit log).
/// </summary>
public sealed partial class AuthInterceptor(AuthTokenStore tokens, ILogger<AuthInterceptor> logger) : Interceptor
{
    public const string NotLoggedInMessage = "Log in to the server first.";
    public const string AdminOnlyMessage = "Only administrators can do this.";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        using var scope = CallerContext.Use(await AuthorizeAsync(context).ConfigureAwait(false));
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        using var scope = CallerContext.Use(await AuthorizeAsync(context).ConfigureAwait(false));
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        using var scope = CallerContext.Use(await AuthorizeAsync(context).ConfigureAwait(false));
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        using var scope = CallerContext.Use(await AuthorizeAsync(context).ConfigureAwait(false));
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

    private async Task<AuthenticatedCaller?> AuthorizeAsync(ServerCallContext context)
    {
        var access = AccessPolicy.For(context.Method);
        if (access == Access.Open)
        {
            return null;
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

        var caller = new AuthenticatedCaller(session.UserId, session.UserName, session.Role, ClientAddressOf(context),
            CleanMachineName(context.RequestHeaders.GetValue(OadmMetadata.ClientMachine)), session.TokenHash);
        if (access == Access.Admin && caller.Role != UserRole.Admin)
        {
            LogDenied(caller.UserName, context.Method);
            throw new RpcException(new Status(StatusCode.PermissionDenied, AdminOnlyMessage));
        }

        return caller;
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
