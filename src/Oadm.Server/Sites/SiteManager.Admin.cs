using Google.Protobuf.WellKnownTypes;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Sites;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Sites;

/// <summary>
/// Sites removed with Keep devices (CLAUDE.md "# Sites", Binding 6): a tombstone per site (table RemovedSites) names the devices
/// that still carry its id, so they show "&lt;site&gt; (removed)" and the Sites page lists the site as Removed with Replace
/// with and Remove devices until no device is left. A tombstone is dropped once its last device left.
/// </summary>
public sealed partial class SiteManager
{
    // Under _lock; loaded at the start before the sites, written by SiteAdmin.
    private readonly Dictionary<Guid, RemovedSite> _removed = [];

    /// <summary>Reads the tombstones (server start, before anything reads a device).</summary>
    internal async Task LoadRemovedAsync(CancellationToken ct)
    {
        List<RemovedSite> rows;
        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            rows = await db.RemovedSites.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        }

        lock (_lock)
        {
            foreach (var row in rows)
            {
                _removed[row.Id] = row;
            }
        }
    }

    public bool IsRemoved(Guid siteId)
    {
        lock (_lock)
        {
            return _removed.ContainsKey(siteId) && !_sites.ContainsKey(siteId);
        }
    }

    public string? RemovedNameOf(Guid siteId)
    {
        lock (_lock)
        {
            return _removed.TryGetValue(siteId, out var row) ? row.Name : null;
        }
    }

    /// <summary>Keeps the site's name for its kept devices (before the site row goes, so their next publish says "removed").</summary>
    internal async Task MarkRemovedAsync(Guid siteId, string name, CancellationToken ct)
    {
        var row = new RemovedSite { Id = siteId, Name = name, RemovedUtc = Time.GetUtcNow().UtcDateTime };
        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.RemovedSites.Where(r => r.Id == siteId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.RemovedSites.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        lock (_lock)
        {
            _removed[siteId] = row;
        }
    }

    /// <summary>Forgets a removed site (its last device left). Publishes nothing: the devices left already.</summary>
    internal async Task DropRemovedAsync(Guid siteId, CancellationToken ct)
    {
        bool had;
        lock (_lock)
        {
            had = _removed.Remove(siteId);
        }

        if (!had)
        {
            return;
        }

        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.RemovedSites.Where(r => r.Id == siteId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        RaiseList();
    }

    /// <summary>The site's devices publish again (their site's name or state changed: Site column and status).</summary>
    internal void RaiseSiteChanged(Guid siteId) => Raise([siteId]);

    /// <summary>
    /// The Sites page: <see cref="SnapshotAsync"/> plus the removed sites that still have devices (by name, after the bound
    /// sites, before the waiting nodes). Tombstones without devices are dropped on the way.
    /// </summary>
    public async Task<Proto.SiteList> PageAsync(CancellationToken ct)
    {
        var list = await SnapshotAsync(ct).ConfigureAwait(false);
        List<RemovedSite> removed;
        lock (_lock)
        {
            removed = [.. _removed.Values.Where(r => !_sites.ContainsKey(r.Id))];
        }

        if (removed.Count == 0)
        {
            return list;
        }

        var counts = await DeviceCountsAsync(ct).ConfigureAwait(false);

        var rows = new List<Proto.Site>();
        foreach (var site in removed.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (counts.GetValueOrDefault(site.Id) is var count and > 0)
            {
                rows.Add(new Proto.Site
                {
                    Id = site.Id.ToString(),
                    Name = site.Name,
                    State = Proto.SiteState.Removed,
                    DeviceCount = count,
                    Message = SiteTexts.RemovedMessage,
                    Bound = Timestamp.FromDateTime(DateTime.SpecifyKind(site.RemovedUtc, DateTimeKind.Utc)),
                });
            }
            else
            {
                await DropRemovedAsync(site.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // After the bound sites (first the rows with an id), before the waiting nodes.
        var at = list.Sites.TakeWhile(s => s.Id.Length > 0).Count();
        for (var i = 0; i < rows.Count; i++)
        {
            list.Sites.Insert(at + i, rows[i]);
        }

        return list;
    }
}
