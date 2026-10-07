using Microsoft.EntityFrameworkCore;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;

namespace Oadm.Core.Security;

/// <summary>Decrypted credentials. Never logged: <see cref="ToString"/> redacts the password.</summary>
public sealed class DeviceCredentials(string userName, string password)
{
    public string UserName { get; } = userName;
    public string Password { get; } = password;

    public override string ToString() => $"DeviceCredentials {{ UserName = {UserName}, Password = *** }}";
}

/// <summary>Encrypted per-device credentials. Plaintext passwords exist only in memory on the server.</summary>
public sealed class CredentialStore(
    IDbContextFactory<OadmDbContext> dbFactory,
    CredentialProtector protector,
    IDeviceChangeFeed changeFeed)
{
    /// <summary>Stores (or replaces) the credentials of a device and publishes a device Updated change.</summary>
    /// <exception cref="KeyNotFoundException">The device does not exist.</exception>
    public async Task SetAsync(Guid deviceId, string userName, string password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");

        var encrypted = protector.Protect(password, deviceId.ToByteArray());
        var existing = await db.DeviceCredentials.FirstOrDefaultAsync(c => c.DeviceId == deviceId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.DeviceCredentials.Add(new DeviceCredential { DeviceId = deviceId, UserName = userName, EncryptedPassword = encrypted });
        }
        else
        {
            existing.UserName = userName;
            existing.EncryptedPassword = encrypted;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        changeFeed.Publish(new DeviceChange(DeviceChangeKind.Updated, deviceId, device));
    }

    /// <summary>Returns the decrypted credentials, or null if none are stored.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Stored blob cannot be decrypted (wrong master key or tampered).</exception>
    public async Task<DeviceCredentials?> GetAsync(Guid deviceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.DeviceCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.DeviceId == deviceId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return new DeviceCredentials(row.UserName, protector.Unprotect(row.EncryptedPassword, deviceId.ToByteArray()));
    }

    public async Task<bool> HasCredentialsAsync(Guid deviceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DeviceCredentials.AnyAsync(c => c.DeviceId == deviceId, ct).ConfigureAwait(false);
    }

    /// <summary>Ids of all devices that have stored credentials (for the gRPC "has credentials" flag).</summary>
    public async Task<IReadOnlySet<Guid>> ListDeviceIdsWithCredentialsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await db.DeviceCredentials.AsNoTracking().Select(c => c.DeviceId).ToListAsync(ct).ConfigureAwait(false);
        return ids.ToHashSet();
    }

    /// <summary>
    /// The distinct user name + password pairs stored for managed devices, each with one device
    /// that uses it, most used first. Server side only: the add page tries them on new devices.
    /// Rows that cannot be decrypted are skipped.
    /// </summary>
    public async Task<IReadOnlyList<(Guid DeviceId, DeviceCredentials Credentials)>> ListDistinctAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.DeviceCredentials.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var groups = new Dictionary<(string User, string Password), (Guid DeviceId, int Count)>();
        foreach (var row in rows)
        {
            string password;
            try
            {
                password = protector.Unprotect(row.EncryptedPassword, row.DeviceId.ToByteArray());
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                continue;
            }

            var key = (row.UserName, password);
            groups[key] = groups.TryGetValue(key, out var seen) ? (seen.DeviceId, seen.Count + 1) : (row.DeviceId, 1);
        }

        return groups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Value.DeviceId)
            .Select(g => (g.Value.DeviceId, new DeviceCredentials(g.Key.User, g.Key.Password)))
            .ToList();
    }

    public async Task<bool> RemoveAsync(Guid deviceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var removed = await db.DeviceCredentials.Where(c => c.DeviceId == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (removed == 0)
        {
            return false;
        }

        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct).ConfigureAwait(false);
        if (device is not null)
        {
            changeFeed.Publish(new DeviceChange(DeviceChangeKind.Updated, deviceId, device));
        }

        return true;
    }
}
