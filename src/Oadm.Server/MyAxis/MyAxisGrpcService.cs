using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.Hosting;

using Oadm.Core.Auth;
using Oadm.Core.MyAxis;
using Oadm.Server.Auth;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.MyAxis;

/// <summary>
/// The caller's own MyAxis sign-in (docs/spec/myaxis.md, myaxis.proto). Every method works on the caller's row only, an
/// Administrator included; the session never goes back to a client.
/// </summary>
public sealed class MyAxisGrpcService(MyAxisSessionStore sessions, AxisComClient axis, AuditLog audit) : Proto.MyAxisService.MyAxisServiceBase
{
    public const string RefusedText = "MyAxis did not accept the sign-in. Try again.";
    public const string UnreachableText = "MyAxis could not be reached. Check the server's internet access.";

    public override async Task<Proto.MyAxisStateReply> Get(Proto.Empty request, ServerCallContext context) =>
        Reply(await sessions.GetStateAsync(AuthInterceptor.RequireCaller().UserId, context.CancellationToken).ConfigureAwait(false));

    public override async Task<Proto.MyAxisStateReply> SignIn(Proto.MyAxisSignInRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = AuthInterceptor.RequireCaller();
        var ct = context.CancellationToken;
        if (!MyAxisCookies.IsValid(request.Session))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, MyAxisCookies.InvalidText));
        }

        AxisAnswer answer;
        try
        {
            answer = await axis.CheckAsync(request.Session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException && !ct.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, UnreachableText));
        }

        if (answer != AxisAnswer.Ok)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, RefusedText));
        }

        await sessions.SaveAsync(caller.UserId, request.Session, ct).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.MyAxisSignedIn, caller.UserName, null, ct).ConfigureAwait(false);
        return Reply(await sessions.GetStateAsync(caller.UserId, ct).ConfigureAwait(false));
    }

    public override async Task<Proto.MyAxisStateReply> SignOut(Proto.Empty request, ServerCallContext context)
    {
        var caller = AuthInterceptor.RequireCaller();
        var ct = context.CancellationToken;
        if (await sessions.DeleteAsync(caller.UserId, ct).ConfigureAwait(false))
        {
            await audit.WriteAsync(AuditActions.MyAxisSignedOut, caller.UserName, null, ct).ConfigureAwait(false);
        }

        return Reply(MyAxisState.None);
    }

    private static Proto.MyAxisStateReply Reply(MyAxisState state)
    {
        var reply = new Proto.MyAxisStateReply { SignedIn = state.SignedIn, Expired = state.Expired };
        if (state.SignedInUtc is { } since)
        {
            reply.SignedInAt = Timestamp.FromDateTime(DateTime.SpecifyKind(since, DateTimeKind.Utc));
        }

        return reply;
    }
}

/// <summary>The HTTP handler of the calls to www.axis.com: no cookie jar, no redirect (docs/spec/myaxis.md rule 4).</summary>
public static class AxisComHandler
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "www.axis.com downloads with the MyAxis session, never a device.")]
    public static HttpMessageHandler Create() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
    };
}

/// <summary>Cleans the download cache at the start (files of a previous run that are too old, partial downloads).</summary>
public sealed class AxisDownloadSweep(AxisDownloadService downloads) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        downloads.Sweep();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
