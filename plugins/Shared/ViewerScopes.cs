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
/// The device scopes of the users limited to a group who read a page of server-wide state (PKI, EST, NTP, DHCP), so a change
/// can be published to each of them with the rows of its own devices (<see cref="PluginEventAudience.DeviceScope"/>). Users
/// with all devices get the full state with <see cref="PluginEventAudience.AllDevices"/>. Keys of deleted groups are dropped;
/// at most <see cref="Max"/> keys (more than the groups a server can have). With a key lifetime, a key not seen again
/// (<see cref="Seen"/>) for that long is dropped too: its pages read the state again every <see cref="RenewInterval"/>
/// (NTP, DHCP). <see cref="ViewAsync{T}"/> keeps one view per key built from the scope's devices, built again when the
/// group's devices change (<see cref="IDeviceScopes.Changed"/>) or after <see cref="ViewLifetime"/> (fields that never move
/// a device between groups, such as its address), so a publish tick never reads the device table.
/// </summary>
internal sealed class ViewerScopes : IDisposable
{
    public const int Max = 256;

    /// <summary>How long a view is used before it is built again (device changes that move no device between groups).</summary>
    public static readonly TimeSpan ViewLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How often the pages of a group read the state again while shown (keys with a lifetime).</summary>
    public static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(5);

    /// <summary>The key lifetime of the NTP and DHCP pages: three renewals missed.</summary>
    public static readonly TimeSpan PageKeyLifetime = TimeSpan.FromMinutes(15);

    private readonly IDeviceScopes? _scopes;
    private readonly TimeProvider _time;
    private readonly TimeSpan? _keyLifetime;
    private readonly Dictionary<string, DateTimeOffset> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedView> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _subscribed;
    private bool _disposed;
    private int _builds;

    /// <param name="scopes">The device groups (null on hosts without them).</param>
    /// <param name="time">The clock of the key and view lifetimes.</param>
    /// <param name="keyLifetime">Null: a key stays until its group is deleted.</param>
    public ViewerScopes(IDeviceScopes? scopes, TimeProvider? time = null, TimeSpan? keyLifetime = null)
    {
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
        _keyLifetime = keyLifetime;
    }

    /// <summary>The device groups (null on hosts without them: no group page gets events).</summary>
    public IDeviceScopes? Scopes => _scopes;

    /// <summary>Whether the pages of a group must read the state again to stay remembered (a key lifetime).</summary>
    public bool Renews => _keyLifetime is not null;

    /// <summary>Views built from a scope's devices so far (tests).</summary>
    internal int Builds => Volatile.Read(ref _builds);

    /// <summary>Remembers the scope of a caller limited to a group (again: its lifetime starts over).</summary>
    public void Seen(ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.HasAllDevices || _scopes is null)
        {
            return;
        }

        var key = caller.DeviceScopeKey;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_keys.Count >= Max && !_keys.ContainsKey(key))
            {
                DropLocked(now);
            }

            if (_keys.Count < Max || _keys.ContainsKey(key))
            {
                _keys[key] = now;
            }
        }
    }

    /// <summary>The remembered scopes whose group still exists (seen within the key lifetime).</summary>
    public IReadOnlyList<string> Keys()
    {
        if (_scopes is null)
        {
            return [];
        }

        lock (_gate)
        {
            DropLocked(_time.GetUtcNow());
            return [.. _keys.Keys];
        }
    }

    /// <summary>
    /// The view of <paramref name="scopeKey"/>'s devices: the one built before while the group's devices did not change and it
    /// is younger than <see cref="ViewLifetime"/>, else built again from the scope's devices (one read). For background work.
    /// </summary>
    public async Task<T> ViewAsync<T>(string scopeKey, Func<IReadOnlyList<IDeviceInfo>, T> build, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(scopeKey);
        ArgumentNullException.ThrowIfNull(build);
        if (_scopes is not { } scopes)
        {
            return build([]);
        }

        long generation;
        lock (_gate)
        {
            if (!_subscribed && !_disposed)
            {
                scopes.Changed += OnChanged;
                _subscribed = true;
            }

            if (_views.TryGetValue(scopeKey, out var cached) && cached.View is T view && _time.GetUtcNow() - cached.Built < ViewLifetime)
            {
                return view;
            }

            generation = _generations.GetValueOrDefault(scopeKey);
        }

        var devices = scopes.Exists(scopeKey) ? await scopes.Devices(scopeKey).ListAsync(ct).ConfigureAwait(false) : [];
        var built = build(devices);
        Interlocked.Increment(ref _builds);
        lock (_gate)
        {
            // The group changed while it was read: this view serves once, the next call reads again.
            if (!_disposed && _generations.GetValueOrDefault(scopeKey) == generation)
            {
                _views[scopeKey] = new CachedView(built, _time.GetUtcNow());
            }
        }

        return built;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _views.Clear();
            if (_subscribed && _scopes is not null)
            {
                _scopes.Changed -= OnChanged;
                _subscribed = false;
            }
        }
    }

    private void OnChanged(object? sender, string scopeKey)
    {
        lock (_gate)
        {
            _views.Remove(scopeKey);
            _generations[scopeKey] = _generations.GetValueOrDefault(scopeKey) + 1;
        }
    }

    /// <summary>Drops the keys of deleted groups and those not seen within the key lifetime, with their views (under the gate).</summary>
    private void DropLocked(DateTimeOffset now)
    {
        var scopes = _scopes!;
        var gone = _keys.Where(k => !scopes.Exists(k.Key) || (_keyLifetime is { } lifetime && now - k.Value > lifetime)).Select(k => k.Key).ToList();
        foreach (var key in gone)
        {
            _keys.Remove(key);
            _views.Remove(key);
        }
    }

    private sealed record CachedView(object View, DateTimeOffset Built);
}
