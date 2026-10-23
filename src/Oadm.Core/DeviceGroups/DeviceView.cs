using System.Collections;

using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The devices one caller may reach (docs/spec/device-scope.md, Enforcement): built from the authenticated caller by
/// <see cref="DeviceViewFactory"/> and passed down explicitly. Methods get their devices from the view, never by id from
/// the global repository: <see cref="ListAsync"/>, <see cref="GetAsync(IReadOnlyCollection{Guid}, CancellationToken)"/>
/// (a batch with any id outside the view, or unknown, is refused whole with <see cref="DevicesNotAvailableException"/>:
/// NOT_FOUND "N devices are no longer available.", the same for every user), <see cref="Contains"/>.
/// The rules are read when asked (the group's current revision and the current tag and site catalog), so a renamed tag
/// or a changed group applies at once; streams that hold a view end on a change of the group anyway (<c>CallerStreams</c>).
/// </summary>
public sealed class DeviceView
{
    private readonly DeviceViewFactory _factory;

    internal DeviceView(DeviceViewFactory factory, ViewKind kind, Guid? groupId, string? userName)
    {
        _factory = factory;
        Kind = kind;
        GroupId = groupId;
        UserName = userName;
    }

    internal ViewKind Kind { get; }

    /// <summary>The factory that made the view (the task engine reads the owner's current view through it).</summary>
    internal DeviceViewFactory Factory => _factory;

    /// <summary>Every device: a user with all devices, an administrator, or <see cref="DeviceViewFactory.System"/>.</summary>
    public bool HasAllDevices => Kind is ViewKind.All or ViewKind.System;

    /// <summary>The background view of the host itself (no user).</summary>
    public bool IsSystem => Kind == ViewKind.System;

    /// <summary>The group of the view; null for all devices and for a view without devices.</summary>
    public Guid? GroupId { get; }

    /// <summary>The user the view belongs to; null for <see cref="DeviceViewFactory.System"/>.</summary>
    public string? UserName { get; }

    /// <summary>The key of per-group state ("all" or the group id), like <c>ICallContext.DeviceScopeKey</c>.</summary>
    public string ScopeKey => HasAllDevices ? Sdk.Plugins.ICallContext.AllDevicesScopeKey : GroupId?.ToString() ?? "none";

    /// <summary>The compiled rules as of now (<see cref="DeviceGroupFilter.Everything"/> / <see cref="DeviceGroupFilter.Nothing"/> included).</summary>
    public DeviceGroupFilter Filter => Kind switch
    {
        ViewKind.All or ViewKind.System => DeviceGroupFilter.Everything,
        ViewKind.Group => _factory.FilterOf(GroupId!.Value),
        _ => DeviceGroupFilter.Nothing,
    };

    /// <summary>True when <paramref name="device"/> is in the view (a few hash lookups on the row, no query).</summary>
    public bool Contains(IDeviceInfo device) => Filter.Contains(device);

    /// <inheritdoc cref="Contains(IDeviceInfo)"/>
    public bool Contains(DeviceScopeRow row) => Filter.Contains(row);

