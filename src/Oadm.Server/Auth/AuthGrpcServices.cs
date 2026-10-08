using System.Reflection;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Settings;
using Oadm.Sdk.Plugins;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Auth;

/// <summary>Maps auth results to gRPC.</summary>
internal static class AuthMapping
{
    public static RpcException ToRpc(AuthException ex) => new(new Status(
        ex.Error switch
        {
            AuthError.NotAuthenticated => StatusCode.Unauthenticated,
            AuthError.Locked => StatusCode.ResourceExhausted,
            AuthError.PermissionDenied => StatusCode.PermissionDenied,
            AuthError.Precondition => StatusCode.FailedPrecondition,
            AuthError.InvalidArgument => StatusCode.InvalidArgument,
            AuthError.NotFound => StatusCode.NotFound,
            _ => StatusCode.Internal,
        },
        ex.Message));

    public static Proto.UserInfo ToProto(UserRecord user)
    {
        var info = new Proto.UserInfo
        {
            Id = user.Id.ToString(),
            UserName = user.UserName,
            Role = ToProto(user.Role),
            Disabled = user.Disabled,
            Created = Timestamp.FromDateTime(DateTime.SpecifyKind(user.CreatedUtc, DateTimeKind.Utc)),
        };
        if (user.LastLoginUtc is { } last)
        {
            info.LastLogin = Timestamp.FromDateTime(DateTime.SpecifyKind(last, DateTimeKind.Utc));
        }

        return info;
    }

    public static Proto.UserRole ToProto(UserRole role) => role == UserRole.Admin ? Proto.UserRole.Admin : Proto.UserRole.Operator;

    public static UserRole FromProto(Proto.UserRole role) => role switch
    {
        Proto.UserRole.Admin => UserRole.Admin,
        Proto.UserRole.Operator => UserRole.Operator,
        _ => throw new AuthException(AuthError.InvalidArgument, "Unknown role."),
    };
}

