using Oadm.Core.Auth;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The devices of the owners of shared objects (docs/spec/device-scope.md, Shared objects): event rules and compliance
/// checks store their owner (all devices or a group) and run only on its devices. This gives the engines the owner's
/// compiled rules now, synchronously (the groups are in memory), and tells them when to evaluate group-owned objects again:
/// <see cref="Changed"/> after a group's rules or the tag and site catalog changed, <see cref="GroupDeleted"/> after a group
/// was deleted (its objects are off and have no owner; the engines read them again). Fail closed: an unknown or deleted
/// group and "no owner" have no devices.
/// </summary>
public sealed class OwnerScopes : IDisposable
{
    private readonly DeviceViewFactory _views;
    private readonly DeviceGroupStore _groups;

    public OwnerScopes(DeviceViewFactory views, DeviceGroupStore groups)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(groups);
        _views = views;
        _groups = groups;
        _groups.Changed += OnGroupChanged;
        _groups.Deleted += OnGroupDeleted;
        _views.Catalog.Changed += OnCatalogChanged;
    }

    /// <summary>Raised (without a caller) when the devices of group owners may have changed: a group's rules, a tag, a site.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised (without a caller) after a group was deleted: the objects it owned are off and have no owner now.</summary>
    public event EventHandler<Guid>? GroupDeleted;

    /// <summary>Reads the groups and the catalog once (call before the first <see cref="FilterOf"/>, e.g. at engine start).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _groups.LoadAsync(ct).ConfigureAwait(false);
        await _views.Catalog.EnsureLoadedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The owner's devices now: every device for all devices, the group's rules, nothing without an owner.</summary>
    public DeviceGroupFilter FilterOf(DeviceScope owner) =>
        owner.AllDevices ? DeviceGroupFilter.Everything
        : owner.GroupId is { } group ? _views.FilterOf(group)
        : DeviceGroupFilter.Nothing;

    /// <summary>
    /// The sites of the owner's group (null: every site, for all devices). Site events of a rule owned by a group are only
    /// those of its sites.
    /// </summary>
    public IReadOnlySet<Guid>? SitesOf(DeviceScope owner)
    {
        if (owner.AllDevices)
        {
            return null;
        }

        return owner.GroupId is { } id && _groups.Loaded is { } groups && groups.TryGetValue(id, out var group)
            ? group.SiteIds.ToHashSet()
            : EmptySites;
    }

    /// <summary>The name of a group that exists, else null.</summary>
    public string? GroupName(Guid? groupId) =>
        groupId is { } id && _groups.Loaded is { } groups && groups.TryGetValue(id, out var group) ? group.Name : null;

    /// <summary>True when the group exists.</summary>
    public bool GroupExists(Guid groupId) => _groups.Loaded is { } groups && groups.ContainsKey(groupId);

    /// <summary>
    /// The owner a new object of <paramref name="view"/> gets: all devices for users with all devices, the user's group
    /// otherwise (a user without devices owns nothing: <see cref="DeviceScope.None"/>).
    /// </summary>
    public static DeviceScope OwnerOf(DeviceView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.HasAllDevices ? DeviceScope.All : view.GroupId is { } group ? DeviceScope.Group(group) : DeviceScope.None;
    }

    /// <summary>
    /// Whether the user of <paramref name="view"/> sees and edits an object of <paramref name="owner"/>: users with all
    /// devices every object, users with a group only the objects of their group (others are hidden).
    /// </summary>
    public static bool Sees(DeviceView view, DeviceScope owner)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.HasAllDevices || (!owner.AllDevices && owner.GroupId is { } group && view.GroupId == group);
    }

    public void Dispose()
    {
        _groups.Changed -= OnGroupChanged;
        _groups.Deleted -= OnGroupDeleted;
        _views.Catalog.Changed -= OnCatalogChanged;
    }

    private static readonly IReadOnlySet<Guid> EmptySites = new HashSet<Guid>();

    private void OnGroupChanged(object? sender, Guid groupId) => CallerContext.Raise(Changed, this, EventArgs.Empty);

    private void OnCatalogChanged(object? sender, EventArgs e) => CallerContext.Raise(Changed, this, EventArgs.Empty);

    private void OnGroupDeleted(object? sender, DeviceGroupDeleteResult e) => CallerContext.Raise(GroupDeleted, this, e.Id);
}

/// <summary>Rules and texts shared by the objects with an owner (event rules, compliance checks).</summary>
public static class OwnerRules
{
    /// <summary>A user who is not an administrator tried to give an object another owner.</summary>
    public const string OnlyAdministrators = "Only administrators choose who owns it.";

    /// <summary>
    /// The tags an object owned by a group chooses: a defined tag (definitions are names every user sees) or a name on a
    /// device of the owner's view; anything else is refused like an unknown tag (<see cref="KeyNotFoundException"/>), so a
    /// name only devices outside the view carry is never confirmed.
    /// </summary>
    public static async Task RequireTagsAsync(IReadOnlyCollection<string> tags, DeviceView view, Devices.DeviceTagStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(store);
        if (tags.Count == 0 || view.HasAllDevices)
        {
            return;
        }

        var known = new HashSet<string>((await store.DefinitionNamesAsync(ct).ConfigureAwait(false)).Values, Devices.TagNames.Comparer);
        if (tags.All(known.Contains))
        {
            return;
        }

        foreach (var device in await view.ListAsync(ct).ConfigureAwait(false))
        {
            known.UnionWith(device.Tags);
        }

        if (tags.FirstOrDefault(t => !known.Contains(t)) is { } missing)
        {
            throw new KeyNotFoundException($"There is no tag named \"{missing}\".");
        }
    }
}