    /// <summary>Every device of the view, with its computed status (sorted by serial).</summary>
    public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct)
    {
        if (Kind == ViewKind.None)
        {
            return [];
        }

        var all = await _factory.Devices.ListDevicesAsync(ct).ConfigureAwait(false);
        if (HasAllDevices)
        {
            return all;
        }

        var filter = Filter;
        return [.. all.Where(filter.Contains)];
    }

    /// <summary>
    /// The device feed of the view (a stream of the device list): subscribed at once, then read the snapshot with
    /// <see cref="PagesAsync"/>, mark it with <see cref="DeviceViewFeed.Sent"/> and read the changes; devices that leave or
    /// enter the view come as Removed and Added.
    /// </summary>
    public DeviceViewFeed Subscribe() => new(this, _factory.Feed, _factory.Catalog);

    /// <summary>
    /// Every device of the view in pages of at most <paramref name="take"/> (the snapshot of a stream: one reader never holds
    /// every device at once). All devices: by serial, like the repository's pages. A group: the cached allowed ids, the rows
    /// read per page and checked again; a device removed meanwhile is left out.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<Device>> PagesAsync(int take, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        if (Kind == ViewKind.None)
        {
            yield break;
        }

        if (HasAllDevices)
        {
            string? after = null;
            while (true)
            {
                var page = await _factory.Devices.ListDevicesPageAsync(after, take, ct).ConfigureAwait(false);
                if (page.Count > 0)
                {
                    yield return page;
                }

                if (page.Count < take)
                {
                    yield break;
                }

                after = page[^1].Serial;
            }
        }

        var ids = await IdsAsync(ct).ConfigureAwait(false);
        foreach (var chunk in ids.Chunk(take))
        {
            var filter = Filter;
            var page = (await _factory.Devices.GetManyAsync(chunk, ct).ConfigureAwait(false)).Where(filter.Contains).ToList();
            if (page.Count > 0)
            {
                yield return page;
            }
        }
    }

    /// <summary>
    /// The number of devices of the view per site (devices without a site are left out): the Sites page of a user with a
    /// device group shows the group's counts. One read of the scope rows, no device rows.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountsBySiteAsync(CancellationToken ct)
    {
        var counts = new Dictionary<Guid, int>();
        if (Kind == ViewKind.None)
        {
            return counts;
        }

        var filter = Filter;
        foreach (var row in await _factory.Devices.ListScopeRowsAsync(ct).ConfigureAwait(false))
        {
            if (row.SiteId is { } site && filter.Contains(row))
            {
                counts[site] = counts.GetValueOrDefault(site) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Every device of the view as <see cref="ViewedDevices"/>: what "empty means all" means (Refresh, Check now, sweeps,
    /// reports run on the caller's view, never on every device).
    /// </summary>
    public async Task<ViewedDevices> AllAsync(CancellationToken ct) => new(this, await ListAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// The devices of <paramref name="ids"/>, all of them in the view, or <see cref="DevicesNotAvailableException"/> with the
    /// number of ids that are unknown or outside it (the whole batch is refused, nothing runs in part). Duplicate ids count once.
    /// </summary>
    public async Task<ViewedDevices> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return new ViewedDevices(this, []);
        }

        if (Kind == ViewKind.None)
        {
            throw new DevicesNotAvailableException(distinct.Count);
        }

        var found = await _factory.Devices.GetManyAsync(distinct, ct).ConfigureAwait(false);
        var filter = Filter;
        var allowed = HasAllDevices ? found : [.. found.Where(filter.Contains)];
        if (allowed.Count != distinct.Count)
        {
            throw new DevicesNotAvailableException(distinct.Count - allowed.Count);
        }

        return new ViewedDevices(this, allowed);
    }

    /// <summary>
    /// <see cref="GetAsync(IReadOnlyCollection{Guid}, CancellationToken)"/>, or every device of the view for an empty list
    /// ("empty means all" = the caller's view).
    /// </summary>
    public Task<ViewedDevices> GetOrAllAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Count == 0 ? AllAsync(ct) : GetAsync(ids, ct);
    }

    /// <summary>One device of the view, or <see cref="DevicesNotAvailableException"/> (unknown or outside the view).</summary>
    public async Task<Device> GetAsync(Guid id, CancellationToken ct) => (await GetAsync([id], ct).ConfigureAwait(false))[0];

    /// <summary>One device of the view, or null (unknown or outside the view, the same answer).</summary>
    public async Task<Device?> FindAsync(Guid id, CancellationToken ct)
    {
        if (Kind == ViewKind.None)
        {
            return null;
        }

        var device = await _factory.Devices.GetAsync(id, ct).ConfigureAwait(false);
        return device is not null && Contains(device) ? device : null;
    }

    /// <summary>
    /// The ids of every device of the view, cached per key (the group's revision, the tag and site catalog,
    /// <c>DeviceChangeFeed.Version</c>): task history, runnable sets and counts intersect with it.
    /// </summary>
    public Task<IReadOnlySet<Guid>> IdsAsync(CancellationToken ct) => _factory.AllowedIdsAsync(this, ct);

    /// <summary>How many devices the view has.</summary>
    public async Task<int> CountAsync(CancellationToken ct) => (await IdsAsync(ct).ConfigureAwait(false)).Count;
}

internal enum ViewKind
{
    None,
    All,
    Group,
    System,
}

/// <summary>
/// Devices a <see cref="DeviceView"/> handed out: the capability the services behind the gRPC classes take instead of raw
/// device ids (docs/spec/device-scope.md, Tests: Capability type). Only <see cref="DeviceView"/> creates it (an architecture
/// test checks that no other code does), so a service that takes it cannot be called with devices outside the caller's view.
/// </summary>
public sealed class ViewedDevices : IReadOnlyList<Device>
{
    private readonly IReadOnlyList<Device> _devices;

    // Only DeviceView calls this (ArchitectureTests.ViewedDevicesAreMadeOnlyByTheView).
    internal ViewedDevices(DeviceView view, IReadOnlyList<Device> devices)
    {
        View = view;
        _devices = devices;
        Ids = [.. devices.Select(d => d.Id)];
    }

    /// <summary>The view they came from (the caller's).</summary>
    public DeviceView View { get; }

    /// <summary>The ids, in the order of the devices.</summary>
    public IReadOnlyList<Guid> Ids { get; }

    public int Count => _devices.Count;

    public Device this[int index] => _devices[index];

    public IEnumerator<Device> GetEnumerator() => _devices.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
