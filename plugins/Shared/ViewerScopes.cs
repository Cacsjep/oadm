using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Shared;

/// <summary>
/// The devices one user (or one device scope) sees, as sets for filtering the rows of server-wide state (certificates,
/// enrollments, listeners per site) without a request per row. Linked as source into each plugin assembly (internal there).
/// </summary>
internal sealed class ViewedSet
{
    private readonly HashSet<Guid> _ids;
    private readonly HashSet<Guid> _sites;

    private ViewedSet(HashSet<Guid> ids, HashSet<Guid> sites, bool hasLocal)
    {
        _ids = ids;
        _sites = sites;
        HasLocal = hasLocal;
    }

    /// <summary>A device of the set is on the server's own network (no site).</summary>
    public bool HasLocal { get; }

    /// <summary>How many devices the set holds.</summary>
    public int Count => _ids.Count;

    public bool Contains(Guid deviceId) => _ids.Contains(deviceId);

    /// <summary>False for null (a row without a device is never shown to a user limited to a group).</summary>
    public bool Contains(Guid? deviceId) => deviceId is { } id && _ids.Contains(id);

    /// <summary>A device of the set is at <paramref name="siteId"/> (null: on the server's own network).</summary>
    public bool CoversSite(Guid? siteId) => siteId is { } id ? _sites.Contains(id) : HasLocal;

    /// <summary>The set of <paramref name="devices"/> (one pass).</summary>
    public static ViewedSet From(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var ids = new HashSet<Guid>(devices.Count);
        var sites = new HashSet<Guid>();
        var local = false;
        foreach (var device in devices)
        {
            ids.Add(device.Id);
            if (device.SiteId is { } site)
            {
                sites.Add(site);
            }
            else
            {
                local = true;
            }
        }

        return new ViewedSet(ids, sites, local);
    }

    /// <summary>Null for a user with all devices (nothing to filter), else the devices of the user's view.</summary>
    public static async Task<ViewedSet?> OfAsync(ICallContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.HasAllDevices ? null : From(await caller.Devices.ListAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The devices of a device scope (none for a deleted group or without scopes).</summary>
    public static async Task<ViewedSet> OfScopeAsync(IDeviceScopes? scopes, string scopeKey, CancellationToken ct) =>
        scopes is null ? From([]) : From(await scopes.Devices(scopeKey).ListAsync(ct).ConfigureAwait(false));
}

/// <summary>
/// The device scopes of the users limited to a group who read a page of server-wide state (PKI, EST), so a change can be
/// published to each of them with the rows of its own devices (<see cref="PluginEventAudience.DeviceScope"/>). Users with
/// all devices get the full state with <see cref="PluginEventAudience.AllDevices"/>. Keys of deleted groups are dropped;
/// at most <see cref="Max"/> keys (more than the groups a server can have).
/// </summary>
internal sealed class ViewerScopes(IDeviceScopes? scopes)
{
    public const int Max = 256;

    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>The device groups (null on hosts without them: no group page gets events).</summary>
    public IDeviceScopes? Scopes => scopes;

    /// <summary>Remembers the scope of a caller limited to a group.</summary>
    public void Seen(ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.HasAllDevices || scopes is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_keys.Count >= Max)
            {
                _keys.RemoveWhere(k => !scopes.Exists(k));
            }

            if (_keys.Count < Max)
            {
                _keys.Add(caller.DeviceScopeKey);
            }
        }
    }

    /// <summary>The remembered scopes whose group still exists.</summary>
    public IReadOnlyList<string> Keys()
    {
        if (scopes is null)
        {
            return [];
        }

        lock (_gate)
        {
            _keys.RemoveWhere(k => !scopes.Exists(k));
            return [.. _keys];
        }
    }
}
