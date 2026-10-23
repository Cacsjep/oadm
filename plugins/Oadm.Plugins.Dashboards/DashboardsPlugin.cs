using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Dashboards;

/// <summary>
/// Core plugin "Dashboards": grids of widgets that show live values of OADM's events (mostly device triggers) and live
/// video. The server part keeps the dashboards in the plugin settings (shared by every user, like Live View's views),
/// subscribes to every event the server handles and keeps the latest value per event and device in memory
/// (<see cref="ValueStore"/>), and pushes the values of the widgets of the open tabs to the pages, at most 4 times a
/// second each. It never talks to a device. Device groups (docs/spec/device-scope.md, Shared objects): every dashboard
/// belongs to the device scope of the user who made it (<see cref="Dashboard.Owner"/>); users of a group see and change only
/// their group's dashboards with the group's devices, get only the events of their devices and sites, and only the values
/// their own page watches.
/// </summary>
public sealed class DashboardsPlugin : ICorePlugin, IDisposable
{
    private const string DashboardsKey = "dashboards";

    // OADM's own device status events (Core BuiltInEvents): while one is on, the device's values may be old.
    private const string BuiltInSource = "oadm.device";
    private const string UnreachableType = "oadm.device.unreachable";
    private const string CredentialsType = "oadm.device.credentials-required";
    private const string CertificateType = "oadm.device.certificate-changed";

    /// <summary>OADM's site events ("Site offline: Building B", server scope): the type id ends with the site id.</summary>
    private const string SiteOfflinePrefix = "oadm.site.offline.";

    /// <summary>A device trigger's second event ("&lt;trigger&gt;: connection lost") is its id with this suffix.</summary>
    private const string LostSuffix = ".lost";

    private static readonly TimeSpan CatalogInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ValueStore _values;
    private readonly Dictionary<(string Session, string Watch), Watcher> _watchers = [];
    private readonly HashSet<ValueKey> _watched = [];
    private readonly HashSet<ValueKey> _dirty = [];
    private readonly Dictionary<Guid, Dictionary<string, string>> _deviceProblems = [];
    private readonly Dictionary<Guid, string> _offlineSites = [];
    private Dictionary<Guid, Guid> _deviceSites = [];
    private Dictionary<string, SubscribedEventType>? _types;
    private ICorePluginContext? _ctx;
    private IEventSubscriptions? _subscriptions;
    private IDisposable? _subscription;
    private ITimer? _timer;
    private List<Dashboard>? _dashboards;
    private bool _catalogDirty;
    private DateTimeOffset _catalogSent = DateTimeOffset.MinValue;
    private int _ticking;

    public DashboardsPlugin()
        : this(TimeProvider.System)
    {
    }

    internal DashboardsPlugin(TimeProvider time, int maxKeys = DashboardsPluginInfo.MaxKeys)
    {
        _time = time;
        _values = new ValueStore(maxKeys);
    }

    public string Id => DashboardsPluginInfo.PluginId;

    public string DisplayName => DashboardsPluginInfo.DisplayName;

