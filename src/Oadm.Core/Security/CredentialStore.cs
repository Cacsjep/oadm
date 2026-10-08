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

    /// <summary>
    /// Stores (or replaces) the same credentials on many devices in one transaction (one write for 5,000
    /// devices instead of 5,000), then publishes one device Updated change per device.
    /// </summary>
    /// <exception cref="KeyNotFoundException">One of the devices does not exist; nothing is stored.</exception>
    public async Task SetManyAsync(IReadOnlyCollection<Guid> deviceIds, string userName, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);

        var ids = deviceIds.Distinct().ToList();
        var devices = new List<Device>(ids.Count);
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var chunk in ids.Chunk(500))
            {
                var found = await db.Devices.AsNoTracking().Where(d => chunk.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false);
                if (found.Count != chunk.Length)
                {
                    var missing = chunk.First(id => !found.Exists(d => d.Id == id));
                    throw new KeyNotFoundException($"Device {missing} not found.");
                }

                var existing = await db.DeviceCredentials.Where(c => chunk.Contains(c.DeviceId))
                    .ToDictionaryAsync(c => c.DeviceId, ct).ConfigureAwait(false);
                foreach (var id in chunk)
                {
                    var encrypted = protector.Protect(password, id.ToByteArray());
                    if (existing.TryGetValue(id, out var row))
                    {
                        row.UserName = userName;
                        row.EncryptedPassword = encrypted;
                    }
                    else
                    {
                        db.DeviceCredentials.Add(new DeviceCredential { DeviceId = id, UserName = userName, EncryptedPassword = encrypted });
                    }
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                devices.AddRange(found);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        foreach (var device in devices)
        {
            changeFeed.Publish(new DeviceChange(DeviceChangeKind.Updated, device.Id, device));
        }
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

    /// <summary>The stored user names of the given devices (devices without credentials are missing). Never a password.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ListUserNamesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var names = new Dictionary<Guid, string>();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var chunk in deviceIds.Distinct().Chunk(500))
        {
            var rows = await db.DeviceCredentials.AsNoTracking().Where(c => chunk.Contains(c.DeviceId))
                .Select(c => new { c.DeviceId, c.UserName }).ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                names[row.DeviceId] = row.UserName;
            }
        }

        return names;
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
