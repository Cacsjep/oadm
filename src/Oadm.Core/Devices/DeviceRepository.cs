using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Oadm.Core.Persistence;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>Thrown when adding a device whose serial is already managed.</summary>
public sealed class DuplicateDeviceException(string serial)
    : InvalidOperationException($"A device with serial {serial} is already managed.")
{
    public string Serial { get; } = serial;
}

/// <summary>
/// Device table access. Every returned <see cref="Device"/> is a detached copy; mutate it and call
/// <see cref="UpdateAsync(Device, CancellationToken)"/>, or use <see cref="UpdateAsync(Guid, Action{Device}, CancellationToken)"/>.
/// All writes publish to <see cref="IDeviceChangeFeed"/>.
/// </summary>
public sealed class DeviceRepository(IDbContextFactory<OadmDbContext> dbFactory, IDeviceChangeFeed changeFeed)
    : IDeviceRepository
{
    public IDeviceChangeFeed Changes => changeFeed;

    public async Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Devices.AsNoTracking().OrderBy(d => d.Serial).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Device?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
    }

    /// <summary>Finds a device by serial; the argument is normalized first (any MAC notation is accepted).</summary>
    public async Task<Device?> FindBySerialAsync(string serial, CancellationToken ct)
    {
        if (!DeviceSerial.TryNormalize(serial, out var normalized))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Serial == normalized, ct).ConfigureAwait(false);
    }

    /// <summary>Adds a device. The serial is normalized in place.</summary>
    /// <exception cref="DuplicateDeviceException">Serial already exists.</exception>
    public async Task<Device> AddAsync(Device device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.Serial = DeviceSerial.Normalize(device.Serial);
        if (device.Id == Guid.Empty)
        {
            device.Id = Guid.NewGuid();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.Devices.AnyAsync(d => d.Serial == device.Serial, ct).ConfigureAwait(false))
        {
            throw new DuplicateDeviceException(device.Serial);
        }

        var entity = device.Clone();
        db.Devices.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Lost a race against a concurrent add of the same serial (SQLITE_CONSTRAINT).
            throw new DuplicateDeviceException(device.Serial);
        }

        changeFeed.Publish(new DeviceChange(DeviceChangeKind.Added, entity.Id, entity.Clone()));
        return entity.Clone();
    }

    /// <summary>Overwrites all columns of an existing device.</summary>
    /// <exception cref="KeyNotFoundException">Device does not exist.</exception>
    /// <exception cref="DuplicateDeviceException">Serial changed to one that already exists.</exception>
    public async Task<Device> UpdateAsync(Device device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var snapshot = device.Clone();
        return await UpdateAsync(device.Id, d => CopyColumns(snapshot, d), ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {device.Id} not found.");
    }

    /// <summary>Loads the device, applies <paramref name="mutate"/>, saves. Returns null if the device does not exist.</summary>
    public async Task<Device?> UpdateAsync(Guid id, Action<Device> mutate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        mutate(entity);
        entity.Id = id;
        entity.Serial = DeviceSerial.Normalize(entity.Serial);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new DuplicateDeviceException(entity.Serial);
        }

        changeFeed.Publish(new DeviceChange(DeviceChangeKind.Updated, id, entity.Clone()));
        return entity.Clone();
    }

    /// <summary>Removes a device and (by cascade) its credentials. Returns false if it did not exist.</summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        db.Devices.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        changeFeed.Publish(new DeviceChange(DeviceChangeKind.Removed, id, null));
        return true;
    }

    async Task<IReadOnlyList<IDeviceInfo>> IDeviceRepository.ListAsync(CancellationToken ct) =>
        await ListDevicesAsync(ct).ConfigureAwait(false);

    async Task<IDeviceInfo?> IDeviceRepository.FindAsync(Guid id, CancellationToken ct) =>
        await GetAsync(id, ct).ConfigureAwait(false);

    private static void CopyColumns(Device from, Device to)
    {
        to.Serial = from.Serial;
        to.Address = from.Address;
        to.UseHostName = from.UseHostName;
        to.HostName = from.HostName;
        to.Model = from.Model;
        to.FirmwareVersion = from.FirmwareVersion;
        to.DhcpEnabled = from.DhcpEnabled;
        to.HttpsEnabled = from.HttpsEnabled;
        to.Dot1xEnabled = from.Dot1xEnabled;
        to.UpnpFriendlyName = from.UpnpFriendlyName;
        to.ServerName = from.ServerName;
        to.Status = from.Status;
        to.Scheme = from.Scheme;
        to.CertFingerprintSha256 = from.CertFingerprintSha256;
        to.LastSeenUtc = from.LastSeenUtc;
        to.WarrantyExpiry = from.WarrantyExpiry;
        to.ReplacementModel = from.ReplacementModel;
        to.Tags = [.. from.Tags];
    }
}
