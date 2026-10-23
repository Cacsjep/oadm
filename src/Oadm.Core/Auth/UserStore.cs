using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>A user as listed to administrators (never the password hash).</summary>
public sealed record UserRecord(Guid Id, string UserName, Guid RoleId, bool Disabled, DateTime CreatedUtc, DateTime? LastLoginUtc)
{
    /// <summary>Admin for the Administrator role, Operator for every other (the SDK's and older clients' view).</summary>
    public UserRole Role => BuiltInRoles.IsAdministrator(RoleId) ? UserRole.Admin : UserRole.Operator;

    /// <summary>The stored device access (the default, a record built without it, is no device).</summary>
    public DeviceScope DeviceAccess { get; init; } = DeviceScope.None;

    /// <summary>The user may reach every device: the Administrator role (whatever is stored) or "All devices".</summary>
    public bool HasAllDevices => BuiltInRoles.IsAdministrator(RoleId) || DeviceAccess.AllDevices;

    public static UserRecord From(UserEntity e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.Id, e.UserName, e.RoleId, e.Disabled, e.CreatedUtc, e.LastLoginUtc) { DeviceAccess = e.DeviceAccess };
    }
}

/// <summary>
/// The users table (Settings page card Users, Admin only). Names are unique case-insensitively, 1..64 characters;
/// passwords any length up to <see cref="MaxPasswordLength"/> (no strength rule). The last enabled administrator can never be demoted,
/// disabled or deleted, and nobody can change the role, the state or the existence of their own account. A changed
/// password or a disabled user ends the user's sessions; a changed role applies to the user's next call.
/// </summary>
public sealed class UserStore(IDbContextFactory<OadmDbContext> dbFactory, PasswordHasher hasher, AuthTokenStore tokens, TimeProvider time) : IDisposable
{
    public const int MaxUserNameLength = 64;
    public const int MaxPasswordLength = 256;

    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile bool _hasUsers;

    /// <summary>Raised after users were added, changed or deleted.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with the user's id after an administrator gave a user another role.</summary>
    public event EventHandler<Guid>? UserRoleChanged;

    /// <summary>Raised with the user's id after an administrator gave a user other devices (all devices or another group).</summary>
    public event EventHandler<Guid>? UserDeviceAccessChanged;

    /// <summary>The trimmed name, or an <see cref="AuthException"/> (InvalidArgument) with the reason.</summary>
    public static string ValidateUserName(string? userName)
    {
        var name = (userName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new AuthException(AuthError.InvalidArgument, "Enter a user name.");
        }

        if (name.Length > MaxUserNameLength || name.Any(char.IsControl))
        {
            throw new AuthException(AuthError.InvalidArgument, $"The user name must be 1-{MaxUserNameLength} characters without control characters.");
        }

        return name;
    }

    /// <summary>Null when the password is acceptable, else the reason.</summary>
    public static string? PasswordError(string? password) =>
        string.IsNullOrEmpty(password) ? "Enter a password."
        : password.Length > MaxPasswordLength ? $"The password must have at most {MaxPasswordLength} characters."
        : null;

    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();