    public string? IconKey => DashboardsPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Monitoring;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>"show" comes automatically (Viewer too, a monitoring page); changing dashboards is a right of its own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(DashboardsPluginInfo.ManageKey, "Change dashboards", "Create, change and delete the dashboards of the Dashboards page."),
    ];

    public string? RequiredPermission(string method) => method switch
    {
        DashboardsMethods.List or DashboardsMethods.Catalog or DashboardsMethods.Devices or DashboardsMethods.Watch or DashboardsMethods.Unwatch => PermissionKeys.Show,
        DashboardsMethods.Save or DashboardsMethods.Delete => DashboardsPluginInfo.ManageKey,
        _ => null,
    };

    public bool IsAudited(string method) => DashboardsMethods.IsChange(method);

    /// <summary>The values in memory (tests).</summary>
    internal ValueStore Values => _values;

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _dashboards = null;
        _subscriptions = ctx.EventSubscriptions;
        if (_subscriptions is not null)
        {
            _subscriptions.TypesChanged += OnTypesChanged;
            _subscription = _subscriptions.Subscribe(OnEvent); // starts with every event that is on now
        }

        _timer = _time.CreateTimer(_ => _ = TickAsync(), null, DashboardsPluginInfo.PushInterval, DashboardsPluginInfo.PushInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Stop();
        _storeGate.Dispose();
    }

    private void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _subscription?.Dispose();
        _subscription = null;
        if (_subscriptions is not null)
        {
            _subscriptions.TypesChanged -= OnTypesChanged;
            _subscriptions = null;
        }

        _values.Clear(); // memory only: a plugin that is off keeps nothing
        lock (_gate)
        {
            _watchers.Clear();
            _watched.Clear();
            _dirty.Clear();
            _deviceProblems.Clear();
            _offlineSites.Clear();
            _deviceSites = [];
            _types = null;
        }

        _dashboards = null;
    }

    /// <summary>In-process calls without a user (the host itself, tests): every dashboard and event, owned by all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => InvokeAsync(System, method, payloadJson, ct);

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        switch (method)
        {
            case DashboardsMethods.List:
                return Serialize(await ListAsync(caller, ct).ConfigureAwait(false));
            case DashboardsMethods.Save:
                return Serialize(await SaveAsync(caller, Read<SaveDashboardRequest>(payloadJson), ct).ConfigureAwait(false));
            case DashboardsMethods.Delete:
                await DeleteAsync(caller, Read<DeleteDashboardRequest>(payloadJson).Id, ct).ConfigureAwait(false);
                return null;
            case DashboardsMethods.Catalog:
                return Serialize(Catalog(await ReachOfAsync(caller, ct).ConfigureAwait(false)));
            case DashboardsMethods.Devices:
                return Serialize(await DevicesAsync(caller, Read<DevicesRequest>(payloadJson).TypeId ?? string.Empty, ct).ConfigureAwait(false));
            case DashboardsMethods.Watch:
                return Serialize(await WatchAsync(caller, Read<WatchRequest>(payloadJson), ct).ConfigureAwait(false));
            case DashboardsMethods.Unwatch:
                Unwatch(caller, Read<UnwatchRequest>(payloadJson).WatchId);
                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    // ------------------------------------------------------------------ dashboards

    /// <summary>Every dashboard (tests: a user with all devices).</summary>
    internal Task<DashboardList> ListAsync(CancellationToken ct) => ListAsync(System, ct);

    /// <summary>
    /// The caller's dashboards sorted by name: users with all devices every dashboard, users of a group their group's
    /// dashboards with only the widgets of devices the group has now (and of server events).
    /// </summary>
    internal async Task<DashboardList> ListAsync(ICallContext caller, CancellationToken ct)
    {
        List<Dashboard> dashboards;
        await _storeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            dashboards = await LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _storeGate.Release();
        }

        if (caller.HasAllDevices)
        {
            return new DashboardList([.. Sorted(dashboards)]);
        }

        var mine = dashboards.Where(d => OwnerScope.Sees(caller, d.Owner)).ToList();
        if (mine.Count == 0)
        {
            return new DashboardList([]);
        }

        var devices = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
        return new DashboardList([.. Sorted(mine.Select(d => Within(d, devices)))]);
    }

    /// <summary>Creates or changes a dashboard (tests: a user with all devices).</summary>
    internal Task<Dashboard> SaveAsync(SaveDashboardRequest request, CancellationToken ct) => SaveAsync(System, request, ct);

    /// <summary>
    /// Creates or changes a dashboard after checking its name, widgets, version and the limits. A new one belongs to the
    /// caller's devices; a change keeps the owner (another group's dashboard answers like a deleted one). Every device of a
    /// widget must be in the owner's devices (else NOT_FOUND, nothing stored); names are unique among the owner's dashboards.
    /// </summary>
    internal async Task<Dashboard> SaveAsync(ICallContext caller, SaveDashboardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (DashboardRules.NameProblem(name) is { } nameProblem)
        {
            throw new ArgumentException(nameProblem);
        }

        var widgets = request.Widgets ?? [];
        if (DashboardRules.WidgetsProblem(widgets) is { } widgetProblem)
        {
            throw new ArgumentException(widgetProblem);
        }

        await _storeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dashboards = await LoadAsync(ct).ConfigureAwait(false);
            var isNew = string.IsNullOrEmpty(request.Id);
            var index = isNew ? -1 : dashboards.FindIndex(d => d.Id == request.Id && OwnerScope.Sees(caller, d.Owner));
            if (!isNew && index < 0)
            {
                throw new ArgumentException(DashboardRules.Gone);
            }

            var owner = OwnerScope.ForSave(Context, caller, index >= 0 ? dashboards[index].Owner : null, isNew);
            await OwnerScope.RequireDevicesAsync(Context, caller, owner, widgets.Select(w => w.DeviceId).OfType<Guid>(), ct).ConfigureAwait(false);

            if (index >= 0 && request.Version != 0 && request.Version != dashboards[index].Version)
            {
                throw new ArgumentException(DashboardRules.ChangedMeanwhile(dashboards[index].Name));
            }

            if (dashboards.Where((d, i) => i != index && OwnerScope.Of(d.Owner) == owner).Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(DashboardRules.NameTaken(name));
            }

            if (isNew && dashboards.Count >= DashboardsPluginInfo.MaxDashboards)
            {
                throw new ArgumentException(DashboardRules.TooManyDashboards);
            }

            var saved = new Dashboard(isNew ? Guid.NewGuid().ToString("N") : request.Id!, name, index >= 0 ? dashboards[index].Version + 1 : 1, [.. widgets])
            {
                Owner = owner == ICallContext.AllDevicesScopeKey ? null : owner,
            };
            var next = new List<Dashboard>(dashboards);
            if (index >= 0)
            {
                next[index] = saved;
            }
            else
            {
                next.Add(saved);
            }

            await StoreAsync(next, owner).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _storeGate.Release();
        }
    }

    /// <summary>Deletes a dashboard (tests: a user with all devices).</summary>
    internal Task DeleteAsync(string id, CancellationToken ct) => DeleteAsync(System, id, ct);

    /// <summary>Deletes a dashboard of the caller's devices; an unknown id (or another group's dashboard) is nothing to do.</summary>
    internal async Task DeleteAsync(ICallContext caller, string id, CancellationToken ct)
    {
        await _storeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dashboards = await LoadAsync(ct).ConfigureAwait(false);
            if (dashboards.Find(d => d.Id == id && OwnerScope.Sees(caller, d.Owner)) is { } gone)
            {
                dashboards.Remove(gone);
                await StoreAsync([.. dashboards], OwnerScope.Of(gone.Owner)).ConfigureAwait(false);
            }
        }
        finally
        {
            _storeGate.Release();
        }
    }

    private async Task<List<Dashboard>> LoadAsync(CancellationToken ct)
    {
        if (_dashboards is not null)
        {
            return [.. _dashboards];
        }

        var ctx = Context;
        var json = await ctx.Settings.GetAsync(DashboardsKey, ct).ConfigureAwait(false);
        List<Dashboard> dashboards = [];
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                dashboards = JsonSerializer.Deserialize<List<Dashboard>>(json, DashboardsPluginInfo.Json) ?? [];
            }
            catch (JsonException ex)
            {
                ctx.Logger.LogUnreadableDashboards(ex);
                await ctx.Settings.SetAsync(DashboardsKey + "Unreadable", json, ct).ConfigureAwait(false); // kept, never overwritten by the next save
            }
        }

        _dashboards = dashboards;
        return [.. dashboards];
    }

    /// <summary>
    /// Stores the dashboards, then tells the open pages: users with all devices get every dashboard, the users of the
    /// changed dashboard's group their group's dashboards with the group's devices.
    /// </summary>
    private async Task StoreAsync(List<Dashboard> dashboards, string changedOwner)
    {
        var ctx = Context;
        // Once the write started it finishes: a cancelled caller never leaves the cache and the stored list apart.
        await ctx.Settings.SetAsync(DashboardsKey, JsonSerializer.Serialize(dashboards, DashboardsPluginInfo.Json), CancellationToken.None).ConfigureAwait(false);
        _dashboards = dashboards;
        if (ctx.Events is not { } events)
        {
            return;
        }

        events.Publish(DashboardsMethods.DashboardsTopic, Serialize(new DashboardList([.. Sorted(dashboards)])), PluginEventAudience.AllDevices);
        if (changedOwner != ICallContext.AllDevicesScopeKey && ctx.DeviceScopes is { } scopes && scopes.Exists(changedOwner))
        {
            var devices = (await scopes.Devices(changedOwner).ListAsync(CancellationToken.None).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
            var group = dashboards.Where(d => OwnerScope.Of(d.Owner) == changedOwner).Select(d => Within(d, devices));
            events.Publish(DashboardsMethods.DashboardsTopic, Serialize(new DashboardList([.. Sorted(group)])), PluginEventAudience.DeviceScope(changedOwner));
        }
    }

    /// <summary>A dashboard with only the widgets of server events and of <paramref name="devices"/>.</summary>
    private static Dashboard Within(Dashboard dashboard, HashSet<Guid> devices) =>
        dashboard.Widgets.All(w => w.DeviceId is not { } id || devices.Contains(id))
            ? dashboard
            : dashboard with { Widgets = [.. dashboard.Widgets.Where(w => w.DeviceId is not { } id || devices.Contains(id))] };

    private static IEnumerable<Dashboard> Sorted(IEnumerable<Dashboard> dashboards) =>
        dashboards.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);

    // ------------------------------------------------------------------ events and values

    private void OnEvent(PublishedEvent published)
    {
        var newField = published.Data.Keys.Any(k => !_values.HasField(published.TypeId, k));
        var key = _values.Apply(published);
        if (published.DeviceId is null && SiteOf(published.TypeId) is { } site)
        {
            OnSiteEvent(site, published);
        }

        lock (_gate)
        {
            if (_watched.Contains(key))
            {
                _dirty.Add(key);
            }

            if (published.DeviceId is { } device)
            {
                if (ProblemText(published.TypeId) is { } problem)
                {
                    SetProblem(device, published.TypeId, published.Kind == PublishedEventKind.On ? problem : null);
                    MarkDevice(device, _ => true); // every value of the device is old (or fresh again)
                }
                else if (published.TypeId.EndsWith(LostSuffix, StringComparison.Ordinal))
                {
                    var trigger = published.TypeId[..^LostSuffix.Length];
                    MarkDevice(device, k => k.TypeId == trigger);
                }
            }

            if (newField)
            {
                _catalogDirty = true;
            }
        }
    }

    private void OnTypesChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _types = null;
            _catalogDirty = true;
            _dirty.UnionWith(_watched); // a source turned on or off, an event gone: every watched value says so
        }
    }

    private static string? ProblemText(string typeId) => typeId switch
    {
        UnreachableType => DeviceMessages.StatusText(DeviceStatus.Unreachable),
        CredentialsType => DeviceMessages.StatusText(DeviceStatus.CredentialsRequired),
        CertificateType => DeviceMessages.StatusText(DeviceStatus.CertificateChanged),
        _ => null,
    };

    private static Guid? SiteOf(string typeId) =>
        typeId.StartsWith(SiteOfflinePrefix, StringComparison.Ordinal) && Guid.TryParseExact(typeId[SiteOfflinePrefix.Length..], "N", out var site)
            ? site
            : null;

    /// <summary>A site went offline or online: the values of its devices may be old (or are fresh again).</summary>
    private void OnSiteEvent(Guid site, PublishedEvent published)
    {
        bool load;
        lock (_gate)
        {
            if (published.Kind == PublishedEventKind.Off)
            {
                _offlineSites.Remove(site);
            }
            else
            {
                _offlineSites[site] = published.Data.GetValueOrDefault("site") ?? string.Empty;
            }

            load = _offlineSites.Count > 0;
            _dirty.UnionWith(_watched);
        }

        if (load)
        {
            _ = LoadDeviceSitesAsync();
        }
    }

    /// <summary>Which device is at which site (read while a site is offline; the plugin never talks to a device).</summary>
    private async Task LoadDeviceSitesAsync()
    {
        var ctx = _ctx;
        if (ctx is null)
        {
            return;
        }

        try
        {
            var devices = await ctx.Devices.ListAsync(CancellationToken.None).ConfigureAwait(false);
            var map = new Dictionary<Guid, Guid>();
            foreach (var device in devices)
            {
                if (device.SiteId is { } site)
                {
                    map[device.Id] = site;
                }
            }

            lock (_gate)
            {
                _deviceSites = map;
                _dirty.UnionWith(_watched);
            }
        }
#pragma warning disable CA1031 // Only a hint on old values; the next site event reads again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ctx.Logger.LogSitesUnreadable(ex);
        }
    }

    private void SetProblem(Guid device, string typeId, string? text)
    {
        if (text is not null)
        {
            if (!_deviceProblems.TryGetValue(device, out var problems))
            {
                problems = new Dictionary<string, string>(StringComparer.Ordinal);
                _deviceProblems[device] = problems;
            }

            problems[typeId] = text;
        }
        else if (_deviceProblems.TryGetValue(device, out var problems) && problems.Remove(typeId) && problems.Count == 0)
        {
            _deviceProblems.Remove(device);
        }
    }

    private void MarkDevice(Guid device, Func<ValueKey, bool> match)
    {
        foreach (var key in _watched)
        {
            if (key.DeviceId == device && match(key))
            {
                _dirty.Add(key);
            }
        }
    }

    /// <summary>The event types by id, read again after a change.</summary>
    private Dictionary<string, SubscribedEventType> Types()
    {
        lock (_gate)
        {
            if (_types is not null)
            {
                return _types;
            }
        }

        var types = (_subscriptions?.Types ?? []).GroupBy(t => t.Info.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        lock (_gate)
        {
            _types ??= types;
            return _types;
        }
    }

    /// <summary>The value of a widget now, with the reason it may be old.</summary>
    internal ValueSnapshot Snapshot(ValueKey key)
    {
        var types = Types();
        var value = _values.Get(key);
        string? stale;
        lock (_gate)
        {
            stale = StaleReason(key, types);
        }

        return value is { } v
            ? new ValueSnapshot(key.TypeId, key.DeviceId, v.On, v.Data, v.Time, stale)
            : new ValueSnapshot(key.TypeId, key.DeviceId, false, new Dictionary<string, string>(), null, stale);
    }

    private string? StaleReason(ValueKey key, Dictionary<string, SubscribedEventType> types)
    {
        if (!types.TryGetValue(key.TypeId, out var type))
        {
            return DashboardTexts.EventGone;
        }

        if (!type.Available)
        {
            return DashboardTexts.SourceOff(type.SourceName);
        }

        if (key.DeviceId is not { } device || type.SourceId == BuiltInSource)
        {
            return null; // OADM's own status events are never old: they are the status
        }

        if (_deviceSites.TryGetValue(device, out var site) && _offlineSites.TryGetValue(site, out var siteName))
        {
            return Oadm.Sdk.Sites.SiteTexts.SiteOffline(siteName);
        }

        if (!key.TypeId.EndsWith(LostSuffix, StringComparison.Ordinal) && _values.IsOn(new ValueKey(key.TypeId + LostSuffix, device)))
        {
            return DashboardTexts.ConnectionLost;
        }

        return _deviceProblems.TryGetValue(device, out var problems) && problems.Count > 0 ? problems.Values.First() : null;
    }

    // ------------------------------------------------------------------ catalog

    /// <summary>Every event (tests: a user with all devices).</summary>
    internal CatalogReply Catalog() => Catalog(null);

    /// <summary>
    /// The events with the fields seen so far: Device triggers first, then OADM's own events, then the others. A user of a
    /// device group (<paramref name="reach"/>) gets the device events that can come from the group's devices and the site
    /// events of the group's sites, no other server events.
    /// </summary>
    internal CatalogReply Catalog(Reach? reach)
    {
        var events = new List<CatalogEvent>();
        foreach (var type in Types().Values)
        {
            var info = type.Info;
            if (reach is not null && !reach.Sees(info))
            {
                continue;
            }

            var declared = info.DataFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
            var fields = new List<CatalogField>();
            if (info.Kind == EventKind.Stateful)
            {
                fields.Add(new CatalogField(DashboardsPluginInfo.ActiveField, DashboardsPluginInfo.ActiveLabel, EventValueKind.YesNo));
            }

            // The declared values at once, typed by the declaration; values received refine the type of a text value. Values
            // an event sends that it does not declare follow once seen.
            var seen = _values.FieldsOf(info.Id).ToDictionary(f => f.Name, f => f.Recent, StringComparer.Ordinal);
            foreach (var field in info.DataFields)
            {
                var label = string.IsNullOrWhiteSpace(field.DisplayName) ? field.Name : field.DisplayName;
                fields.Add(new CatalogField(field.Name, label, FieldValues.Suggest(field.Kind, seen.GetValueOrDefault(field.Name) ?? [])));
            }

            foreach (var (name, recent) in seen.Where(f => !declared.ContainsKey(f.Key) && f.Key != DashboardsPluginInfo.ActiveField))
            {
                fields.Add(new CatalogField(name, name, FieldValues.Suggest(EventValueKind.Text, recent)));
            }

            events.Add(new CatalogEvent(info.Id, info.DisplayName, info.Group, type.SourceId, type.SourceName, info.Kind == EventKind.Stateful,
                info.Scope == EventScope.Device, type.Available, fields) { HasDeviceList = info.DeviceIds is not null });
        }

        return new CatalogReply([.. events
            .OrderBy(e => e.SourceId == DashboardsPluginInfo.DeviceTriggersId ? 0 : e.SourceId == BuiltInSource ? 1 : 2)
            .ThenBy(e => e.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)]);
    }

    /// <summary>The devices that can send an event; for a group user only those of the group (none for an event the user does not see).</summary>
    private async Task<DevicesReply> DevicesAsync(ICallContext caller, string typeId, CancellationToken ct)
    {
        var type = Types().GetValueOrDefault(typeId);
        if (caller.HasAllDevices || type is null)
        {
            return new DevicesReply(type?.Info.DeviceIds);
        }

        var reach = await ReachOfAsync(caller, ct).ConfigureAwait(false);
        if (!reach!.Sees(type.Info))
        {
            return new DevicesReply([]);
        }

        return new DevicesReply(type.Info.DeviceIds is { } ids ? [.. ids.Where(reach.Devices.Contains)] : null);
    }

    /// <summary>What a group user may see (null: every device, every event).</summary>
    private static async Task<Reach?> ReachOfAsync(ICallContext caller, CancellationToken ct) =>
        caller.HasAllDevices ? null : Reach.Of(await caller.Devices.ListAsync(ct).ConfigureAwait(false));

    /// <summary>The devices and sites of a device scope (the background push of a group's catalog).</summary>
    private async Task<Reach?> ReachOfAsync(string scopeKey, CancellationToken ct) =>
        scopeKey == ICallContext.AllDevicesScopeKey ? null
        : Reach.Of(_ctx?.DeviceScopes is { } scopes && scopes.Exists(scopeKey) ? await scopes.Devices(scopeKey).ListAsync(ct).ConfigureAwait(false) : []);

    // ------------------------------------------------------------------ watching

    /// <summary>A page's open tabs (tests: a user with all devices).</summary>
    internal ValuesReply Watch(WatchRequest request) => Register(System, request, KeysOf(request));

    /// <summary>
    /// A page's open tabs: these values now, then every change pushed (the page renews it every 10 s). The watch belongs to
    /// the caller's session (another session's watch id is another watch). A group user's keys must name devices of the
    /// group (else NOT_FOUND, like an unknown device) and server events the user sees; the values reach only that session.
    /// </summary>
    internal async Task<ValuesReply> WatchAsync(ICallContext caller, WatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var keys = KeysOf(request);
        if (!caller.HasAllDevices && keys.Count > 0)
        {
            var reach = (await ReachOfAsync(caller, ct).ConfigureAwait(false))!;
            var missing = keys.Where(k => k.DeviceId is { } id && !reach.Devices.Contains(id)).Select(k => k.DeviceId).Distinct().Count();
            if (missing > 0)
            {
                throw new DevicesNotAvailableException(missing);
            }

            var types = Types();
            keys.RemoveWhere(k => k.DeviceId is null && !(types.TryGetValue(k.TypeId, out var type) && reach.Sees(type.Info)));
        }

        return Register(caller, request, keys);
    }

    /// <summary>The keys of a watch request, checked (id and count).</summary>
    private static HashSet<ValueKey> KeysOf(WatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WatchId) || request.WatchId.Length > 64)
        {
            throw new ArgumentException("The watch has no id.");
        }

        var keys = (request.Keys ?? []).Where(k => k is not null && !string.IsNullOrWhiteSpace(k.TypeId)).ToHashSet();
        return keys.Count > DashboardsPluginInfo.MaxWatchedKeys ? throw new ArgumentException(DashboardTexts.TooManyOpen) : keys;
    }

    /// <summary>Keeps the watch of the caller's session and answers the values now.</summary>
    private ValuesReply Register(ICallContext caller, WatchRequest request, HashSet<ValueKey> keys)
    {
        lock (_gate)
        {
            var id = (caller.SessionId, request.WatchId);
            if (!_watchers.ContainsKey(id) && _watchers.Count >= 1_000)
            {
                throw new InvalidOperationException("Too many pages watch the dashboards.");
            }

            _watchers[id] = new Watcher(keys, _time.GetUtcNow(), caller.SessionId, caller.HasAllDevices ? ICallContext.AllDevicesScopeKey : caller.DeviceScopeKey, caller.HasAllDevices);
            RebuildWatched();
        }

        return new ValuesReply([.. keys.Select(Snapshot)]);
    }

    /// <summary>Ends a page's watch (tests: a user with all devices).</summary>
    internal void Unwatch(string watchId) => Unwatch(System, watchId);

    /// <summary>Ends a watch of the caller's session; another session's watch id is nothing to do.</summary>
    internal void Unwatch(ICallContext caller, string watchId)
    {
        lock (_gate)
        {
            if (_watchers.Remove((caller.SessionId, watchId ?? string.Empty)))
            {
                RebuildWatched();
            }
        }
    }

    /// <summary>Keys any page watches (tests).</summary>
    internal int WatchedCount
    {
        get
        {
            lock (_gate)
            {
                return _watched.Count;
            }
        }
    }

    private void RebuildWatched()
    {
        _watched.Clear();
        foreach (var watcher in _watchers.Values)
        {
            _watched.UnionWith(watcher.Keys);
        }

        _dirty.IntersectWith(_watched);
    }

    /// <summary>
    /// Every 250 ms: forget pages that went away, push the changed values, and the catalog at most once a second. Users with
    /// all devices get every changed value and the whole catalog, as before device groups; each session of a group user only
    /// the changed values its pages watch, and each group its own catalog.
    /// </summary>
    internal async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var now = _time.GetUtcNow();
            List<ValueKey> changed;
            bool catalog;
            List<Watcher> groupWatchers;
            lock (_gate)
            {
                var expired = _watchers.Where(w => now - w.Value.LastSeen > DashboardsPluginInfo.WatchLifetime).Select(w => w.Key).ToList();
                foreach (var id in expired)
                {
                    _watchers.Remove(id);
                }

                if (expired.Count > 0)
                {
                    RebuildWatched();
                }

                changed = [.. _dirty];
                _dirty.Clear();
                catalog = _catalogDirty && now - _catalogSent >= CatalogInterval;
                if (catalog)
                {
                    _catalogDirty = false;
                    _catalogSent = now;
                }

                groupWatchers = [.. _watchers.Values.Where(w => !w.HasAllDevices)];
            }

            var events = _ctx?.Events;
            if (events is null)
            {
                return;
            }

            if (changed.Count > 0)
            {
                var snapshots = changed.ToDictionary(k => k, Snapshot);
                events.Publish(DashboardsMethods.ValuesTopic, Serialize(new ValuesReply([.. snapshots.Values])), PluginEventAudience.AllDevices);
                foreach (var session in groupWatchers.GroupBy(w => w.SessionId, StringComparer.Ordinal))
                {
                    var mine = session.SelectMany(w => w.Keys).Where(snapshots.ContainsKey).Distinct().Select(k => snapshots[k]).ToList();
                    if (mine.Count > 0)
                    {
                        events.Publish(DashboardsMethods.ValuesTopic, Serialize(new ValuesReply(mine)), PluginEventAudience.Session(session.Key));
                    }
                }
            }

            if (catalog)
            {
                events.Publish(DashboardsMethods.CatalogTopic, Serialize(Catalog()), PluginEventAudience.AllDevices);
                foreach (var scope in groupWatchers.Select(w => w.ScopeKey).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var reach = await ReachOfAsync(scope, CancellationToken.None).ConfigureAwait(false);
                    events.Publish(DashboardsMethods.CatalogTopic, Serialize(Catalog(reach)), PluginEventAudience.DeviceScope(scope));
                }
            }
        }
