using System.Collections.Concurrent;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// The device groups as core plugins see them (<see cref="ICorePluginContext.DeviceScopes"/>, docs/spec/device-scope.md,
/// Plugins): objects a plugin keeps per group (dashboards, device triggers, saved views, sweeps) are stored with
/// <see cref="ICallContext.DeviceScopeKey"/> and run only on that scope's devices. A key that is neither "all" nor a group
/// that exists covers nothing (fail closed: a deleted group's objects are off). The checks are a few hash lookups on the
/// device row (<see cref="DeviceView.Contains(IDeviceInfo)"/>), no request.
/// <see cref="Changed"/> (raised without a caller) follows the group store at once (new rules, a deleted group) and,
/// coalesced over <see cref="CoalesceWindow"/>, every change that may move devices into or out of a group: the tag and site
/// catalog, and devices added, removed, moved to another site, tagged or untagged. For the device changes it keeps the
/// group-relevant fields of each device (<see cref="DeviceScopeRow"/>); an update that changes none of them (status polling)
/// costs one comparison, one that does is checked against each group's compiled rules before and after.
/// </summary>
public sealed class HostDeviceScopes : IDeviceScopes, IDisposable
{
    /// <summary>How long membership changes are collected before <see cref="Changed"/> is raised once per group.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    private readonly DeviceGroupStore _groups;
    private readonly DeviceViewFactory _views;
    private readonly ConcurrentDictionary<Guid, DeviceScopeRow> _rows = new();
    private readonly ConcurrentDictionary<Guid, byte> _removedWhileLoading = new();
    private readonly Lock _rowsGate = new();
    private readonly Lock _loadGate = new();
    private Task? _load;
    private readonly HashSet<Guid> _dirty = [];
    private readonly Lock _dirtyGate = new();
    private readonly ITimer _flush;
    private bool _flushScheduled;
    private int _loaded;

    public HostDeviceScopes(DeviceGroupStore groups, DeviceViewFactory views, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(views);
        _groups = groups;
        _views = views;
        var time = timeProvider ?? TimeProvider.System;

        // The timer never carries the caller of whoever made this.
        _flush = CallerContext.Detached(() => time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        _groups.Changed += OnGroupChanged;
        _groups.Deleted += OnGroupDeleted;
        _views.Catalog.Changed += OnCatalogChanged;
        _views.Feed.Changed += OnDeviceChanged;
    }

    public event EventHandler<string>? Changed;

    /// <summary>Loads the groups and the group-relevant fields of every device (the host does it before the core plugins start).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _groups.LoadAsync(ct).ConfigureAwait(false);
        await _views.Catalog.EnsureLoadedAsync(ct).ConfigureAwait(false);

        // One read for every caller: two reads that overlap could bring back a device removed between them.
        Task load;
        lock (_loadGate)
        {
            load = _load ??= LoadRowsAsync();
        }

        try
        {
            await load.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (load.IsFaulted || load.IsCanceled)
        {
            lock (_loadGate)
            {
                if (ReferenceEquals(_load, load))
                {
                    _load = null; // the next call reads again
                }
            }

            throw;
        }
    }

    /// <summary>Reads of the device rows so far (tests: one, however many callers load).</summary>
    internal int RowReads => Volatile.Read(ref _rowReads);

    private int _rowReads;

    private async Task LoadRowsAsync()
    {
        Interlocked.Increment(ref _rowReads);
        // Subscribed before the read: a change during the read wins over the row read (TryAdd), a removed device stays out.
        var rows = await _views.Devices.ListScopeRowsAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_rowsGate)
        {
            foreach (var row in rows)
            {
                if (!_removedWhileLoading.ContainsKey(row.Id))
                {
                    _rows.TryAdd(row.Id, row);
                }
            }
        }

        // The same rows tell the device feed what it has not published yet: the first polling round after the start then
        // moves no membership version (no allowed ids read again for every device that changes its status).
        if (_views.Feed is DeviceChangeFeed feed)
        {
            feed.SeedMembership(rows);
        }

        Volatile.Write(ref _loaded, 1);
        _removedWhileLoading.Clear();
    }

    public bool Exists(string scopeKey) => IsAll(scopeKey) || GroupOf(scopeKey) is not null;

    public bool Contains(string scopeKey, IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (IsAll(scopeKey))
        {
            return true;
        }

        return GroupOf(scopeKey) is { } id && _views.FilterOf(id).Contains(device);
    }

    /// <summary>From the group-relevant fields kept per device: an unknown device (removed, not loaded yet) is in no scope.</summary>
    public bool Contains(string scopeKey, Guid deviceId)
    {
        if (!_rows.TryGetValue(deviceId, out var row))
        {
            return false;
        }

        return IsAll(scopeKey) || (GroupOf(scopeKey) is { } id && _views.FilterOf(id).Contains(row));
    }

    public IDeviceRepository Devices(string scopeKey) => new ScopeDevices(this, scopeKey);

    public bool InAnyGroup(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_groups.Loaded is not { } loaded)
        {
            return true; // groups not loaded yet: fail closed
        }

        foreach (var id in loaded.Keys)
        {
            if (_views.FilterOf(id).Contains(device))
            {
                return true;
            }
        }

