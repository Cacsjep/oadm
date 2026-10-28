namespace Oadm.Plugins.AppManagement.Client;

/// <summary>The status filter of the page.</summary>
public enum AppStatusFilter
{
    All = 0,
    NotRunning = 1,
    LicenseMissing = 2,
    LicenseExpiresSoon = 3,
    ReadFailed = 4,
}

/// <summary>The page's filters: Hide AXIS apps, the status filter and the search.</summary>
public sealed record AppFilter(bool HideAxisApps, AppStatusFilter Status, string Search);

/// <summary>What the grid shows after the filters, with the counts of the devices.</summary>
public sealed record AppView(IReadOnlyList<AppRow> Rows, int Devices, int Failed);

/// <summary>
/// The rows of the page by device id (whole devices replaced per read) and the filtered grid in one pass over the rows:
/// search, Hide AXIS apps, status filter (30,000 devices with ten applications each: 300,000 rows).
/// </summary>
public sealed class AppIndex
{
    private readonly Dictionary<Guid, Entry> _byId = [];
    private List<Entry>? _sorted;

    /// <summary>Devices with a row.</summary>
    public int Count => _byId.Count;

    /// <summary>Every device's data.</summary>
    public IEnumerable<DeviceApps> Devices => _byId.Values.Select(e => e.Source);

    public bool Contains(Guid deviceId) => _byId.ContainsKey(deviceId);

    public DeviceApps? Find(Guid deviceId) => _byId.TryGetValue(deviceId, out var entry) ? entry.Source : null;

    /// <summary>Replaces every row (a page of the server's rows after a reconnect starts with an empty index).</summary>
    public void Clear()
    {
        _byId.Clear();
        _sorted = null;
    }

    /// <summary>Sets changed devices and drops removed ones; true when anything changed.</summary>
    public bool Apply(IEnumerable<DeviceApps> devices, IEnumerable<Guid>? removed, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var changed = false;
        foreach (var id in removed ?? [])
        {
            if (_byId.Remove(id))
            {
                changed = true;
                _sorted = null;
            }
        }

        foreach (var device in devices)
        {
            var (apps, empty) = AppRow.For(device, today);
            if (_byId.TryGetValue(device.DeviceId, out var entry))
            {
                // Updated in place: the address order stays unless the address changed.
                if (!string.Equals(entry.Empty.AddressSortKey, empty.AddressSortKey, StringComparison.Ordinal))
                {
                    _sorted = null;
                }

                entry.Set(device, apps, empty);
            }
            else
            {
                _byId[device.DeviceId] = new Entry(device, apps, empty);
                _sorted = null;
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>Keeps only the devices of <paramref name="ids"/> (a new choice).</summary>
    public bool KeepOnly(IReadOnlySet<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var gone = _byId.Keys.Where(id => !ids.Contains(id)).ToList();
        foreach (var id in gone)
        {
            _byId.Remove(id);
        }

        if (gone.Count > 0)
        {
            _sorted = null;
        }

        return gone.Count > 0;
    }

    /// <summary>The grid after <paramref name="filter"/>: one pass over the rows, devices in address order.</summary>
    public AppView Build(AppFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var sorted = _sorted ??= [.. _byId.Values.OrderBy(e => e.Empty.AddressSortKey, StringComparer.Ordinal).ThenBy(e => e.Source.DeviceId)];
        var search = filter.Search.Trim();
        var byDevice = new List<AppRow>(sorted.Count * 4);
        var failed = 0;
        foreach (var entry in sorted)
        {
            if (entry.Source.Error is not null)
            {
                failed++;
                if (filter.Status is AppStatusFilter.All or AppStatusFilter.ReadFailed && Matches(entry.Empty, search))
                {
                    byDevice.Add(entry.Empty);
                }

                continue;
            }

            if (filter.Status == AppStatusFilter.ReadFailed)
            {
                continue;
            }

            var shown = 0;
            foreach (var row in entry.Apps)
            {
                if (filter.HideAxisApps && row.IsAxisApp)
                {
                    continue;
                }

                shown++;
                if (!StatusMatches(row, filter.Status) || !Matches(row, search))
                {
                    continue;
                }

                byDevice.Add(row);
            }

            if (shown == 0 && filter.Status == AppStatusFilter.All && Matches(entry.Empty, search))
            {
                byDevice.Add(entry.Empty);
            }
        }

        return new AppView(byDevice, sorted.Count, failed);
    }

    private static bool Matches(AppRow row, string search) =>
        search.Length == 0 || row.Haystack.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool StatusMatches(AppRow row, AppStatusFilter status) => status switch
    {
        AppStatusFilter.NotRunning => !row.IsRunning,
        AppStatusFilter.LicenseMissing => row.IsMissing,
        AppStatusFilter.LicenseExpiresSoon => row.ExpiresSoon,
        _ => true,
    };

    private sealed class Entry(DeviceApps source, AppRow[] apps, AppRow empty)
    {
        public DeviceApps Source { get; private set; } = source;

        public AppRow[] Apps { get; private set; } = apps;

        public AppRow Empty { get; private set; } = empty;

        public void Set(DeviceApps source, AppRow[] apps, AppRow empty)
        {
            Source = source;
            Apps = apps;
            Empty = empty;
        }
    }
}
