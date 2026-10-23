using System.Collections.Concurrent;

using Oadm.Core.Auth;
using Oadm.Core.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// Makes <see cref="DeviceView"/>s (docs/spec/device-scope.md, Enforcement): from the authenticated caller of a call
/// (<see cref="ForAsync(AuthenticatedCaller, CancellationToken)"/>), from a stored scope (the owner of a task, a rule or a
/// waiting entry), and the named <see cref="System"/> view for background work. Never "no caller = allow": a missing caller
/// throws. Compiled filters are cached per group revision and catalog key, allowed id sets per (group revision, catalog key,
/// <see cref="DeviceChangeFeed.Version"/>), both lazily.
/// </summary>
public sealed class DeviceViewFactory
{
    private readonly DeviceGroupStore _groups;
    private readonly DeviceScopeCatalog _catalog;
    private readonly IDeviceChangeFeed _feed;
    private readonly UserStore _users;
    private readonly ConcurrentDictionary<Guid, DeviceGroupFilter> _filters = new();
    private readonly ConcurrentDictionary<Guid, IdsEntry> _ids = new();

    public DeviceViewFactory(DeviceGroupStore groups, DeviceScopeCatalog catalog, DeviceRepository devices, IDeviceChangeFeed feed, UserStore users)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(users);
        _groups = groups;
        _catalog = catalog;
        Devices = devices;
        _feed = feed;
        _users = users;
        System = new DeviceView(this, ViewKind.System, null, null);
    }

    /// <summary>
    /// Every device, for the host's own background work (polling, event rules, sweeps); never for a user's call. The
    /// global repository is the same for system work.
    /// </summary>
    public DeviceView System { get; }

    /// <summary>The global device repository behind the views (system work only).</summary>
    internal DeviceRepository Devices { get; }

    /// <summary>The device groups behind the views.</summary>
    internal DeviceGroupStore Groups => _groups;

    /// <summary>The device change feed behind the views' feeds (system work only).</summary>
    internal IDeviceChangeFeed Feed => _feed;

    /// <summary>The tag and site catalog the filters resolve against.</summary>
    public DeviceScopeCatalog Catalog => _catalog;

    /// <summary>The caller's view. Throws <see cref="InvalidOperationException"/> without a caller: a scoped getter never allows everything.</summary>
    public async Task<DeviceView> ForAsync(AuthenticatedCaller? caller, CancellationToken ct)
    {
        if (caller is null)
        {
            throw new InvalidOperationException("A device view needs the caller of the call; background work uses DeviceViewFactory.System.");
        }

        return await ForScopeAsync(caller.HasAllDevices ? DeviceScope.All : caller.Devices.Access, caller.UserName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The view of a stored scope (the owner of a task, a rule, a compliance check, a waiting entry): all devices, a group
    /// (an unknown or deleted group has no devices) or none.
    /// </summary>
    public async Task<DeviceView> ForScopeAsync(DeviceScope scope, string? userName, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (scope.AllDevices)
        {
            return new DeviceView(this, ViewKind.All, null, userName);
        }

        return scope.GroupId is { } groupId && _groups.Loaded!.ContainsKey(groupId)
            ? new DeviceView(this, ViewKind.Group, groupId, userName)
            : new DeviceView(this, ViewKind.None, null, userName);
    }

    /// <summary>
    /// The view of a user as stored now (a queued task leaving the queue, a waiting entry): a deleted or disabled user has no
    /// devices, an administrator all.
    /// </summary>
    public async Task<DeviceView> ForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = (await _users.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(u => u.Id == userId);
        if (user is null || user.Disabled)
        {
            return await ForScopeAsync(DeviceScope.None, user?.UserName, ct).ConfigureAwait(false);
        }

        return await ForScopeAsync(user.HasAllDevices ? DeviceScope.All : user.DeviceAccess, user.UserName, ct).ConfigureAwait(false);
    }

    /// <summary>The caller's devices for the auth interceptor: the stored access with the group's current revision.</summary>
    public async Task<CallerDevices> CallerDevicesAsync(DeviceScope access, CancellationToken ct)
    {
        await _groups.LoadAsync(ct).ConfigureAwait(false);
        return new CallerDevices(access, _groups.RevisionOf(access.EffectiveGroupId));
    }

    /// <summary>
    /// How many devices <paramref name="rules"/> cover now (the group editor's live count and the group list). The serial
    /// rule counts as it would for <paramref name="groupId"/> (null: a new group, only devices added by users with all devices).
    /// </summary>
    public async Task<int> CountAsync(DeviceGroupRules rules, Guid? groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var filter = DeviceGroupFilter.Compile(groupId, 0, rules.Normalize(), _catalog.TagNames, _catalog.IsLiveSite, _catalog.Key);
        var rows = await Devices.ListScopeRowsAsync(ct).ConfigureAwait(false);
        return rows.Count(filter.Contains);
    }

    /// <summary>
    /// The device count of every group of <paramref name="groups"/> and the number of all devices, from one read of the
    /// device table (the Users page card).
    /// </summary>
    public async Task<(IReadOnlyDictionary<Guid, int> Counts, int Total)> CountGroupsAsync(IReadOnlyCollection<DeviceGroup> groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groups);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var rows = await Devices.ListScopeRowsAsync(ct).ConfigureAwait(false);
        var key = _catalog.Key;
        var counts = new Dictionary<Guid, int>(groups.Count);
        foreach (var group in groups)
        {
            var filter = DeviceGroupFilter.Compile(group, _catalog.TagNames, _catalog.IsLiveSite, key);
            counts[group.Id] = rows.Count(filter.Contains);
        }

        return (counts, rows.Count);
    }

    /// <summary>The current filter of a group: compiled again when its revision or the catalog moved; Nothing for an unknown group.</summary>
    internal DeviceGroupFilter FilterOf(Guid groupId)
    {
        if (_groups.Loaded is not { } groups || !groups.TryGetValue(groupId, out var group))
        {
            return DeviceGroupFilter.Nothing; // deleted meanwhile: fail closed
        }

        var key = _catalog.Key;
        if (_filters.TryGetValue(groupId, out var cached) && cached.Revision == group.Revision && cached.Catalog == key)
        {
            return cached;
        }

        var compiled = DeviceGroupFilter.Compile(group, _catalog.TagNames, _catalog.IsLiveSite, key);
        _filters[groupId] = compiled;
        return compiled;
    }

    /// <summary>The allowed ids of a view, cached lazily per (group revision, catalog key, device feed version).</summary>
    internal async Task<IReadOnlySet<Guid>> AllowedIdsAsync(DeviceView view, CancellationToken ct)
    {
        if (view.Kind == ViewKind.None)
        {
            return EmptyIds;
        }

        // The feed version is read before the rows: a result keyed with an older version is read again next time.
        var feedVersion = _feed is DeviceChangeFeed feed ? feed.Version : -1;
        var filter = view.Filter;
        var cacheKey = view.HasAllDevices ? Guid.Empty : view.GroupId!.Value;
        var key = new IdsKey(filter.Revision, filter.Catalog, feedVersion);
        if (feedVersion >= 0 && _ids.TryGetValue(cacheKey, out var entry) && entry.Key == key)
        {
            return await entry.Ids.ConfigureAwait(false);
        }

        var load = LoadIdsAsync(filter, ct);
        if (feedVersion >= 0)
        {
            _ids[cacheKey] = new IdsEntry(key, load);
        }

        try
        {
            return await load.ConfigureAwait(false);
        }
        catch
        {
            _ids.TryRemove(new KeyValuePair<Guid, IdsEntry>(cacheKey, new IdsEntry(key, load)));
            throw;
        }
    }

    private async Task<IReadOnlySet<Guid>> LoadIdsAsync(DeviceGroupFilter filter, CancellationToken ct)
    {
        if (filter.IsEverything)
        {
            return (await Devices.ListDeviceIdsAsync(ct).ConfigureAwait(false)).ToHashSet();
        }

        var rows = await Devices.ListScopeRowsAsync(ct).ConfigureAwait(false);
        return rows.Where(filter.Contains).Select(r => r.Id).ToHashSet();
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken ct)
    {
        await _groups.LoadAsync(ct).ConfigureAwait(false);
        await _catalog.EnsureLoadedAsync(ct).ConfigureAwait(false);
    }

    private static readonly IReadOnlySet<Guid> EmptyIds = new HashSet<Guid>();

    private readonly record struct IdsKey(long Revision, DeviceScopeCatalogKey Catalog, long FeedVersion);

    private sealed record IdsEntry(IdsKey Key, Task<IReadOnlySet<Guid>> Ids);
}

