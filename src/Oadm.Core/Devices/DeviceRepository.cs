using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Oadm.Core.Persistence;
using Oadm.Core.Sites;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>Thrown when adding a device whose serial is already managed.</summary>
public sealed class DuplicateDeviceException(string serial)
    : InvalidOperationException($"A device with serial {serial} is already managed.")
{
    public string Serial { get; } = serial;
}

/// <summary>Thrown when an add would take the server over <see cref="DeviceLimits.MaxDevices"/>.</summary>
public sealed class DeviceLimitException() : InvalidOperationException(DeviceLimits.TooManyDevicesText);

/// <summary>
/// Device table access. Every returned <see cref="Device"/> is a detached copy; mutate it and call
/// <see cref="UpdateAsync(Device, CancellationToken)"/>, or use <see cref="UpdateAsync(Guid, Action{Device}, CancellationToken)"/>.
/// All writes publish to <see cref="IDeviceChangeFeed"/>, except a write that only moves LastSeenUtc.
/// Every read shows the computed status (<see cref="SiteDeviceStatus"/>): <see cref="DeviceStatus.SiteOffline"/> while the
/// device's site is not online, with <see cref="Device.SiteName"/> filled; the stored status stays the last real one and
/// Site offline is never written.
/// </summary>
public sealed class DeviceRepository(IDbContextFactory<OadmDbContext> dbFactory, IDeviceChangeFeed changeFeed, ISiteStates? sites = null)
    : IDeviceRepository, IDisposable
{
    private readonly SemaphoreSlim _addLock = new(1, 1);
    private readonly ISiteStates _sites = sites ?? NoSiteStates.Instance;

    /// <summary>The sites the computed status comes from.</summary>
    public ISiteStates Sites => _sites;

    private Device Shown(Device device)
    {
        SiteDeviceStatus.Apply(device, _sites);
        return device;
    }

    public void Dispose() => _addLock.Dispose();

    public IDeviceChangeFeed Changes => changeFeed;

    /// <summary>The most devices <see cref="AddAsync"/> stores (<see cref="DeviceLimits.MaxDevices"/>; tests lower it).</summary>
    public int MaxDevices { get; set; } = DeviceLimits.MaxDevices;

    /// <summary>Serial numbers of all managed devices (normalized), without loading the rows.</summary>
    public async Task<IReadOnlySet<string>> ListSerialsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Devices.AsNoTracking().Select(d => d.Serial).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Address and serial of every device, without loading the rows (the waiting list's check per round).</summary>
    public async Task<IReadOnlyList<ManagedAddress>> ListAddressesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Devices.AsNoTracking().Select(d => new { d.Serial, d.Address, d.SiteId }).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new ManagedAddress(r.Serial, r.Address, r.SiteId))];
    }

    /// <summary>Number of managed devices.</summary>
    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Devices.CountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>All devices with <see cref="Device.CredentialUserName"/> filled.</summary>
    public async Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.Serial).ToListAsync(ct).ConfigureAwait(false);
        var users = await db.DeviceCredentials.AsNoTracking()
            .ToDictionaryAsync(c => c.DeviceId, c => c.UserName, ct).ConfigureAwait(false);
        foreach (var device in devices)
        {
            device.CredentialUserName = users.GetValueOrDefault(device.Id);
            Shown(device);
        }

        return devices;
    }

    /// <summary>
    /// The devices of <paramref name="siteIds"/> with their computed status (a site went offline or online). Uses the
    /// SiteId index; nothing is written.
    /// </summary>
    public async Task<IReadOnlyList<Device>> ListBySitesAsync(IReadOnlyCollection<Guid> siteIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteIds);
        if (siteIds.Count == 0)
        {
            return [];
        }

        var ids = siteIds.Select(id => (Guid?)id).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var devices = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.SiteId)).OrderBy(d => d.Serial).ToListAsync(ct).ConfigureAwait(false);
        foreach (var device in devices)
        {
            Shown(device);
        }

        return devices;
    }

    /// <summary>Ids of the devices of <paramref name="siteIds"/>, without loading the rows.</summary>
    public async Task<IReadOnlyList<Guid>> ListIdsBySitesAsync(IReadOnlyCollection<Guid> siteIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteIds);
        if (siteIds.Count == 0)
        {
            return [];
        }

        var ids = siteIds.Select(id => (Guid?)id).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Devices.AsNoTracking().Where(d => ids.Contains(d.SiteId)).OrderBy(d => d.Serial).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Up to <paramref name="take"/> devices after <paramref name="afterSerial"/> (null: from the start) in serial order, with
    /// <see cref="Device.CredentialUserName"/> filled: the device list in pages, so 30,000 devices are never all in memory
    /// at once for one reader.
    /// </summary>
    public async Task<IReadOnlyList<Device>> ListDevicesPageAsync(string? afterSerial, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Devices.AsNoTracking();
        if (afterSerial is not null)
        {
#pragma warning disable CA1309 // Translated to SQL, where SQLite compares the serials byte by byte (ordinal).
            query = query.Where(d => string.Compare(d.Serial, afterSerial) > 0);
#pragma warning restore CA1309
        }

        var devices = await query.OrderBy(d => d.Serial).Take(take).ToListAsync(ct).ConfigureAwait(false);
        var ids = devices.Select(d => d.Id).ToList();
        var users = await db.DeviceCredentials.AsNoTracking().Where(c => ids.Contains(c.DeviceId))
            .ToDictionaryAsync(c => c.DeviceId, c => c.UserName, ct).ConfigureAwait(false);
        foreach (var device in devices)
        {
            device.CredentialUserName = users.GetValueOrDefault(device.Id);
            Shown(device);
        }

        return devices;
    }

    /// <summary>One device with <see cref="Device.CredentialUserName"/> filled, or null.</summary>
    public async Task<Device?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (device is not null)
        {
            device.CredentialUserName = await db.DeviceCredentials.AsNoTracking()
                .Where(c => c.DeviceId == id).Select(c => c.UserName)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            Shown(device);
        }

        return device;
    }

    /// <summary>
    /// The devices of <paramref name="ids"/> that exist, with <see cref="Device.CredentialUserName"/> filled (read 500 ids at
    /// a time; unknown ids are left out, so a caller compares the counts). Order: by serial.
    /// </summary>
    public async Task<IReadOnlyList<Device>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var result = new List<Device>(ids.Count);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            var devices = await db.Devices.AsNoTracking().Where(d => chunk.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false);
            var users = await db.DeviceCredentials.AsNoTracking().Where(c => chunk.Contains(c.DeviceId))
                .ToDictionaryAsync(c => c.DeviceId, c => c.UserName, ct).ConfigureAwait(false);
            foreach (var device in devices)
            {
                device.CredentialUserName = users.GetValueOrDefault(device.Id);
                result.Add(Shown(device));
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Serial, b.Serial));
        return result;
    }

    /// <summary>
    /// What device groups need of every device (id, serial, site, tags, who added it), without loading the rows: the
    /// allowed ids of a view and the counts of the group editor.
    /// </summary>
    public async Task<IReadOnlyList<DeviceScopeRow>> ListScopeRowsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Devices.AsNoTracking()
            .Select(d => new { d.Id, d.Serial, d.SiteId, d.Tags, d.AddedByAllDevices, d.AddedByGroupId })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new DeviceScopeRow(r.Id, r.Serial, r.SiteId, r.Tags, r.AddedByAllDevices, r.AddedByGroupId))];
    }

    /// <summary>Finds a device by serial; the argument is normalized first (any MAC notation is accepted).</summary>
    public async Task<Device?> FindBySerialAsync(string serial, CancellationToken ct)
    {
        if (!DeviceSerial.TryNormalize(serial, out var normalized))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Serial == normalized, ct).ConfigureAwait(false);
        return device is null ? null : Shown(device);
    }

    /// <summary>Adds a device. The serial is normalized in place.</summary>
    /// <exception cref="DuplicateDeviceException">Serial already exists.</exception>
    /// <exception cref="DeviceLimitException">The server manages <see cref="MaxDevices"/> devices already.</exception>
    public async Task<Device> AddAsync(Device device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.Serial = DeviceSerial.Normalize(device.Serial);
        if (device.Id == Guid.Empty)
        {
            device.Id = Guid.NewGuid();
        }

        var entity = device.Clone();
        if (entity.Status == DeviceStatus.SiteOffline)
        {
            entity.Status = DeviceStatus.Unknown; // computed, never stored
        }

        // Adds are serialized, so concurrent adds cannot pass the limit together.
        await _addLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (await db.Devices.AnyAsync(d => d.Serial == device.Serial, ct).ConfigureAwait(false))
            {
                throw new DuplicateDeviceException(device.Serial);
            }

            if (await db.Devices.CountAsync(ct).ConfigureAwait(false) >= MaxDevices)
            {
                throw new DeviceLimitException();
            }

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
        }
        finally
        {
            _addLock.Release();
        }

        changeFeed.Publish(new DeviceChange(DeviceChangeKind.Added, entity.Id, entity.Clone()));
        return Shown(entity.Clone());
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

        var storedStatus = entity.Status;
        mutate(entity);
        if (entity.Status == DeviceStatus.SiteOffline)
        {
            // Computed, never stored: a copy read while the site was offline keeps the last real status.
            entity.Status = storedStatus;
        }

        entity.Id = id;
        entity.Serial = DeviceSerial.Normalize(entity.Serial);

        // Scale: a status poll of 5,000 devices every minute usually changes nothing but LastSeenUtc.
        // Unchanged rows are not written; a LastSeenUtc-only change is written but not published, so
        // watchers (every client) do not receive 5,000 identical devices per poll interval.
        db.ChangeTracker.DetectChanges();
        var modified = db.Entry(entity).Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList();
        if (modified.Count == 0)
        {
            return Shown(entity.Clone());
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new DuplicateDeviceException(entity.Serial);
        }

        if (modified is not [nameof(Device.LastSeenUtc)])
        {
            changeFeed.Publish(new DeviceChange(DeviceChangeKind.Updated, id, entity.Clone()));
        }

        return Shown(entity.Clone());
    }

    /// <summary>Ids of all devices, without loading the rows (cheap for 5,000 devices).</summary>
    public async Task<IReadOnlyList<Guid>> ListDeviceIdsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Devices.AsNoTracking().OrderBy(d => d.Serial).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ids and computed statuses of all devices in serial order, without loading the rows.</summary>
    public async Task<IReadOnlyList<(Guid Id, DeviceStatus Status)>> ListIdsWithStatusAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Devices.AsNoTracking().OrderBy(d => d.Serial).Select(d => new { d.Id, d.Status, d.SiteId }).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => (r.Id, SiteDeviceStatus.Of(r.Status, r.SiteId, _sites)))];
    }

    /// <summary>
    /// Removes many devices (and by cascade their credentials) in one transaction, then publishes one
    /// Removed per device. Returns the ids that existed.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RemoveManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var removed = new List<Guid>(ids.Count);
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Chunked: SQLite allows a limited number of parameters per statement.
            foreach (var chunk in ids.Distinct().Chunk(500))
            {
                var existing = await db.Devices.Where(d => chunk.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
                if (existing.Count == 0)
                {
                    continue;
                }

                await db.DeviceCredentials.Where(c => existing.Contains(c.DeviceId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.Devices.Where(d => existing.Contains(d.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                removed.AddRange(existing);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        foreach (var id in removed)
        {
            changeFeed.Publish(new DeviceChange(DeviceChangeKind.Removed, id, null));
        }

        return removed;
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
