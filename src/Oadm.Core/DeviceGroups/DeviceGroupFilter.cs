using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The compiled rules of one device group (docs/spec/device-scope.md, Compiled predicate): hash sets of site ids, tag names
/// (resolved from the tag ids when it was compiled), serials and excepted serials, so a check is a few hash lookups on the
/// device row. Compiled again when the group's revision or the tag and site catalog changes (<see cref="DeviceViewFactory"/>).
/// <see cref="Everything"/> is the filter of users with all devices, <see cref="Nothing"/> the one of a user without devices
/// or with a deleted group (fail closed).
/// </summary>
public sealed class DeviceGroupFilter
{
    private readonly bool _everything;
    private readonly bool _thisServer;
    private readonly HashSet<Guid> _sites;
    private readonly HashSet<string> _tags;
    private readonly HashSet<string> _serials;
    private readonly HashSet<string> _except;

    private DeviceGroupFilter(bool everything, Guid? groupId, long revision, DeviceScopeCatalogKey catalog, bool thisServer,
        HashSet<Guid> sites, HashSet<string> tags, HashSet<string> serials, HashSet<string> except)
    {
        _everything = everything;
        GroupId = groupId;
        Revision = revision;
        Catalog = catalog;
        _thisServer = thisServer;
        _sites = sites;
        _tags = tags;
        _serials = serials;
        _except = except;
    }

    /// <summary>Every device (users with all devices, the system).</summary>
    public static DeviceGroupFilter Everything { get; } = new(true, null, 0, default, false, [], [], [], []);

    /// <summary>No device (fail closed: no access, an unknown or deleted group).</summary>
    public static DeviceGroupFilter Nothing { get; } = new(false, null, 0, default, false, [], [], [], []);

    /// <summary>True for <see cref="Everything"/>.</summary>
    public bool IsEverything => _everything;

    /// <summary>The group it was compiled from; null for <see cref="Everything"/> and <see cref="Nothing"/>.</summary>
    public Guid? GroupId { get; }

    /// <summary>The group's revision it was compiled from.</summary>
    public long Revision { get; }

    /// <summary>The tag and site catalog it was compiled with.</summary>
    public DeviceScopeCatalogKey Catalog { get; }

    /// <summary>
    /// Compiles <paramref name="rules"/>: tag ids become the names of <paramref name="tagNames"/> (a deleted tag drops out),
    /// site ids are kept only while <paramref name="isLiveSite"/> says the site exists (a removed site drops out, its kept
    /// devices are not "This server" either).
    /// </summary>
    public static DeviceGroupFilter Compile(Guid? groupId, long revision, DeviceGroupRules rules, IReadOnlyDictionary<Guid, string> tagNames,
        Func<Guid, bool> isLiveSite, DeviceScopeCatalogKey catalog)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(tagNames);
        ArgumentNullException.ThrowIfNull(isLiveSite);
        var sites = rules.SiteIds.Where(isLiveSite).ToHashSet();
        var tags = new HashSet<string>(TagNames.Comparer);
        foreach (var id in rules.TagIds)
        {
            if (tagNames.TryGetValue(id, out var name))
            {
                tags.Add(name);
            }
        }

        var serials = rules.Serials.ToHashSet(StringComparer.Ordinal);
        var except = rules.ExceptSerials.ToHashSet(StringComparer.Ordinal);
        return new DeviceGroupFilter(false, groupId, revision, catalog, rules.ThisServer, sites, tags, serials, except);
    }

    /// <inheritdoc cref="Compile(Guid?, long, DeviceGroupRules, IReadOnlyDictionary{Guid, string}, Func{Guid, bool}, DeviceScopeCatalogKey)"/>
    public static DeviceGroupFilter Compile(DeviceGroup group, IReadOnlyDictionary<Guid, string> tagNames, Func<Guid, bool> isLiveSite, DeviceScopeCatalogKey catalog)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Compile(group.Id, group.Revision, group.Rules, tagNames, isLiveSite, catalog);
    }

    /// <summary>True when the group's rules name the site (and it exists): the Sites page of a user with the group shows it.</summary>
    public bool CoversSite(Guid siteId) => _everything || _sites.Contains(siteId);

    /// <summary>True when the device is in the group: a site, "This server", a tag or a single serial covers it and its serial is not excepted.</summary>
    public bool Contains(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Contains(device.Serial, device.SiteId, device.Tags, device.AddedByAllDevices, (device as Device)?.AddedByGroupId);
    }

    /// <inheritdoc cref="Contains(IDeviceInfo)"/>
    public bool Contains(DeviceScopeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Contains(row.Serial, row.SiteId, row.Tags, row.AddedByAllDevices, row.AddedByGroupId);
    }

    /// <inheritdoc cref="Contains(IDeviceInfo)"/>
    /// <param name="serial">The normalized serial.</param>
    /// <param name="siteId">The device's site; null = the server's own network.</param>
    /// <param name="tags">The device's tag names.</param>
    /// <param name="addedByAllDevices">A user with all devices added it.</param>
    /// <param name="addedByGroupId">The group of the user who added it.</param>
    public bool Contains(string serial, Guid? siteId, IReadOnlyList<string> tags, bool addedByAllDevices, Guid? addedByGroupId)
    {
        if (_everything)
        {
            return true;
        }

        if (serial is null || _except.Contains(serial))
        {
            return false;
        }

        if (siteId is { } site ? _sites.Contains(site) : _thisServer)
        {
            return true;
        }

        if (_tags.Count > 0 && tags is not null)
        {
            foreach (var tag in tags)
            {
                if (_tags.Contains(tag))
                {
                    return true;
                }
            }
        }

        // A single serial covers a device only when someone allowed to choose it added it (a removed serial stays dormant
        // until a user with all devices or a user of this group adds the device again).
        return _serials.Contains(serial) && (addedByAllDevices || (addedByGroupId is { } by && by == GroupId));
    }
}
