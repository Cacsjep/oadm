using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.Security;

/// <summary>One entry of the technician's credential list (row of CredentialListEntries).</summary>
public sealed class CredentialListEntry
{
    public Guid Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>Output of <see cref="CredentialProtector.Protect"/> with the entry id as associated data.</summary>
#pragma warning disable CA1819 // EF entity blob column
    public byte[] EncryptedPassword { get; set; } = [];
#pragma warning restore CA1819

    public DateTime CreatedUtc { get; set; }

    public override string ToString() => $"CredentialListEntry {Id} ({UserName})";
}

/// <summary>A credential list entry without its password, as shown to clients.</summary>
public sealed record CredentialListItem(Guid Id, string UserName, DateTime CreatedUtc);

/// <summary>A decrypted credential list entry. Server memory only; <see cref="ToString"/> redacts the password.</summary>
public sealed record DecryptedCredential(Guid Id, string UserName, string Password)
{
    public override string ToString() => $"DecryptedCredential {{ Id = {Id}, UserName = {UserName}, Password = *** }}";
}

/// <summary>
/// The encrypted credential list (Settings page): user name + password pairs the server tries on
/// discovered devices when adding them. Passwords are encrypted with the master key (AES-256-GCM,
/// entry id as associated data); clients see ids and user names, a password only through the explicit
/// <see cref="RevealAsync"/> (Settings page eye button, user decision 2026-10-08).
/// </summary>
public sealed class CredentialListStore(IDbContextFactory<OadmDbContext> dbFactory, CredentialProtector protector, TimeProvider time)
{
    /// <summary>Most entries the list may hold; every entry may be tried on every discovered device.</summary>
    public const int MaxEntries = 20;

    public const int MaxUserNameLength = 64;

    /// <summary>Raised after an entry was added or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Entries in the order they were added (also the order they are tried in).</summary>
    public async Task<IReadOnlyList<CredentialListItem>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.CredentialListEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rows.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).Select(r => new CredentialListItem(r.Id, r.UserName, r.CreatedUtc)).ToList();
    }

    /// <summary>
    /// Adds an entry. When the same user name and password are already in the list, the existing
    /// entry is returned instead of a duplicate.
    /// </summary>
    /// <exception cref="ArgumentException">Empty or too long user name, password not 1-64 printable ASCII characters.</exception>
    /// <exception cref="InvalidOperationException">The list already holds <see cref="MaxEntries"/> entries.</exception>
    public async Task<CredentialListItem> AddAsync(string userName, string password, CancellationToken ct)
    {
        var user = (userName ?? string.Empty).Trim();
        if (user.Length is 0 or > MaxUserNameLength)
        {
            throw new ArgumentException($"The user name must be 1-{MaxUserNameLength} characters.", nameof(userName));
        }

        Vapix.VapixClient.ValidatePassword(password);

        foreach (var existing in await GetAllAsync(ct).ConfigureAwait(false))
        {
            if (existing.UserName == user && existing.Password == password)
            {
                return (await ListAsync(ct).ConfigureAwait(false)).First(e => e.Id == existing.Id);
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.CredentialListEntries.CountAsync(ct).ConfigureAwait(false) >= MaxEntries)
        {
            throw new InvalidOperationException($"The credential list holds at most {MaxEntries} entries.");
        }

        var id = Guid.NewGuid();
        var entry = new CredentialListEntry
        {
            Id = id,
            UserName = user,
            EncryptedPassword = protector.Protect(password, id.ToByteArray()),
            CreatedUtc = time.GetUtcNow().UtcDateTime,
        };
        db.CredentialListEntries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return new CredentialListItem(entry.Id, entry.UserName, entry.CreatedUtc);
    }

    /// <returns>False when no entry has this id.</returns>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var removed = await db.CredentialListEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed > 0;
    }

    /// <summary>
    /// One entry decrypted, for the explicit reveal on the Settings page (SettingsService.RevealCredential).
    /// Null when no entry has this id or it cannot be decrypted (other master key).
    /// </summary>
    public async Task<DecryptedCredential?> RevealAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.CredentialListEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        try
        {
            return new DecryptedCredential(row.Id, row.UserName, protector.Unprotect(row.EncryptedPassword, row.Id.ToByteArray()));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>All entries decrypted, oldest first. Server side only (automatic login).</summary>
    /// <remarks>An entry that cannot be decrypted (other master key) is skipped.</remarks>
    public async Task<IReadOnlyList<DecryptedCredential>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.CredentialListEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var result = new List<DecryptedCredential>(rows.Count);
        foreach (var row in rows.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id))
        {
            try
            {
                result.Add(new DecryptedCredential(row.Id, row.UserName, protector.Unprotect(row.EncryptedPassword, row.Id.ToByteArray())));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // wrong master key or tampered row: never usable, so never tried
            }
        }

        return result;
    }
}