        return false;
    }

    public string? NameOf(string scopeKey) =>
        IsAll(scopeKey) ? DeviceGroupTexts.AllDevices : GroupOf(scopeKey) is { } id ? _groups.Loaded![id].Name : null;

    public void Dispose()
    {
        _groups.Changed -= OnGroupChanged;
        _groups.Deleted -= OnGroupDeleted;
        _views.Catalog.Changed -= OnCatalogChanged;
        _views.Feed.Changed -= OnDeviceChanged;
        _flush.Dispose();
    }

    private static bool IsAll(string? scopeKey) => string.Equals(scopeKey, ICallContext.AllDevicesScopeKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id of an existing group with this key; null otherwise (unknown, deleted, groups not loaded yet).</summary>
    private Guid? GroupOf(string? scopeKey) =>
        Guid.TryParse(scopeKey, out var id) && _groups.Loaded is { } loaded && loaded.ContainsKey(id) ? id : null;

    private void OnGroupChanged(object? sender, Guid groupId) => CallerContext.Raise(Changed, this, groupId.ToString());

    private void OnGroupDeleted(object? sender, DeviceGroupDeleteResult deleted) => CallerContext.Raise(Changed, this, deleted.Id.ToString());

    /// <summary>A tag definition or a site changed: every group may have other devices now.</summary>
    private void OnCatalogChanged(object? sender, EventArgs e) => MarkDirty(AllGroups());

    private void OnDeviceChanged(object? sender, DeviceChange change)
    {
        // Checked and applied under one lock: a change raised after a newer one of the same device (the feed raises outside
        // its lock) is dropped, so a device that left a group never comes back with its old fields.
        lock (_rowsGate)
        {
            if (_views.Feed.IsLatest(change))
            {
                Apply(change);
            }
        }
    }

    private void Apply(DeviceChange change)
    {
        if (_groups.Loaded is not { Count: > 0 })
        {
            // No groups: nothing to tell, only keep the rows current.
            Remember(change);
            return;
        }

        var before = change.Kind == DeviceChangeKind.Added ? null : _rows.GetValueOrDefault(change.DeviceId);
        var after = Remember(change);
        if (change.Kind == DeviceChangeKind.Updated && before is not null && after is not null && SameScope(before, after))
        {
            return; // status, name, firmware: no group can have gained or lost it
        }

        if (before is null && change.Kind == DeviceChangeKind.Removed)
        {
            MarkDirty(AllGroups()); // a device whose groups are unknown left: every group may have lost it
            return;
        }

        List<Guid>? moved = null;
        foreach (var id in AllGroups())
        {
            var filter = _views.FilterOf(id);
            var was = before is not null && filter.Contains(before);
            var isNow = after is not null && filter.Contains(after);
            if (was != isNow || (before is null && isNow))
            {
                (moved ??= []).Add(id);
            }
        }

        if (moved is not null)
        {
            MarkDirty(moved);
        }
    }

    /// <summary>Keeps the device's row after the change; the row after it (null when removed).</summary>
    private DeviceScopeRow? Remember(DeviceChange change)
    {
        if (change.Kind == DeviceChangeKind.Removed || change.Device is null)
        {
            _rows.TryRemove(change.DeviceId, out _);
            if (Volatile.Read(ref _loaded) == 0)
            {
                _removedWhileLoading[change.DeviceId] = 0;
            }

            return null;
        }

        var row = DeviceScopeRow.Of(change.Device);
        _rows[change.DeviceId] = row;
        return row;
    }

    private static bool SameScope(DeviceScopeRow a, DeviceScopeRow b) =>
        a.SiteId == b.SiteId && a.AddedByAllDevices == b.AddedByAllDevices && a.AddedByGroupId == b.AddedByGroupId
        && string.Equals(a.Serial, b.Serial, StringComparison.Ordinal)
        && (ReferenceEquals(a.Tags, b.Tags) || a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal));

    private IReadOnlyCollection<Guid> AllGroups() => _groups.Loaded is { } loaded ? [.. loaded.Keys] : [];

    private void MarkDirty(IReadOnlyCollection<Guid> groupIds)
    {
        if (groupIds.Count == 0)
        {
            return;
        }

        lock (_dirtyGate)
        {
            _dirty.UnionWith(groupIds);
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        _flush.Change(CoalesceWindow, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        Guid[] keys;
        lock (_dirtyGate)
        {
            keys = [.. _dirty];
            _dirty.Clear();
            _flushScheduled = false;
        }

        foreach (var id in keys)
        {
            CallerContext.Raise(Changed, this, id.ToString());
        }
    }

    /// <summary>The devices of a scope as of each read (a group's current rules; nothing for a deleted group).</summary>
    private sealed class ScopeDevices(HostDeviceScopes scopes, string scopeKey) : IDeviceRepository
    {
        public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
            [.. await (await ViewAsync(ct).ConfigureAwait(false)).ListAsync(ct).ConfigureAwait(false)];

        public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
            await (await ViewAsync(ct).ConfigureAwait(false)).FindAsync(id, ct).ConfigureAwait(false);

        private Task<DeviceView> ViewAsync(CancellationToken ct)
        {
            var scope = IsAll(scopeKey) ? DeviceScope.All
                : Guid.TryParse(scopeKey, out var id) ? DeviceScope.Group(id)
                : DeviceScope.None;
            return scopes._views.ForScopeAsync(scope, null, ct);
        }
    }
}