#pragma warning disable CA1031 // A failed push must never stop the timer; the next tick tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _ctx?.Logger.LogPushFailed(ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private ICorePluginContext Context => _ctx ?? throw new InvalidOperationException("The Dashboards plugin is not running.");

    /// <summary>The call of in-process work without a user (all devices).</summary>
    private ContextCall System => new(Context);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, DashboardsPluginInfo.Json);

    private static T Read<T>(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json ?? "null", DashboardsPluginInfo.Json) ?? throw new ArgumentException("The request is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request could not be read: " + ex.Message, ex);
        }
    }

    private sealed record Watcher(HashSet<ValueKey> Keys, DateTimeOffset LastSeen, string SessionId, string ScopeKey, bool HasAllDevices);

    /// <summary>
    /// What a user of a device group sees: the group's devices, the device events that can come from them (an event with a
    /// device list needs one of them) and the site events of the group's sites; no other server events.
    /// </summary>
    internal sealed class Reach
    {
        private Reach(HashSet<Guid> devices, HashSet<Guid> sites)
        {
            Devices = devices;
            Sites = sites;
        }

        public HashSet<Guid> Devices { get; }

        public HashSet<Guid> Sites { get; }

        public static Reach Of(IEnumerable<IDeviceInfo> devices)
        {
            var list = devices.ToList();
            return new Reach([.. list.Select(d => d.Id)], [.. list.Select(d => d.SiteId).OfType<Guid>()]);
        }

        public bool Sees(EventTypeInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (info.Scope == EventScope.Device)
            {
                return info.DeviceIds is not { } ids || ids.Any(Devices.Contains);
            }

            return SiteOf(info.Id) is { } site && Sites.Contains(site);
        }
    }
}

