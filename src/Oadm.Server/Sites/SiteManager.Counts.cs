using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;

namespace Oadm.Server.Sites;

/// <summary>
/// The device count per site of the Sites page. The page is pushed after every heartbeat, status and transfer change, so
/// the counts are read from the database only when the device feed says a device joined, left or changed its site
/// (<see cref="WatchDevices"/>); every other push is built from memory.
/// </summary>
public sealed partial class SiteManager
{
    private readonly Lock _countsLock = new();
    private IDeviceChangeFeed? _deviceFeed;
    private Dictionary<Guid, int> _counts = [];
    private Dictionary<Guid, Guid> _deviceSites = []; // the devices with a site at the last read
    private bool _countsStale = true;
    private Task<IReadOnlyDictionary<Guid, int>>? _countsRead; // the read in progress, shared by every page that asks
    private int _countReads;

    /// <summary>Reads of the device counts from the database (tests: none while only states change).</summary>
    internal int DeviceCountReads => Volatile.Read(ref _countReads);

    /// <summary>
    /// Follows the device feed: the counts are read again only after a change of a device's site. Without it (tests that
    /// build the manager alone) every page reads them.
    /// </summary>
    public void WatchDevices(IDeviceChangeFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        _deviceFeed = feed;
        feed.Changed += OnDeviceChanged;
    }

    private void OnDeviceChanged(object? sender, DeviceChange change)
    {
        var site = change.Kind == DeviceChangeKind.Removed ? null : change.Device?.SiteId;
        lock (_countsLock)
        {
            if (_countsStale)
            {
                return;
            }

            Guid? before = _deviceSites.TryGetValue(change.DeviceId, out var known) ? known : null;
            // While a read runs its result may or may not hold this change: any change that touches a site reads again.
            var touches = _countsRead is not null
                ? site is not null || before is not null || change.Kind == DeviceChangeKind.Removed
                : site != before;
            if (!touches)
            {
                return;
            }

            _countsStale = true;
        }

        RaiseList();
    }

    /// <summary>The number of devices per site id (bound and removed sites).</summary>
    private async Task<IReadOnlyDictionary<Guid, int>> DeviceCountsAsync(CancellationToken ct)
    {
        Task<IReadOnlyDictionary<Guid, int>> read;
        lock (_countsLock)
        {
            if (_deviceFeed is not null && !_countsStale && _countsRead is null)
            {
                return _counts;
            }

            if (_countsRead is null)
            {
                _countsStale = false;
                _countsRead = ReadCountsAsync();
            }

            read = _countsRead;
        }

        return await read.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<Guid, int>> ReadCountsAsync()
    {
        await Task.Yield(); // started under the lock: the read runs after it
        Interlocked.Increment(ref _countReads);
        try
        {
            List<(Guid Device, Guid Site)> rows;
            await using (var db = await _db.CreateDbContextAsync(_stopping).ConfigureAwait(false))
            {
                rows = [.. (await db.Devices.AsNoTracking().Where(d => d.SiteId != null).Select(d => new { d.Id, Site = d.SiteId!.Value })
                    .ToListAsync(_stopping).ConfigureAwait(false)).Select(r => (r.Id, r.Site))];
            }

            var sites = new Dictionary<Guid, Guid>(rows.Count);
            var counts = new Dictionary<Guid, int>();
            foreach (var (device, site) in rows)
            {
                sites[device] = site;
                counts[site] = counts.GetValueOrDefault(site) + 1;
            }

            lock (_countsLock)
            {
                _countsRead = null;
                _deviceSites = sites;
                _counts = counts;
            }

            return counts;
        }
        catch
        {
            lock (_countsLock)
            {
                _countsRead = null;
                _countsStale = true; // read again next time
            }

            throw;
        }
    }

    private void StopWatchingDevices()
    {
        if (_deviceFeed is { } feed)
        {
            feed.Changed -= OnDeviceChanged;
        }
    }
}
