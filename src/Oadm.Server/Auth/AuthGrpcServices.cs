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

    /// <summary>A user with its role (id, name and the two-value role older clients know).</summary>
    public static async Task<Proto.UserInfo> ToProtoAsync(UserRecord user, RoleStore roles, CancellationToken ct)
    {
        var info = new Proto.UserInfo
        {
            Id = user.Id.ToString(),
            UserName = user.UserName,
            Role = ToProto(user.Role),
            Disabled = user.Disabled,
            Created = Timestamp.FromDateTime(DateTime.SpecifyKind(user.CreatedUtc, DateTimeKind.Utc)),
            RoleId = user.RoleId.ToString(),
            RoleName = await roles.NameOfAsync(user.RoleId, ct).ConfigureAwait(false),
        };
        if (user.LastLoginUtc is { } last)
        {
            info.LastLogin = Timestamp.FromDateTime(DateTime.SpecifyKind(last, DateTimeKind.Utc));
        }

        return info;
    }

    /// <summary>The caller's own user: <see cref="ToProtoAsync"/> plus what the role allows.</summary>
    public static async Task<Proto.UserInfo> SelfAsync(UserRecord user, RoleStore roles, CancellationToken ct)
    {
        var info = await ToProtoAsync(user, roles, ct).ConfigureAwait(false);
        var permissions = await roles.PermissionsOfAsync(user.RoleId, ct).ConfigureAwait(false);
        info.AllPermissions = permissions.IsAll;
        info.StartPage = await roles.StartPageOfAsync(user.RoleId, ct).ConfigureAwait(false);
        info.Permissions.AddRange(permissions.Keys.Order(StringComparer.Ordinal));
        return info;
    }

    /// <summary>The role of a request: <paramref name="roleId"/> when set, else the two-value role of older clients.</summary>
    public static Guid RoleIdOf(string? roleId, Proto.UserRole role) =>
        string.IsNullOrEmpty(roleId) ? BuiltInRoles.IdOf(FromProto(role))
        : Guid.TryParse(roleId, out var id) ? id
        : throw new AuthException(AuthError.InvalidArgument, "Unknown role.");

    public static Proto.UserRole ToProto(UserRole role) => role == UserRole.Admin ? Proto.UserRole.Admin : Proto.UserRole.Operator;

    public static UserRole FromProto(Proto.UserRole role) => role switch
    {
        Proto.UserRole.Admin => UserRole.Admin,
        Proto.UserRole.Operator => UserRole.Operator,
        _ => throw new AuthException(AuthError.InvalidArgument, "Unknown role."),
    };
}

/// <summary>gRPC AuthService: status, login, logout and the first administrator.</summary>
public sealed class AuthGrpcService(AuthManager auth, UserStore users, RoleStore roles, AuthTokenStore tokens, AuditLog audit, ServerSettingsStore settings) : Proto.AuthService.AuthServiceBase
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
            return new Proto.LoginReply { Token = result.Token, User = await AuthMapping.SelfAsync(result.User, roles, context.CancellationToken).ConfigureAwait(false) };
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
            return new Proto.LoginReply { Token = result.Token, User = await AuthMapping.SelfAsync(result.User, roles, context.CancellationToken).ConfigureAwait(false) };
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
        return await SelfAsync(caller, context.CancellationToken).ConfigureAwait(false)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, SessionEndedMessage));
    }

    /// <summary>
    /// The caller's user now, then again after every change of its role or of the role's permissions. Ends with
    /// UNAUTHENTICATED when the user is gone or disabled.
    /// </summary>
    public override async Task WatchSession(Proto.Empty request, IServerStreamWriter<Proto.UserInfo> responseStream, ServerCallContext context)
    {
        var caller = AuthInterceptor.RequireCaller();
        var ct = context.CancellationToken;
        var signal = System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
        });
        void OnRoles(object? sender, IReadOnlyCollection<Guid> ids) => signal.Writer.TryWrite(true);
        void OnUsers(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        roles.Changed += OnRoles;
        users.Changed += OnUsers;
        try
        {
            string? last = null;
            do
            {
                var info = await SelfAsync(caller, ct).ConfigureAwait(false)
                    ?? throw new RpcException(new Status(StatusCode.Unauthenticated, SessionEndedMessage));
                var text = info.ToString();
                if (text != last)
                {
                    last = text;
                    await responseStream.WriteAsync(info, ct).ConfigureAwait(false);
                }
            }
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false) && signal.Reader.TryRead(out _));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client stopped watching.
        }
        finally
        {
            roles.Changed -= OnRoles;
            users.Changed -= OnUsers;
        }
    }

    private const string SessionEndedMessage = "Your session has ended. Log in again.";

    /// <summary>The caller's user with role and permissions, or null when it is gone or disabled.</summary>
    private async Task<Proto.UserInfo?> SelfAsync(AuthenticatedCaller caller, CancellationToken ct)
    {
        var user = await users.FindByNameAsync(caller.UserName, ct).ConfigureAwait(false);
        return user is null || user.Disabled || user.Id != caller.UserId ? null : await AuthMapping.SelfAsync(UserRecord.From(user), roles, ct).ConfigureAwait(false);
    }
}

/// <summary>gRPC UserService (Admin only, see <see cref="AccessPolicy"/>). Every change goes to the audit log.</summary>
public sealed class UserGrpcService(UserStore users, RoleStore roles, AuditLog audit) : Proto.UserService.UserServiceBase
{
    public override async Task<Proto.UserList> List(Proto.Empty request, ServerCallContext context)
    {
        var reply = new Proto.UserList();
        foreach (var user in await users.ListAsync(context.CancellationToken).ConfigureAwait(false))
        {
            reply.Users.Add(await AuthMapping.ToProtoAsync(user, roles, context.CancellationToken).ConfigureAwait(false));
        }

        return reply;
    }

    public override async Task<Proto.UserInfo> Add(Proto.AddUserRequest request, ServerCallContext context)
    {
        try
        {
            var roleId = AuthMapping.RoleIdOf(request.RoleId, request.Role);
            var user = await users.AddAsync(request.UserName, request.Password, roleId, context.CancellationToken).ConfigureAwait(false);
            var info = await AuthMapping.ToProtoAsync(user, roles, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.UserAdded, user.UserName, "Role " + info.RoleName, context.CancellationToken).ConfigureAwait(false);
            return info;
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
            Guid? roleId = request.HasRoleId ? AuthMapping.RoleIdOf(request.RoleId, request.Role)
                : request.HasRole ? BuiltInRoles.IdOf(AuthMapping.FromProto(request.Role))
                : null;
            var (user, changes) = await users.UpdateAsync(
                id,
                roleId,
                request.HasDisabled ? request.Disabled : null,
                string.IsNullOrEmpty(request.NewPassword) ? null : request.NewPassword,
                AuthInterceptor.RequireCaller().UserId,
                context.CancellationToken).ConfigureAwait(false);
            if (changes.Length > 0)
            {
                await audit.WriteAsync(AuditActions.UserChanged, user.UserName, changes, context.CancellationToken).ConfigureAwait(false);
            }

            return await AuthMapping.ToProtoAsync(user, roles, context.CancellationToken).ConfigureAwait(false);
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