    /// <summary>True once any user exists (cached after the first true).</summary>
    public async Task<bool> HasUsersAsync(CancellationToken ct)
    {
        if (_hasUsers)
        {
            return true;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        _hasUsers = await db.Users.AnyAsync(ct).ConfigureAwait(false);
        return _hasUsers;
    }

    public async Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Users.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase).Select(UserRecord.From)];
    }

    /// <summary>The user with this id as stored now, or null (one row by its key: a queued task leaving the queue).</summary>
    public async Task<UserRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
        return row is null ? null : UserRecord.From(row);
    }

    /// <summary>The user with this name (case-insensitive), or null.</summary>
    public async Task<UserEntity?> FindByNameAsync(string userName, CancellationToken ct)
    {
        var normalized = Normalize(userName ?? string.Empty);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedName == normalized, ct).ConfigureAwait(false);
    }

    public async Task<UserRecord> AddAsync(string userName, string password, Guid roleId, CancellationToken ct)
    {
        var entity = await CreateAsync(userName, password, roleId, onlyIfNoUsers: false, ct).ConfigureAwait(false);
        return UserRecord.From(entity);
    }

    /// <summary>Adds a user with a built-in role (Admin = Administrator, Operator = Operator).</summary>
    public Task<UserRecord> AddAsync(string userName, string password, UserRole role, CancellationToken ct) =>
        AddAsync(userName, password, BuiltInRoles.IdOf(role), ct);

    /// <summary>
    /// Creates a user. With <paramref name="onlyIfNoUsers"/> (first administrator) it fails with Precondition when a
    /// user exists, checked in the same write lock.
    /// </summary>
    public Task<UserEntity> CreateAsync(string userName, string password, UserRole role, bool onlyIfNoUsers, CancellationToken ct) =>
        CreateAsync(userName, password, BuiltInRoles.IdOf(role), onlyIfNoUsers, ct);

    /// <summary>
    /// <see cref="CreateAsync(string, string, Guid, DeviceScope, bool, CancellationToken)"/> with all devices: the
    /// first administrator, in-process hosts and older clients that do not choose (a user created before device groups
    /// existed had all devices too).
    /// </summary>
    public Task<UserEntity> CreateAsync(string userName, string password, Guid roleId, bool onlyIfNoUsers, CancellationToken ct) =>
        CreateAsync(userName, password, roleId, DeviceScope.All, onlyIfNoUsers, ct);

    /// <summary>Adds a user with a role and an explicit device access (a group must exist).</summary>
    public async Task<UserRecord> AddAsync(string userName, string password, Guid roleId, DeviceScope access, CancellationToken ct)
    {
        var entity = await CreateAsync(userName, password, roleId, access, onlyIfNoUsers: false, ct).ConfigureAwait(false);
        return UserRecord.From(entity);
    }

    /// <summary>
    /// Creates a user with <paramref name="access"/>: all devices or an existing device group (InvalidArgument for an
    /// unknown group and for no devices at all). With <paramref name="onlyIfNoUsers"/> (first administrator) it fails with
    /// Precondition when a user exists, checked in the same write lock.
    /// </summary>
    public async Task<UserEntity> CreateAsync(string userName, string password, Guid roleId, DeviceScope access, bool onlyIfNoUsers, CancellationToken ct)
    {
        var name = ValidateUserName(userName);
        if (PasswordError(password) is { } error)
        {
            throw new AuthException(AuthError.InvalidArgument, error);
        }

        var hash = hasher.Hash(password);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (onlyIfNoUsers && await db.Users.AnyAsync(ct).ConfigureAwait(false))
            {
                throw new AuthException(AuthError.Precondition, "The first administrator exists already. Log in instead.");
            }

            if (!await db.Roles.AnyAsync(r => r.Id == roleId, ct).ConfigureAwait(false))
            {
                throw new AuthException(AuthError.InvalidArgument, "Unknown role.");
            }

            await RequireValidAccessAsync(db, access, ct).ConfigureAwait(false);
            var normalized = Normalize(name);
            if (await db.Users.AnyAsync(u => u.NormalizedName == normalized, ct).ConfigureAwait(false))
            {
                throw new AuthException(AuthError.InvalidArgument, $"A user named {name} exists already.");
            }

            var entity = new UserEntity
            {
                Id = Guid.NewGuid(),
                UserName = name,
                NormalizedName = normalized,
                PasswordHash = hash,
                RoleId = roleId,
                AllDevices = access.AllDevices,
                DeviceGroupId = access.EffectiveGroupId,
                CreatedUtc = time.GetUtcNow().UtcDateTime,
            };
            db.Users.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _hasUsers = true;
            CallerContext.Raise(Changed, this, EventArgs.Empty);
            return entity;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>
    /// Changes role, state and / or password (null / empty = keep). <paramref name="actorId"/> is the administrator doing
    /// it: role and state of the own account cannot be changed. Returns the user and what changed ("role Admin, password").
    /// </summary>
    public Task<(UserRecord User, string Changes)> UpdateAsync(Guid id, UserRole? role, bool? disabled, string? newPassword, Guid? actorId, CancellationToken ct) =>
        UpdateAsync(id, role is { } r ? BuiltInRoles.IdOf(r) : null, disabled, newPassword, actorId, ct);

    /// <inheritdoc cref="UpdateAsync(Guid, UserRole?, bool?, string?, Guid?, CancellationToken)"/>
    public Task<(UserRecord User, string Changes)> UpdateAsync(Guid id, Guid? roleId, bool? disabled, string? newPassword, Guid? actorId, CancellationToken ct) =>
        UpdateAsync(id, roleId, disabled, newPassword, access: null, actorId, ct);

    /// <summary>
    /// Changes role, state, password and / or the devices (null / empty = keep; a group must exist). A new device access
    /// applies to the user's next call and ends the user's open streams (<see cref="UserDeviceAccessChanged"/>), like a
    /// new role. Returns the user and what changed ("role Operator, devices Building B").
    /// </summary>
    public async Task<(UserRecord User, string Changes)> UpdateAsync(Guid id, Guid? roleId, bool? disabled, string? newPassword, DeviceScope? access, Guid? actorId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(newPassword) && PasswordError(newPassword) is { } error)
        {
            throw new AuthException(AuthError.InvalidArgument, error);
        }

        var hash = string.IsNullOrEmpty(newPassword) ? null : hasher.Hash(newPassword);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                ?? throw new AuthException(AuthError.NotFound, "The user does not exist (any more).");
            var changes = new List<string>();
            var roleChanges = roleId is { } newRole && newRole != user.RoleId;
            if (roleChanges && !await db.Roles.AnyAsync(r => r.Id == roleId!.Value, ct).ConfigureAwait(false))
            {
                throw new AuthException(AuthError.InvalidArgument, "Unknown role.");
            }

            var stateChanges = disabled is { } newState && newState != user.Disabled;
            if ((roleChanges || stateChanges) && actorId == user.Id)
            {
                throw new AuthException(AuthError.Precondition, "You cannot change the role or state of your own account.");
            }

            if ((roleChanges && !BuiltInRoles.IsAdministrator(roleId!.Value)) || (stateChanges && disabled == true))
            {
                await RequireAnotherEnabledAdminAsync(db, user, ct).ConfigureAwait(false);
            }

            var newAccess = access is { } a ? new DeviceScope(a.AllDevices, a.EffectiveGroupId) : user.DeviceAccess;
            var accessChanges = newAccess != new DeviceScope(user.AllDevices, user.AllDevices ? null : user.DeviceGroupId);
            if (accessChanges)
            {
                await RequireValidAccessAsync(db, newAccess, ct).ConfigureAwait(false);
            }

            if (roleChanges)
            {
                user.RoleId = roleId!.Value;
                var roleName = await db.Roles.Where(r => r.Id == user.RoleId).Select(r => r.Name).FirstAsync(ct).ConfigureAwait(false);
                changes.Add("role " + roleName);
            }

            if (stateChanges)
            {
                user.Disabled = disabled!.Value;
                changes.Add(user.Disabled ? "disabled" : "enabled");
            }

            if (hash is not null)
            {
                user.PasswordHash = hash;
                changes.Add("password reset");
            }

            if (accessChanges)
            {
                user.AllDevices = newAccess.AllDevices;
                user.DeviceGroupId = newAccess.EffectiveGroupId;
                var groupName = newAccess.EffectiveGroupId is { } groupId
                    ? await db.DeviceGroups.Where(g => g.Id == groupId).Select(g => g.Name).FirstAsync(ct).ConfigureAwait(false)
                    : null;
                changes.Add("devices " + (groupName ?? "All devices"));
            }

            if (changes.Count > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                if (hash is not null || user.Disabled)
                {
                    await tokens.RevokeUserAsync(user.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    // A new role or new devices apply to the next call; the sessions stay.
                    if (roleChanges)
                    {
                        tokens.SetRole(user.Id, user.RoleId);
                    }

                    if (accessChanges)
                    {
                        tokens.SetDeviceAccess(user.Id, user.DeviceAccess);
                    }
                }

                CallerContext.Raise(Changed, this, EventArgs.Empty);
                if (roleChanges)
                {
                    CallerContext.Raise(UserRoleChanged, this, user.Id);
                }

                if (accessChanges)
                {
                    CallerContext.Raise(UserDeviceAccessChanged, this, user.Id);
                }
            }

            return (UserRecord.From(user), string.Join(", ", changes));
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>All devices, or a group that exists; never "no devices" by choice.</summary>
    private static async Task RequireValidAccessAsync(OadmDbContext db, DeviceScope access, CancellationToken ct)
    {
        if (access.AllDevices)
        {
            return;
        }

        if (access.GroupId is not { } groupId)
        {
            throw new AuthException(AuthError.InvalidArgument, DeviceGroups.DeviceGroupTexts.ChooseDevices);
        }

        if (!await db.DeviceGroups.AnyAsync(g => g.Id == groupId, ct).ConfigureAwait(false))
        {
            throw new AuthException(AuthError.InvalidArgument, DeviceGroups.DeviceGroupTexts.UnknownGroup);
        }
    }

    /// <summary>Deletes a user (never the own account, never the last enabled administrator). Returns its name.</summary>
    public async Task<string> DeleteAsync(Guid id, Guid? actorId, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                ?? throw new AuthException(AuthError.NotFound, "The user does not exist (any more).");
            if (actorId == user.Id)
            {
                throw new AuthException(AuthError.Precondition, "You cannot delete your own account.");
            }

            await RequireAnotherEnabledAdminAsync(db, user, ct).ConfigureAwait(false);
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tokens.RevokeUserAsync(user.Id, ct).ConfigureAwait(false);
            CallerContext.Raise(Changed, this, EventArgs.Empty);
            return user.UserName;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Stores the time of a successful login.</summary>
    public async Task TouchLoginAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginUtc, now), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Another store changed the device access of users in the database (a deleted device group: its users have no devices
    /// now): their sessions take it over and the events of a changed access are raised.
    /// </summary>
    public void NotifyDeviceAccessChanged(IReadOnlyCollection<(Guid UserId, DeviceScope Access)> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (users.Count == 0)
        {
            return;
        }

        foreach (var (userId, access) in users)
        {
            tokens.SetDeviceAccess(userId, access);
        }

        CallerContext.Raise(Changed, this, EventArgs.Empty);
        foreach (var (userId, _) in users)
        {
            CallerContext.Raise(UserDeviceAccessChanged, this, userId);
        }
    }

    public void Dispose() => _write.Dispose();

    private static async Task RequireAnotherEnabledAdminAsync(OadmDbContext db, UserEntity user, CancellationToken ct)
    {
        if (!BuiltInRoles.IsAdministrator(user.RoleId) || user.Disabled)
        {
            return;
        }

        var admin = BuiltInRoles.AdministratorId;
        if (!await db.Users.AnyAsync(u => u.Id != user.Id && u.RoleId == admin && !u.Disabled, ct).ConfigureAwait(false))
        {
            throw new AuthException(AuthError.Precondition, $"{user.UserName} is the last enabled administrator. Add or enable another administrator first.");
        }
    }
}
