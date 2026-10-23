using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.Devices;

/// <summary>
/// Publishes devices as they are stored after a write (docs/spec/device-scope.md, Sessions): a writer never publishes the
/// copy it read before its write, which may miss a newer write of another column (a polling round that read the tags before
/// a tag change would publish the old tags, and the device would go back into the old group's grids until its next change).
/// Every writer of the device table goes through it (<see cref="DeviceRepository"/>, <see cref="DeviceTagStore"/>,
/// <see cref="Security.CredentialStore"/>, the polling's status note), in one of two ways:
/// <list type="bullet">
/// <item><see cref="PublishStoredAsync"/> after the commit: the rows are read again and published while the gate of each
/// device is held, so reads and publishes of a device happen in the same order.</item>
/// <item><see cref="HoldAsync"/> before a write transaction (batch writers): the gates of every device are held from
/// before the transaction begins (SQLite's write lock is taken after the gates, never the other way round) until the rows
/// the transaction read and wrote are published after its commit. No other writer can change those rows between the
/// transaction's read and its commit, and no other publish of them can come between the commit and this one, so the
/// written rows are what the table holds: no second read (30,000 devices: one read of the rows instead of two).</item>
/// </list>
/// The last publish of a device always carries what the table holds after its last write. The gates are striped by device
/// id (<see cref="Stripes"/>), so publishes of different devices (a polling round) do not wait for each other. The read and
/// the publish ignore the caller's cancellation: the write is done, its change must go out.
/// </summary>
public static class StoredDevicePublisher
{
    /// <summary>Gates per feed; a device always takes the same one.</summary>
    internal const int Stripes = 64;

    private const int Chunk = 500;

    private static readonly ConditionalWeakTable<IDeviceChangeFeed, SemaphoreSlim[]> Gates = [];

    /// <summary>Reads the devices of <paramref name="ids"/> as stored now and publishes them as Updated (Added for <paramref name="kind"/> Added); devices no longer stored are skipped.</summary>
    /// <returns>The devices published, as stored.</returns>
    public static async Task<IReadOnlyList<Device>> PublishStoredAsync(IDbContextFactory<OadmDbContext> dbFactory, IDeviceChangeFeed feed,
        IReadOnlyCollection<Guid> ids, DeviceChangeKind kind = DeviceChangeKind.Updated)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        return await PublishStoredAsync(db, feed, ids, kind).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="PublishStoredAsync(IDbContextFactory{OadmDbContext}, IDeviceChangeFeed, IReadOnlyCollection{Guid}, DeviceChangeKind)"/>
    /// with the writer's own context after its commit (no second context and connection for one device of a polling round).
    /// </summary>
    internal static async Task<IReadOnlyList<Device>> PublishStoredAsync(OadmDbContext db, IDeviceChangeFeed feed,
        IReadOnlyCollection<Guid> ids, DeviceChangeKind kind = DeviceChangeKind.Updated)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var held = await EnterAsync(feed, ids).ConfigureAwait(false);
        try
        {
            var stored = new List<Device>(ids.Count);
            if (ids.Count == 1)
            {
                var id = ids.First();
                if (await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, CancellationToken.None).ConfigureAwait(false) is { } one)
                {
                    stored.Add(one);
                }
            }
            else
            {
                foreach (var chunk in ids.Distinct().Chunk(Chunk))
                {
                    stored.AddRange(await db.Devices.AsNoTracking().Where(d => chunk.Contains(d.Id)).ToListAsync(CancellationToken.None).ConfigureAwait(false));
                }
            }

            Publish(feed, stored, kind);
            return stored;
        }
        finally
        {
            Exit(held);
        }
    }

    /// <summary>
    /// Holds the publish order of every device for a batch writer: call it before the write transaction begins, read and
    /// write the rows inside the transaction, commit, then <see cref="Lease.Publish"/> the written rows and dispose the
    /// lease. Never call it while a transaction of the device table is open.
    /// </summary>
    public static async Task<Lease> HoldAsync(IDeviceChangeFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var gates = GatesOf(feed);
        var held = new List<SemaphoreSlim>(gates.Length);
        try
        {
            foreach (var gate in gates)
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                held.Add(gate);
            }
        }
        catch
        {
            Exit(held);
            throw;
        }

        return new Lease(feed, held);
    }

    /// <summary>Publishes the removal of <paramref name="ids"/> behind the same gates (a stored copy read before never brings them back).</summary>
    public static async Task PublishRemovedAsync(IDeviceChangeFeed feed, IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        var held = await EnterAsync(feed, ids).ConfigureAwait(false);
        try
        {
            foreach (var id in ids)
            {
                feed.Publish(new DeviceChange(DeviceChangeKind.Removed, id, null));
            }
        }
        finally
        {
            Exit(held);
        }
    }

    private static SemaphoreSlim[] GatesOf(IDeviceChangeFeed feed) =>
        Gates.GetValue(feed, _ => [.. Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(1, 1))]);

    private static int StripeOf(Guid id) => (id.GetHashCode() & int.MaxValue) % Stripes;

    /// <summary>Takes the gates of <paramref name="ids"/> in ascending order (no two callers wait for each other in a circle).</summary>
    private static async Task<List<SemaphoreSlim>> EnterAsync(IDeviceChangeFeed feed, IReadOnlyCollection<Guid> ids)
    {
        var gates = GatesOf(feed);
        var stripes = new SortedSet<int>();
        foreach (var id in ids)
        {
            stripes.Add(StripeOf(id));
            if (stripes.Count == Stripes)
            {
                break;
            }
        }

        var held = new List<SemaphoreSlim>(stripes.Count);
        foreach (var stripe in stripes)
        {
            await gates[stripe].WaitAsync(CancellationToken.None).ConfigureAwait(false);
            held.Add(gates[stripe]);
        }

        return held;
    }

    private static void Exit(List<SemaphoreSlim> held)
    {
        for (var i = held.Count - 1; i >= 0; i--)
        {
            held[i].Release();
        }

        held.Clear();
    }

    private static void Publish(IDeviceChangeFeed feed, List<Device> stored, DeviceChangeKind kind)
    {
        if (stored.Count == 1)
        {
            feed.Publish(new DeviceChange(kind, stored[0].Id, stored[0].Clone()));
        }
        else if (stored.Count > 1)
        {
            feed.PublishMany([.. stored.Select(d => new DeviceChange(kind, d.Id, d.Clone()))]);
        }
    }

    /// <summary>The gates of every device, held by a batch writer from before its transaction until it published (<see cref="HoldAsync"/>).</summary>
    public sealed class Lease : IDisposable
    {
        private readonly IDeviceChangeFeed _feed;
        private readonly List<SemaphoreSlim> _held;

        internal Lease(IDeviceChangeFeed feed, List<SemaphoreSlim> held)
        {
            _feed = feed;
            _held = held;
        }

        /// <summary>
        /// Publishes rows the transaction read and wrote, after its commit, as Updated (Added for <paramref name="kind"/>
        /// Added). Only rows read inside the transaction: those are what the table holds.
        /// </summary>
        public void Publish(IReadOnlyList<Device> written, DeviceChangeKind kind = DeviceChangeKind.Updated)
        {
            ArgumentNullException.ThrowIfNull(written);
            ObjectDisposedException.ThrowIf(_held.Count == 0, this);
            StoredDevicePublisher.Publish(_feed, [.. written], kind);
        }

        /// <summary>Releases the gates (also when the transaction failed: nothing was written, nothing is published).</summary>
        public void Dispose() => Exit(_held);
    }
}
