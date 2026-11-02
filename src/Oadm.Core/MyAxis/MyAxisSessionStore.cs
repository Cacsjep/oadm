using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Core.Security;

namespace Oadm.Core.MyAxis;

/// <summary>A user's MyAxis state as the client sees it (never the session).</summary>
public sealed record MyAxisState(bool SignedIn, DateTime? SignedInUtc, bool Expired)
{
    public static readonly MyAxisState None = new(false, null, false);
}

/// <summary>
/// The users' MyAxis sign-ins (docs/spec/myaxis.md "On the server"): one row per user, the cookie header encrypted with the
/// master key and the user id as associated data, so a row copied to another user does not decrypt. Each method touches
/// only the given user's row.
/// </summary>
public sealed class MyAxisSessionStore(IDbContextFactory<OadmDbContext> dbFactory, CredentialProtector protector, TimeProvider time)
{
    /// <summary>The user's state for <c>MyAxisService.Get</c>.</summary>
    public async Task<MyAxisState> GetStateAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.MyAxisSessions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        return row is null ? MyAxisState.None
            : row.EncryptedSession is null ? new MyAxisState(false, null, row.Expired)
            : new MyAxisState(true, row.SignedInUtc, false);
    }

    /// <summary>The user's cookie header for a request to axis.com; null when not signed in (or another master key).</summary>
    internal async Task<string?> GetSessionAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var blob = await db.MyAxisSessions.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.EncryptedSession)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (blob is null)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(blob, AssociatedData(userId));
        }
        catch (CryptographicException)
        {
            return null; // another master key: the user signs in again
        }
    }

    /// <summary>True when the user has a kept session.</summary>
    public async Task<bool> HasSessionAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.MyAxisSessions.AnyAsync(s => s.UserId == userId && s.EncryptedSession != null, ct).ConfigureAwait(false);
    }

    /// <summary>Keeps the session axis.com just accepted (replaces an older one, clears Expired).</summary>
    public async Task SaveAsync(Guid userId, string session, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(session);
        var now = time.GetUtcNow().UtcDateTime;
        var blob = protector.Protect(session, AssociatedData(userId));
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.MyAxisSessions.FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new MyAxisSessionEntity { UserId = userId };
            db.MyAxisSessions.Add(row);
        }

        row.EncryptedSession = blob;
        row.SignedInUtc = now;
        row.LastAcceptedUtc = now;
        row.Expired = false;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A download with the session worked.</summary>
    internal async Task TouchAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.MyAxisSessions.Where(s => s.UserId == userId && s.EncryptedSession != null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastAcceptedUtc, now), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// axis.com refused <paramref name="session"/>: deletes it and marks the user's sign-in expired. A newer session the user
    /// kept meanwhile stays.
    /// </summary>
    public async Task ExpireAsync(Guid userId, string session, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.MyAxisSessions.FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        if (row?.EncryptedSession is null)
        {
            return;
        }

        string? kept;
        try
        {
            kept = protector.Unprotect(row.EncryptedSession, AssociatedData(userId));
        }
        catch (CryptographicException)
        {
            kept = null;
        }

        if (kept is not null && !string.Equals(kept, session, StringComparison.Ordinal))
        {
            return;
        }

        row.EncryptedSession = null;
        row.Expired = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sign out: deletes the user's row. True when there was one.</summary>
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.MyAxisSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Binds a blob to its user: copied to another user's row it does not decrypt.</summary>
    internal static byte[] AssociatedData(Guid userId) => Encoding.UTF8.GetBytes("OADM MyAxis session " + userId.ToString("D"));

    public override string ToString() => nameof(MyAxisSessionStore);
}
