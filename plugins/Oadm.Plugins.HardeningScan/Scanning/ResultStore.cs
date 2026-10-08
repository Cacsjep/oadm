using System.Text.Json;

namespace Oadm.Plugins.HardeningScan.Scanning;

/// <summary>
/// The last result per device and level (user decision 2026-10-08: kept across restarts in the plugin setting
/// <c>results</c>). Bounded: at most <see cref="HardeningScanPluginInfo.MaxStoredDevices"/> devices per level (the oldest
/// results are dropped first), values and details are cut when they are made. Thread-safe.
/// </summary>
public sealed class ResultStore
{
    public const string SettingKey = "results";

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid DeviceId, ScanLevel Level), DeviceDetail> _results = [];
    private readonly int _maxPerLevel;
    private long _version;
    private long _savedVersion;

    public ResultStore(int maxPerLevel = HardeningScanPluginInfo.MaxStoredDevices)
    {
        _maxPerLevel = Math.Max(1, maxPerLevel);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _results.Count;
            }
        }
    }

    /// <summary>True when results changed since the last <see cref="Serialize"/> marked them saved.</summary>
    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _version != _savedVersion;
            }
        }
    }

    public void Set(DeviceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        lock (_gate)
        {
            _results[(detail.DeviceId, detail.Level)] = detail;
            _version++;
            Trim(detail.Level);
        }
    }

    public DeviceDetail? Get(Guid deviceId, ScanLevel level)
    {
        lock (_gate)
        {
            return _results.TryGetValue((deviceId, level), out var detail) ? detail : null;
        }
    }

    public IReadOnlyList<DeviceDetail> All()
    {
        lock (_gate)
        {
            return [.. _results.Values];
        }
    }

    /// <summary>Drops the results of devices that are no longer managed; returns how many were dropped.</summary>
    public int RemoveExcept(IReadOnlySet<Guid> managed)
    {
        ArgumentNullException.ThrowIfNull(managed);
        lock (_gate)
        {
            var gone = _results.Keys.Where(k => !managed.Contains(k.DeviceId)).ToList();
            foreach (var key in gone)
            {
                _results.Remove(key);
            }

            if (gone.Count > 0)
            {
                _version++;
            }

            return gone.Count;
        }
    }

    /// <summary>The stored JSON; marks the current state as saved.</summary>
    public string Serialize()
    {
        lock (_gate)
        {
            _savedVersion = _version;
            return JsonSerializer.Serialize(new StoredResults { Results = [.. _results.Values.Select(Compact)] }, HardeningJson.Options);
        }
    }

    /// <summary>Loads stored JSON (unreadable JSON is ignored: the page then shows "Not scanned").</summary>
    public void Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        StoredResults? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredResults>(json, HardeningJson.Options);
        }
        catch (JsonException)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var detail in stored?.Results ?? [])
            {
                if (detail.DeviceId != Guid.Empty)
                {
                    _results[(detail.DeviceId, detail.Level)] = detail;
                }
            }

            Trim(ScanLevel.Basic);
            Trim(ScanLevel.Extended);
            _savedVersion = _version;
        }
    }

    /// <summary>The compact form of a stored result (getState, events).</summary>
    public static DeviceResult ToResult(DeviceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var columns = HardeningCatalog.Columns;
        var states = new char[columns.Count];
        var values = new string?[columns.Count];
        Array.Fill(states, '-');
        foreach (var check in detail.Checks)
        {
            if (HardeningCatalog.ColumnIndex.TryGetValue(check.Id, out var index))
            {
                states[index] = CheckStateCodes.ToCode(check.State);
                values[index] = check.Value;
            }
        }

        return new DeviceResult
        {
            DeviceId = detail.DeviceId,
            Level = detail.Level,
            ScannedUtc = detail.ScannedUtc,
            Status = detail.Status,
            States = new string(states),
            Values = values,
        };
    }

    /// <summary>Without the checks that were not scanned (Basic results do not store the Extended columns).</summary>
    private static DeviceDetail Compact(DeviceDetail detail) =>
        detail.Checks.Any(c => c.State == CheckState.NotScanned)
            ? new DeviceDetail { DeviceId = detail.DeviceId, Level = detail.Level, ScannedUtc = detail.ScannedUtc, Status = detail.Status, Checks = [.. detail.Checks.Where(c => c.State != CheckState.NotScanned)] }
            : detail;

    private void Trim(ScanLevel level)
    {
        if (_results.Count <= _maxPerLevel)
        {
            return; // cheap path: the common case never counts
        }

        var count = _results.Keys.Count(k => k.Level == level);
        if (count <= _maxPerLevel)
        {
            return;
        }

        foreach (var oldest in _results.Values.Where(r => r.Level == level).OrderBy(r => r.ScannedUtc).Take(count - _maxPerLevel).ToList())
        {
            _results.Remove((oldest.DeviceId, level));
        }
    }

    private sealed class StoredResults
    {
        public int Version { get; init; } = 1;

        public IReadOnlyList<DeviceDetail> Results { get; init; } = [];
    }
}
