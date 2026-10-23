using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.Devices;

/// <summary>
/// Publishes devices as they are stored after a write (docs/spec/device-scope.md, Sessions): a writer never publishes the
/// copy it read before its write, which may miss a newer write of another column (a polling round that read the tags before
/// a tag change would publish the old tags, and the device would go back into the old group's grids until its next change).
/// The row is read again after the save and published while one gate per feed is held, so reads and publishes happen in
/// the same order: the last publish of a device always carries what the table holds after its last write. Every writer of
/// the device table goes through it (<see cref="DeviceRepository"/>, <see cref="DeviceTagStore"/>,
/// <see cref="Security.CredentialStore"/>, the polling's status note). The read and the publish ignore the caller's
/// cancellation: the write is done, its change must go out.
/// </summary>
public static class StoredDevicePublisher
{
    private const int Chunk = 500;

    private static readonly ConditionalWeakTable<IDeviceChangeFeed, SemaphoreSlim> Gates = [];

    /// <summary>Reads the devices of <paramref name="ids"/> as stored now and publishes them as Updated (Added for <paramref name="kind"/> Added); devices no longer stored are skipped.</summary>
    /// <returns>The devices published, as stored.</returns>
    public static async Task<IReadOnlyList<Device>> PublishStoredAsync(IDbContextFactory<OadmDbContext> dbFactory, IDeviceChangeFeed feed,
        IReadOnlyCollection<Guid> ids, DeviceChangeKind kind = DeviceChangeKind.Updated)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var gate = Gates.GetValue(feed, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var stored = new List<Device>(ids.Count);
            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
            {
                foreach (var chunk in ids.Distinct().Chunk(Chunk))
                {
                    stored.AddRange(await db.Devices.AsNoTracking().Where(d => chunk.Contains(d.Id)).ToListAsync(CancellationToken.None).ConfigureAwait(false));
                }
            }

            if (stored.Count == 1)
            {
                feed.Publish(new DeviceChange(kind, stored[0].Id, stored[0].Clone()));
            }
            else if (stored.Count > 1)
            {
                feed.PublishMany([.. stored.Select(d => new DeviceChange(kind, d.Id, d.Clone()))]);
            }

            return stored;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Publishes the removal of <paramref name="ids"/> behind the same gate (a stored copy read before never brings them back).</summary>
    public static async Task PublishRemovedAsync(IDeviceChangeFeed feed, IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        var gate = Gates.GetValue(feed, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var id in ids)
            {
                feed.Publish(new DeviceChange(DeviceChangeKind.Removed, id, null));
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
