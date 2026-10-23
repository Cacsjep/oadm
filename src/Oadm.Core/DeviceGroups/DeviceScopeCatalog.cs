using System.Collections.Concurrent;

using Oadm.Core.Devices;
using Oadm.Core.Sites;

namespace Oadm.Core.DeviceGroups;

/// <summary>The revision of the tag and site catalog a filter was compiled with.</summary>
/// <param name="TagVersion"><see cref="DeviceTagStore.Version"/>.</param>
/// <param name="SiteRevision">Moves when a site appears or goes (added, removed), not when it goes online or offline.</param>
public readonly record struct DeviceScopeCatalogKey(long TagVersion, long SiteRevision);

/// <summary>
/// What device groups resolve their rules against: the tag definitions (groups keep tags by id, devices carry names) and the
/// sites that exist. <see cref="Key"/> moves with every change of either, so compiled filters and cached id sets follow a
/// tag rename, a deleted tag or a removed site at once (docs/spec/device-scope.md, Compiled predicate).
/// </summary>
public sealed class DeviceScopeCatalog : IDisposable
{
    private readonly DeviceTagStore _tags;
    private readonly ISiteStates _sites;
    private readonly ConcurrentDictionary<Guid, bool> _knownSites = new();
    private long _siteRevision;

    public DeviceScopeCatalog(DeviceTagStore tags, ISiteStates sites)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(sites);
        _tags = tags;
        _sites = sites;
        _sites.Changed += OnSitesChanged;
        _tags.DefinitionsChanged += OnTagsChanged;
    }

    /// <summary>Raised (without a caller) when <see cref="Key"/> moved: tag definitions or the sites changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The current revision.</summary>
    public DeviceScopeCatalogKey Key => new(_tags.Version, Interlocked.Read(ref _siteRevision));

    /// <summary>The tag names by definition id; empty until <see cref="EnsureLoadedAsync"/> ran once.</summary>
    public IReadOnlyDictionary<Guid, string> TagNames => _tags.DefinitionNames ?? EmptyNames;

    /// <summary>True while the site exists (bound; not removed). A removed site's kept devices are in no group by their site.</summary>
    public bool IsLiveSite(Guid siteId) => _sites.NameOf(siteId) is not null;

    /// <summary>The name of a site that exists, else null.</summary>
    public string? SiteName(Guid siteId) => _sites.NameOf(siteId);

    /// <summary>Reads the tag definitions once (later writes keep them current).</summary>
    public async ValueTask EnsureLoadedAsync(CancellationToken ct) => await _tags.DefinitionNamesAsync(ct).ConfigureAwait(false);

    public void Dispose()
    {
        _sites.Changed -= OnSitesChanged;
        _tags.DefinitionsChanged -= OnTagsChanged;
    }

    private void OnTagsChanged(object? sender, EventArgs e) => RaiseChanged();

    private void RaiseChanged() => Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);

    private static readonly IReadOnlyDictionary<Guid, string> EmptyNames = new Dictionary<Guid, string>();

    private void OnSitesChanged(object? sender, SiteStatesChangedEventArgs e)
    {
        var moved = false;
        foreach (var id in e.SiteIds)
        {
            var live = IsLiveSite(id);
            if (!_knownSites.TryGetValue(id, out var before) || before != live)
            {
                _knownSites[id] = live;
                moved = true;
            }
        }

        if (moved)
        {
            Interlocked.Increment(ref _siteRevision);
            RaiseChanged();
        }
    }
}
