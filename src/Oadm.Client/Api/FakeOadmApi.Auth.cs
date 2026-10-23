using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode has no login: every call runs as the administrator "admin", or the user <see cref="CurrentUserName"/> names
/// (FakeOadmApi.GroupUser.cs). Users and audit entries are kept in memory
/// with the same rules as the server (last enabled administrator, own account, password length).
/// </summary>
public sealed partial class FakeOadmApi
{
    /// <summary>User of fake mode.</summary>
    public const string FakeUserName = "admin";

    private readonly List<UserInfo> _users = [];
    private readonly List<AuditEntry> _audit = [];
    private bool _authSeeded;

    public string? AccessToken { get; set; } = "fake";

#pragma warning disable CS0067 // Fake mode never ends a session.
    public event EventHandler? SessionEnded;
#pragma warning restore CS0067

    public Task<AuthStatus> GetAuthStatusAsync(CancellationToken ct) =>
        Task.FromResult(new AuthStatus { ServerName = "FAKE-SERVER", Version = "fake", NeedsFirstAdmin = false });

    public Task<LoginReply> LoginAsync(string userName, string password, bool remember, CancellationToken ct)
    {
        lock (_gate)
        {
            SeedAuthLocked();
            UserInfo user = _users.FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase))
                ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "The user name or password is wrong."));
            return Task.FromResult(new LoginReply { Token = "fake", User = user.Clone() });
        }
    }

    public Task<LoginReply> CreateFirstAdminAsync(string userName, string password, string? setupCode, bool remember, CancellationToken ct) =>
        throw new RpcException(new Status(StatusCode.FailedPrecondition, "The first administrator exists already. Log in instead."));

    public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<UserInfo> GetCurrentUserAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            SeedAuthLocked();
            return Task.FromResult(CurrentUserInfoLocked());
        }
    }

    public Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            return Task.FromResult<IReadOnlyList<UserInfo>>(_users.Select(u => u.Clone()).OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    public Task<UserInfo> AddUserAsync(AddUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string userName = request.UserName;
        string password = request.Password;
        string roleId = request.RoleId;
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            string name = (userName ?? "").Trim();
            if (name.Length is 0 or > 64)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The user name must be 1-64 characters without control characters."));
            }

            if (string.IsNullOrEmpty(password))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Enter a password."));
            }

            if (_users.Any(u => string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"A user named {name} exists already."));
            }

            RoleInfo role = _roles.FirstOrDefault(r => r.Id == roleId)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown role."));
            var user = new UserInfo { Id = Guid.NewGuid().ToString(), UserName = name, Created = Timestamp.FromDateTime(DateTime.UtcNow) };
            SetRole(user, role);
            user.AllDevices = true;
            if (request.HasAllDevices && !request.AllDevices && string.IsNullOrEmpty(request.DeviceGroupId))
            {
                throw Invalid("Choose All devices or a device group.");
            }

            SetUserDevicesLocked(user, request.HasAllDevices ? request.AllDevices : null, request.DeviceGroupId);
            _users.Add(user);
            AuditLocked("Added user", name, "Role " + role.Name);
            return Task.FromResult(user.Clone());
        }
    }

    public Task<UserInfo> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            UserInfo user = _users.FirstOrDefault(u => u.Id == request.Id)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "The user does not exist (any more)."));
            string? roleId = request.HasRoleId ? request.RoleId
                : request.HasRole ? (request.Role == UserRole.Admin ? AdministratorRoleId : OperatorRoleId)
                : null;
            RoleInfo? newRole = roleId is null ? null : _roles.FirstOrDefault(r => r.Id == roleId)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown role."));
            bool roleChanges = newRole is not null && newRole.Id != user.RoleId;
            bool stateChanges = request.HasDisabled && request.Disabled != user.Disabled;
            if ((roleChanges || stateChanges) && user.UserName == FakeUserName)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "You cannot change the role or state of your own account."));
            }

            if (((roleChanges && newRole!.Kind != RoleKind.Administrator) || (stateChanges && request.Disabled)) && IsLastAdminLocked(user))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{user.UserName} is the last enabled administrator. Add or enable another administrator first."));
            }

            if (roleChanges)
            {
                SetRole(user, newRole!);
            }

            if (stateChanges)
            {
                user.Disabled = request.Disabled;
            }

            if (request.HasAllDevices || request.HasDeviceGroupId)
            {
                SetUserDevicesLocked(user, request.HasAllDevices ? request.AllDevices : null, request.HasDeviceGroupId ? request.DeviceGroupId : null);
            }
            else if (roleChanges)
            {
                user.AllDevices = user.AllDevices || user.Role == UserRole.Admin;
            }

            AuditLocked("Changed user", user.UserName, request.NewPassword.Length > 0 ? "password reset" : "role or state");
            return Task.FromResult(user.Clone());
        }
    }

    public Task DeleteUserAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            UserInfo user = _users.FirstOrDefault(u => u.Id == id)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "The user does not exist (any more)."));
            if (user.UserName == FakeUserName)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "You cannot delete your own account."));
            }

            if (IsLastAdminLocked(user))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{user.UserName} is the last enabled administrator. Add or enable another administrator first."));
            }

            _users.Remove(user);
            AuditLocked("Deleted user", user.UserName, "");
            return Task.CompletedTask;
        }
    }

    public Task<AuditList> ListAuditAsync(int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedAuthLocked();
            var reply = new AuditList { TotalCount = _audit.Count };
            reply.Entries.AddRange(_audit.OrderByDescending(e => e.Id).Take(limit <= 0 ? 10_000 : limit).Select(e => e.Clone()));
            return Task.FromResult(reply);
        }
    }

    /// <summary>Adds audit entries (tests: thousands of rows).</summary>
    public void AddAuditEntries(IEnumerable<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            SeedAuthLocked();
            foreach (AuditEntry entry in entries)
            {
                entry.Id = _audit.Count + 1;
                _audit.Add(entry);
            }
        }
    }

    private static void SetRole(UserInfo user, RoleInfo role)
    {
        user.RoleId = role.Id;
        user.RoleName = role.Name;
        user.Role = role.Kind == RoleKind.Administrator ? UserRole.Admin : UserRole.Operator;
    }

    private bool IsLastAdminLocked(UserInfo user) =>
        user.Role == UserRole.Admin && !user.Disabled && !_users.Any(u => u != user && u.Role == UserRole.Admin && !u.Disabled);

    private void AuditLocked(string action, string target, string detail) =>
        _audit.Add(new AuditEntry
        {
            Id = _audit.Count + 1,
            Time = Timestamp.FromDateTime(DateTime.UtcNow),
            UserName = FakeUserName,
            ClientAddress = "127.0.0.1",
            Action = action,
            Target = target,
            Detail = detail,
        });

    private void SeedAuthLocked()
    {
        if (_authSeeded)
        {
            return;
        }

        _authSeeded = true;
        SeedRolesLocked();
        DateTime now = DateTime.UtcNow;
        UserInfo User(string name, string roleId, bool disabled, double daysAgo, double? lastLoginHoursAgo)
        {
            var user = new UserInfo
            {
                Id = Guid.NewGuid().ToString(),
                UserName = name,
                Disabled = disabled,
                Created = Timestamp.FromDateTime(now.AddDays(-daysAgo)),
                LastLogin = lastLoginHoursAgo is { } h ? Timestamp.FromDateTime(now.AddHours(-h)) : null,
                AllDevices = true,
            };
            SetRole(user, _roles.First(r => r.Id == roleId));
            return user;
        }

        _users.Add(User(FakeUserName, AdministratorRoleId, false, 120, 0));
        _users.Add(User("anna.berg", AdministratorRoleId, false, 90, 26));
        _users.Add(User("tech1", OperatorRoleId, false, 60, 3));
        _users.Add(User("tech2", OperatorRoleId, false, 45, null));
        _users.Add(User("guard", ViewerRoleId, false, 20, 50));
        _users.Add(User("contractor", ContractorRoleId, true, 30, 400));

        (string User, string Client, string Action, string Target, string Detail)[] seed =
        [
            ("admin", "127.0.0.1", "Created the first administrator", "admin", ""),
            ("admin", "127.0.0.1", "Logged in", "admin", ""),
            ("admin", "127.0.0.1", "Added user", "tech1", "Role Operator"),
            ("admin", "127.0.0.1", "Added credential", "root", ""),
            ("tech1", "10.0.0.21", "Login failed", "tech1", "Wrong password"),
            ("tech1", "10.0.0.21", "Logged in", "tech1", "Remember me"),
            ("tech1", "10.0.0.21", "Started task", "Restart", "2 devices"),
            ("tech1", "10.0.0.21", "Plugin action", "VAPIX Commander", "rollout"),
            ("anna.berg", "10.0.0.35", "Logged in", "anna.berg", ""),
            ("anna.berg", "10.0.0.35", "Plugin action", "NTP server", "save"),
            ("anna.berg", "10.0.0.35", "Changed server settings", "Server settings", "Polling interval (s) 60 -> 30"),
            ("anna.berg", "10.0.0.35", "Showed credential password", "root", ""),
            ("admin", "127.0.0.1", "Removed devices", "10.0.0.77", "1 device"),
            ("admin", "127.0.0.1", "Changed user", "contractor", "disabled"),
        ];
        for (int i = 0; i < seed.Length; i++)
        {
            _audit.Add(new AuditEntry
            {
                Id = i + 1,
                Time = Timestamp.FromDateTime(now.AddMinutes(-(seed.Length - i) * 37)),
                UserName = seed[i].User,
                ClientAddress = seed[i].Client,
                Action = seed[i].Action,
                Target = seed[i].Target,
                Detail = seed[i].Detail,
            });
        }

        SeedGroupsLocked();
    }
}