/// <summary>The texts of a group's rules with names ("Site Building B, tag PTZ, 3 devices, except 1").</summary>
public static class DeviceGroupSummary
{
    /// <summary>
    /// The rules in words, sites and tags by name. <paramref name="withExcept"/> false leaves the excepted devices out
    /// (Your role never shows them). A deleted tag or site is left out.
    /// </summary>
    public static string Text(DeviceGroupRules rules, DeviceScopeCatalog catalog, bool withExcept = true)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(catalog);
        var parts = new List<string>();
        if (rules.ThisServer)
        {
            parts.Add(DeviceGroupTexts.ThisServer);
        }

        var sites = SiteNames(rules, catalog);
        if (sites.Count > 0)
        {
            parts.Add((sites.Count == 1 ? "Site " : "Sites ") + string.Join(", ", sites));
        }

        var tags = TagNamesOf(rules, catalog);
        if (tags.Count > 0)
        {
            parts.Add((tags.Count == 1 ? "tag " : "tags ") + string.Join(", ", tags));
        }

        if (rules.Serials.Count > 0)
        {
            parts.Add(DeviceGroupStore.Count(rules.Serials.Count, "device", "devices"));
        }

        if (withExcept && rules.ExceptSerials.Count > 0)
        {
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"except {rules.ExceptSerials.Count:N0}"));
        }

        return parts.Count == 0 ? DeviceGroupTexts.NoDevices : string.Join(", ", parts);
    }

    /// <summary>The names of the group's sites that exist, sorted.</summary>
    public static IReadOnlyList<string> SiteNames(DeviceGroupRules rules, DeviceScopeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. rules.SiteIds.Select(catalog.SiteName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The names of the group's tags that exist, sorted.</summary>
    public static IReadOnlyList<string> TagNamesOf(DeviceGroupRules rules, DeviceScopeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(catalog);
        var names = catalog.TagNames;
        return [.. rules.TagIds.Select(id => names.TryGetValue(id, out var name) ? name : null).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
    }
}
