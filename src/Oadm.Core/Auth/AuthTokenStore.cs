using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>A valid session: the user behind a token.</summary>
public sealed record AuthSession(string TokenHash, Guid UserId, string UserName, Guid RoleId, DateTime ExpiresUtc)
{
    public UserRole Role => BuiltInRoles.IsAdministrator(RoleId) ? UserRole.Admin : UserRole.Operator;
}

/// <summary>
/// Login sessions (table AuthTokens). A token is 32 random bytes (base64url) handed to the client once; the server
/// keeps only its SHA-256. Sliding expiry: 8 hours, 30 days with "Remember me"; every use moves the expiry forward
/// (written to the database at most once per minute per token). Valid sessions are cached in memory, so a call costs
/// no database read; disabling or deleting a user or a new password revokes its sessions (<see cref="RevokeUserAsync"/>),
/// a new role is taken over by them (<see cref="SetRole"/>).
/// </summary>
public sealed class AuthTokenStore(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider time)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan RememberLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);

    // Counts revokes and role changes. A session read from the database while one happened may be stale: it is read again.
    private long _epoch;

    /// <summary>Lower-case hex SHA-256 of a token.</summary>
    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    /// <summary>Creates a session for the user; returns the token (only now in clear text).</summary>
    public async Task<string> CreateAsync(UserEntity user, bool remember, string clientAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow().UtcDateTime;
        var entity = new AuthTokenEntity
        {
            TokenHash = HashOf(token),
            UserId = user.Id,
            ClientAddress = clientAddress.Length > 64 ? clientAddress[..64] : clientAddress,
            Remember = remember,
            CreatedUtc = now,
            ExpiresUtc = now + Lifetime(remember),
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.AuthTokens.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _cache[entity.TokenHash] = new Cached(user.Id, user.UserName, user.RoleId, remember, entity.ExpiresUtc, entity.ExpiresUtc);
        return token;
    }

    /// <summary>The session of a token, or null when it is unknown, expired, revoked or its user is disabled.</summary>
    public async Task<AuthSession?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            return null;
        }

        var hash = HashOf(token);
        while (true)
        {
            var (done, session) = await TryValidateAsync(hash, ct).ConfigureAwait(false);
            if (done)
            {
                return session;
            }
        }
    }

    /// <summary>
    /// One attempt; not done when a revoke or role change ran at the same time (the cache entry changed under it), so a
    /// revoked session or an old role never goes back into the cache.
    /// </summary>
    private async Task<(bool Done, AuthSession? Session)> TryValidateAsync(string hash, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (!_cache.TryGetValue(hash, out var cached))
        {
            var epoch = Interlocked.Read(ref _epoch);
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await (from t in db.AuthTokens.AsNoTracking()
                             join u in db.Users.AsNoTracking() on t.UserId equals u.Id
                             where t.TokenHash == hash && !u.Disabled
                             select new { t.Remember, t.ExpiresUtc, u.Id, u.UserName, u.RoleId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null)
            {
                return (true, null);
            }

            cached = new Cached(row.Id, row.UserName, row.RoleId, row.Remember, row.ExpiresUtc, row.ExpiresUtc);
            _cache[hash] = cached;
            if (Interlocked.Read(ref _epoch) != epoch)
            {
                _cache.TryRemove(new KeyValuePair<string, Cached>(hash, cached));
                return (false, null);
            }
        }

        if (cached.ExpiresUtc <= now)
        {
            _cache.TryRemove(hash, out _);
            await DeleteAsync(hash, ct).ConfigureAwait(false);
            return (true, null);
        }

        var original = cached;

        // Sliding expiry: in memory now, in the database at most once per minute.
        var expires = now + Lifetime(cached.Remember);
        cached = cached with { ExpiresUtc = expires };
        if (expires - cached.PersistedExpiresUtc >= PersistEvery)
        {
            cached = cached with { PersistedExpiresUtc = expires };
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.AuthTokens.Where(t => t.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresUtc, expires), ct).ConfigureAwait(false);
        }

        if (!_cache.TryUpdate(hash, cached, original))
        {
            return (false, null);
        }

        return (true, new AuthSession(hash, cached.UserId, cached.UserName, cached.RoleId, expires));
    }

    /// <summary>Logout: ends one session.</summary>
    public async Task RevokeAsync(string tokenHash, CancellationToken ct)
    {
        Interlocked.Increment(ref _epoch);
        _cache.TryRemove(tokenHash, out _);
        await DeleteAsync(tokenHash, ct).ConfigureAwait(false);
    }

    /// <summary>The user's sessions take over a new role (sessions not cached read it from the database).</summary>
    public void SetRole(Guid userId, Guid roleId)
    {
        Interlocked.Increment(ref _epoch);
        foreach (var pair in _cache.Where(p => p.Value.UserId == userId).ToList())
        {
            _cache.TryUpdate(pair.Key, pair.Value with { RoleId = roleId }, pair.Value);
        }
    }

    /// <summary>Ends every session of a user (disabled, deleted, password changed).</summary>
    public async Task RevokeUserAsync(Guid userId, CancellationToken ct)
    {
        Interlocked.Increment(ref _epoch);
        foreach (var pair in _cache.Where(p => p.Value.UserId == userId).ToList())
        {
            _cache.TryRemove(pair.Key, out _);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AuthTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes expired sessions (hourly).</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var pair in _cache.Where(p => p.Value.ExpiresUtc <= now).ToList())
        {
            _cache.TryRemove(pair.Key, out _);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Sliding expiries newer than the stored value live in the cache; only rows expired in both are deleted.
        var live = _cache.Keys.ToList();
        return await db.AuthTokens.Where(t => t.ExpiresUtc <= now && !live.Contains(t.TokenHash)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static TimeSpan Lifetime(bool remember) => remember ? RememberLifetime : SessionLifetime;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task DeleteAsync(string hash, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AuthTokens.Where(t => t.TokenHash == hash).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private sealed record Cached(Guid UserId, string UserName, Guid RoleId, bool Remember, DateTime ExpiresUtc, DateTime PersistedExpiresUtc);
}
