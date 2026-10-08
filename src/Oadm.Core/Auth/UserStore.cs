using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>A user as listed to administrators (never the password hash).</summary>
public sealed record UserRecord(Guid Id, string UserName, UserRole Role, bool Disabled, DateTime CreatedUtc, DateTime? LastLoginUtc)
{
    public static UserRecord From(UserEntity e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.Id, e.UserName, e.Role, e.Disabled, e.CreatedUtc, e.LastLoginUtc);
    }
}

/// <summary>
/// The users table (Settings page card Users, Admin only). Names are unique case-insensitively, 1..64 characters;
/// passwords at least <see cref="MinPasswordLength"/> characters. The last enabled administrator can never be demoted,
/// disabled or deleted, and nobody can change the role, the state or the existence of their own account. A changed
/// password, role or state ends the user's sessions.
/// </summary>
public sealed class UserStore(IDbContextFactory<OadmDbContext> dbFactory, PasswordHasher hasher, AuthTokenStore tokens, TimeProvider time) : IDisposable
{
    public const int MaxUserNameLength = 64;
    public const int MinPasswordLength = 10;
    public const int MaxPasswordLength = 256;

    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile bool _hasUsers;

    /// <summary>Raised after users were added, changed or deleted.</summary>
    public event EventHandler? Changed;

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
        : password.Length < MinPasswordLength ? $"The password must have at least {MinPasswordLength} characters."
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

    /// <summary>The user with this name (case-insensitive), or null.</summary>
    public async Task<UserEntity?> FindByNameAsync(string userName, CancellationToken ct)
    {
        var normalized = Normalize(userName ?? string.Empty);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedName == normalized, ct).ConfigureAwait(false);
    }

    public async Task<UserRecord> AddAsync(string userName, string password, UserRole role, CancellationToken ct)
    {
        var entity = await CreateAsync(userName, password, role, onlyIfNoUsers: false, ct).ConfigureAwait(false);
        return UserRecord.From(entity);
    }

    /// <summary>
    /// Creates a user. With <paramref name="onlyIfNoUsers"/> (first administrator) it fails with Precondition when a
    /// user exists, checked in the same write lock.
    /// </summary>
    public async Task<UserEntity> CreateAsync(string userName, string password, UserRole role, bool onlyIfNoUsers, CancellationToken ct)
    {
        var name = ValidateUserName(userName);
        if (PasswordError(password) is { } error)
        {
            throw new AuthException(AuthError.InvalidArgument, error);
        }

        if (!Enum.IsDefined(role))
        {
            throw new AuthException(AuthError.InvalidArgument, "Unknown role.");
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
                Role = role,
                CreatedUtc = time.GetUtcNow().UtcDateTime,
            };
            db.Users.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _hasUsers = true;
            Changed?.Invoke(this, EventArgs.Empty);
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
    public async Task<(UserRecord User, string Changes)> UpdateAsync(Guid id, UserRole? role, bool? disabled, string? newPassword, Guid? actorId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(newPassword) && PasswordError(newPassword) is { } error)
        {
            throw new AuthException(AuthError.InvalidArgument, error);
        }

        if (role is { } r && !Enum.IsDefined(r))
        {
            throw new AuthException(AuthError.InvalidArgument, "Unknown role.");
        }

        var hash = string.IsNullOrEmpty(newPassword) ? null : hasher.Hash(newPassword);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                ?? throw new AuthException(AuthError.NotFound, "The user does not exist (any more).");
            var changes = new List<string>();
            var roleChanges = role is { } newRole && newRole != user.Role;
            var stateChanges = disabled is { } newState && newState != user.Disabled;
            if ((roleChanges || stateChanges) && actorId == user.Id)
            {
                throw new AuthException(AuthError.Precondition, "You cannot change the role or state of your own account.");
            }

            if ((roleChanges && role != UserRole.Admin) || (stateChanges && disabled == true))
            {
                await RequireAnotherEnabledAdminAsync(db, user, ct).ConfigureAwait(false);
            }

            if (roleChanges)
            {
                user.Role = role!.Value;
                changes.Add("role " + user.Role);
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

            if (changes.Count > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tokens.RevokeUserAsync(user.Id, ct).ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return (UserRecord.From(user), string.Join(", ", changes));
        }
        finally
        {
            _write.Release();
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
            Changed?.Invoke(this, EventArgs.Empty);
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

    public void Dispose() => _write.Dispose();

    private static async Task RequireAnotherEnabledAdminAsync(OadmDbContext db, UserEntity user, CancellationToken ct)
    {
        if (user.Role != UserRole.Admin || user.Disabled)
        {
            return;
        }

        if (!await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == UserRole.Admin && !u.Disabled, ct).ConfigureAwait(false))
        {
            throw new AuthException(AuthError.Precondition, $"{user.UserName} is the last enabled administrator. Add or enable another administrator first.");
        }
    }
}