/// <summary>gRPC AuthService: status, login, logout and the first administrator.</summary>
public sealed class AuthGrpcService(AuthManager auth, UserStore users, AuthTokenStore tokens, AuditLog audit, ServerSettingsStore settings) : Proto.AuthService.AuthServiceBase
{
    private static readonly string Version =
        typeof(AuthGrpcService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    public override async Task<Proto.AuthStatus> Status(Proto.Empty request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var needsAdmin = !await users.HasUsersAsync(ct).ConfigureAwait(false);
        return new Proto.AuthStatus
        {
            ServerName = (await settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName,
            Version = Version,
            NeedsFirstAdmin = needsAdmin,
            SetupCodeRequired = needsAdmin && !AuthInterceptor.IsLoopback(context),
        };
    }

    public override async Task<Proto.LoginReply> Login(Proto.LoginRequest request, ServerCallContext context)
    {
        try
        {
            var result = await auth.LoginAsync(request.UserName, request.Password, request.Remember, AuthInterceptor.ClientAddressOf(context), context.CancellationToken).ConfigureAwait(false);
            return new Proto.LoginReply { Token = result.Token, User = AuthMapping.ToProto(result.User) };
        }
        catch (AuthException ex)
        {
            throw AuthMapping.ToRpc(ex);
        }
    }

    public override async Task<Proto.LoginReply> CreateFirstAdmin(Proto.CreateFirstAdminRequest request, ServerCallContext context)
    {
        try
        {
            var result = await auth.CreateFirstAdminAsync(request.UserName, request.Password, request.SetupCode,
                AuthInterceptor.IsLoopback(context), request.Remember, AuthInterceptor.ClientAddressOf(context), context.CancellationToken).ConfigureAwait(false);
            return new Proto.LoginReply { Token = result.Token, User = AuthMapping.ToProto(result.User) };
        }
        catch (AuthException ex)
        {
            throw AuthMapping.ToRpc(ex);
        }
    }

    public override async Task<Proto.Empty> Logout(Proto.Empty request, ServerCallContext context)
    {
        var caller = AuthInterceptor.RequireCaller();
        await tokens.RevokeAsync(caller.TokenHash, context.CancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.Logout, caller.UserName, null, context.CancellationToken).ConfigureAwait(false);
        return new Proto.Empty();
    }

    public override async Task<Proto.UserInfo> Me(Proto.Empty request, ServerCallContext context)
    {
        var caller = AuthInterceptor.RequireCaller();
        var user = await users.FindByNameAsync(caller.UserName, context.CancellationToken).ConfigureAwait(false)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Your session has ended. Log in again."));
        return AuthMapping.ToProto(UserRecord.From(user));
    }
}

/// <summary>gRPC UserService (Admin only, see <see cref="AccessPolicy"/>). Every change goes to the audit log.</summary>
public sealed class UserGrpcService(UserStore users, AuditLog audit) : Proto.UserService.UserServiceBase
{
    public override async Task<Proto.UserList> List(Proto.Empty request, ServerCallContext context)
    {
        var reply = new Proto.UserList();
        reply.Users.AddRange((await users.ListAsync(context.CancellationToken).ConfigureAwait(false)).Select(AuthMapping.ToProto));
        return reply;
    }

    public override async Task<Proto.UserInfo> Add(Proto.AddUserRequest request, ServerCallContext context)
    {
        try
        {
            var user = await users.AddAsync(request.UserName, request.Password, AuthMapping.FromProto(request.Role), context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.UserAdded, user.UserName, "Role " + user.Role, context.CancellationToken).ConfigureAwait(false);
            return AuthMapping.ToProto(user);
        }
        catch (AuthException ex)
        {
            throw AuthMapping.ToRpc(ex);
        }
    }

    public override async Task<Proto.UserInfo> Update(Proto.UpdateUserRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "user id");
        try
        {
            var (user, changes) = await users.UpdateAsync(
                id,
                request.HasRole ? AuthMapping.FromProto(request.Role) : null,
                request.HasDisabled ? request.Disabled : null,
                string.IsNullOrEmpty(request.NewPassword) ? null : request.NewPassword,
                AuthInterceptor.RequireCaller().UserId,
                context.CancellationToken).ConfigureAwait(false);
            if (changes.Length > 0)
            {
                await audit.WriteAsync(AuditActions.UserChanged, user.UserName, changes, context.CancellationToken).ConfigureAwait(false);
            }

            return AuthMapping.ToProto(user);
        }
        catch (AuthException ex)
        {
            throw AuthMapping.ToRpc(ex);
        }
    }

    public override async Task<Proto.Empty> Delete(Proto.UserIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "user id");
        try
        {
            var name = await users.DeleteAsync(id, AuthInterceptor.RequireCaller().UserId, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.UserDeleted, name, null, context.CancellationToken).ConfigureAwait(false);
            return new Proto.Empty();
        }
        catch (AuthException ex)
        {
            throw AuthMapping.ToRpc(ex);
        }
    }
}

/// <summary>gRPC AuditService (Admin only): the audit log, newest first.</summary>
public sealed class AuditGrpcService(AuditLog audit) : Proto.AuditService.AuditServiceBase
{
    public override async Task<Proto.AuditList> List(Proto.ListAuditRequest request, ServerCallContext context)
    {
        var (entries, total) = await audit.ListAsync(request.Limit, request.Offset, context.CancellationToken).ConfigureAwait(false);
        var reply = new Proto.AuditList { TotalCount = total };
        reply.Entries.AddRange(entries.Select(e => new Proto.AuditEntry
        {
            Id = e.Id,
            Time = Timestamp.FromDateTime(DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc)),
            UserName = e.UserName,
            ClientAddress = e.ClientAddress,
            Action = e.Action,
            Target = e.Target,
            Detail = e.Detail,
        }));
        return reply;
    }
}