/// <summary>Texts of the server part and the page (one phrase per concept).</summary>
public static class DashboardTexts
{
    public const string ConnectionLost = "Connection lost";

    public const string EventGone = "Event no longer exists";

    public const string NoValue = "No value yet";

    /// <summary>A page with more widgets in its open tabs than the server follows.</summary>
    public const string TooManyOpen = "Too many dashboards are open. Close some tabs.";

    public const string NotANumber = "Not a number";

    public const string TriggersOff = "Dashboards show the values of device triggers. Turn on Device triggers on the Plugins page.";

    public const string NoTriggers = "Create a device trigger first.";

    public const string NoDashboards = "Click New dashboard, then drag an event onto the grid.";

    public const string NoDashboardsReadOnly = "No dashboards yet.";

    public const string NoTabs = "Click a dashboard on the left to open it.";

    public const string EmptyDashboard = "Drag an event from the left onto the grid, or click it. Add cameras adds live video.";

    public const string NoEvents = "No events with values yet.";

    public const string NotAllowed = "Your role does not allow live view.";

    public const string NotAvailable = "This OADM client cannot show live video. Update the client.";

    public const string ChooseWidget = "Click a widget to change it. Drag over empty cells or Ctrl+click to select several.";

    public const string ChannelHint = "Choose the channel with the buttons on the picture.";

    /// <summary>The device picker of Copy for other devices: the widget's own device and devices without video.</summary>
    public const string OwnDevice = "Shown by this widget";

    public const string NoVideo = "No video";

    /// <summary>The device picker of Add cameras: a camera the dashboard shows already.</summary>
    public const string OnDashboard = "On this dashboard";

    public static string SourceOff(string source) => source + " is turned off";

    public static string LoadFailed(string reason) => "The dashboards could not be read: " + reason;

    public static string DeleteFailed(string reason) => "The dashboard could not be deleted: " + reason;
}

internal static partial class DashboardsLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "The saved dashboards could not be read; the list starts empty")]
    public static partial void LogUnreadableDashboards(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The devices of the sites could not be read")]
    public static partial void LogSitesUnreadable(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard values could not be sent to the pages")]
    public static partial void LogPushFailed(this ILogger logger, Exception ex);
}
