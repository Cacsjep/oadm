using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Dashboards;

/// <summary>
/// Core plugin "Dashboards": grids of widgets that show live values of OADM's events (mostly device triggers) and live
/// video. The server part keeps the dashboards in the plugin settings (shared by every user, like Live View's views),
/// subscribes to every event the server handles and keeps the latest value per event and device in memory
/// (<see cref="ValueStore"/>), and pushes the values of the widgets of the open tabs to the pages, at most 4 times a
/// second each. It never talks to a device.
/// </summary>
public sealed class DashboardsPlugin : ICorePlugin, IDisposable
{
    private const string DashboardsKey = "dashboards";

    // OADM's own device status events (Core BuiltInEvents): while one is on, the device's values may be old.
    private const string BuiltInSource = "oadm.device";
    private const string UnreachableType = "oadm.device.unreachable";
    private const string CredentialsType = "oadm.device.credentials-required";
    private const string CertificateType = "oadm.device.certificate-changed";

    /// <summary>A device trigger's second event ("&lt;trigger&gt;: connection lost") is its id with this suffix.</summary>
    private const string LostSuffix = ".lost";

    private static readonly TimeSpan CatalogInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ValueStore _values;
    private readonly Dictionary<string, Watcher> _watchers = new(StringComparer.Ordinal);
    private readonly HashSet<ValueKey> _watched = [];
    private readonly HashSet<ValueKey> _dirty = [];
    private readonly Dictionary<Guid, Dictionary<string, string>> _deviceProblems = [];
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
        DashboardsMethods.List or DashboardsMethods.Catalog or DashboardsMethods.Watch or DashboardsMethods.Unwatch => PermissionKeys.Show,
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

        _timer = _time.CreateTimer(_ => Tick(), null, DashboardsPluginInfo.PushInterval, DashboardsPluginInfo.PushInterval);
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
            _types = null;
        }

        _dashboards = null;
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        switch (method)
        {
            case DashboardsMethods.List:
                return Serialize(await ListAsync(ct).ConfigureAwait(false));
            case DashboardsMethods.Save:
                return Serialize(await SaveAsync(Read<SaveDashboardRequest>(payloadJson), ct).ConfigureAwait(false));
            case DashboardsMethods.Delete:
                await DeleteAsync(Read<DeleteDashboardRequest>(payloadJson).Id, ct).ConfigureAwait(false);
                return null;
            case DashboardsMethods.Catalog:
                return Serialize(Catalog());
            case DashboardsMethods.Watch:
                return Serialize(Watch(Read<WatchRequest>(payloadJson)));
            case DashboardsMethods.Unwatch:
                Unwatch(Read<UnwatchRequest>(payloadJson).WatchId);
                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    // ------------------------------------------------------------------ dashboards

    /// <summary>Every dashboard, sorted by name.</summary>
    internal async Task<DashboardList> ListAsync(CancellationToken ct)
    {
        await _storeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return new DashboardList([.. Sorted(await LoadAsync(ct).ConfigureAwait(false))]);
        }
        finally
        {
            _storeGate.Release();
        }
    }

    /// <summary>Creates or changes a dashboard after checking its name, widgets, version and the limits.</summary>
    internal async Task<Dashboard> SaveAsync(SaveDashboardRequest request, CancellationToken ct)
    {
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
            var index = isNew ? -1 : dashboards.FindIndex(d => d.Id == request.Id);
            if (!isNew && index < 0)
            {
                throw new ArgumentException(DashboardRules.Gone);
            }

            if (index >= 0 && request.Version != 0 && request.Version != dashboards[index].Version)
            {
                throw new ArgumentException(DashboardRules.ChangedMeanwhile(dashboards[index].Name));
            }

            if (dashboards.Where((d, i) => i != index).Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(DashboardRules.NameTaken(name));
            }

            if (isNew && dashboards.Count >= DashboardsPluginInfo.MaxDashboards)
            {
                throw new ArgumentException(DashboardRules.TooManyDashboards);
            }

            var saved = new Dashboard(isNew ? Guid.NewGuid().ToString("N") : request.Id!, name, index >= 0 ? dashboards[index].Version + 1 : 1, [.. widgets]);
            var next = new List<Dashboard>(dashboards);
            if (index >= 0)
            {
                next[index] = saved;
            }
            else
            {
                next.Add(saved);
            }

            await StoreAsync(next).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _storeGate.Release();
        }
    }

    /// <summary>Deletes a dashboard; an unknown id is nothing to do.</summary>
    internal async Task DeleteAsync(string id, CancellationToken ct)
    {
        await _storeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dashboards = await LoadAsync(ct).ConfigureAwait(false);
            if (dashboards.RemoveAll(d => d.Id == id) > 0)
            {
                await StoreAsync([.. dashboards]).ConfigureAwait(false);
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

    private async Task StoreAsync(List<Dashboard> dashboards)
    {
        var ctx = Context;
        // Once the write started it finishes: a cancelled caller never leaves the cache and the stored list apart.
        await ctx.Settings.SetAsync(DashboardsKey, JsonSerializer.Serialize(dashboards, DashboardsPluginInfo.Json), CancellationToken.None).ConfigureAwait(false);
        _dashboards = dashboards;
        ctx.Events?.Publish(DashboardsMethods.DashboardsTopic, Serialize(new DashboardList([.. Sorted(dashboards)])));
    }

    private static IEnumerable<Dashboard> Sorted(IEnumerable<Dashboard> dashboards) =>
        dashboards.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);

    // ------------------------------------------------------------------ events and values

    private void OnEvent(PublishedEvent published)
    {
        var newField = published.Data.Keys.Any(k => !_values.HasField(published.TypeId, k));
        var key = _values.Apply(published);
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

        if (!key.TypeId.EndsWith(LostSuffix, StringComparison.Ordinal) && _values.IsOn(new ValueKey(key.TypeId + LostSuffix, device)))
        {
            return DashboardTexts.ConnectionLost;
        }

        return _deviceProblems.TryGetValue(device, out var problems) && problems.Count > 0 ? problems.Values.First() : null;
    }

    // ------------------------------------------------------------------ catalog

    /// <summary>The events with the fields seen so far: Device triggers first, then OADM's own events, then the others.</summary>
    internal CatalogReply Catalog()
    {
        var events = new List<CatalogEvent>();
        foreach (var type in Types().Values)
        {
            var info = type.Info;
            var declared = info.DataFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
            var fields = new List<CatalogField>();
            if (info.Kind == EventKind.Stateful)
            {
                fields.Add(new CatalogField(DashboardsPluginInfo.ActiveField, DashboardsPluginInfo.ActiveLabel, EventValueKind.YesNo));
            }

            var seen = _values.FieldsOf(info.Id);
            foreach (var (name, recent) in seen)
            {
                var field = declared.GetValueOrDefault(name);
                var label = string.IsNullOrWhiteSpace(field?.DisplayName) ? name : field.DisplayName;
                fields.Add(new CatalogField(name, label, FieldValues.Suggest(field?.Kind ?? EventValueKind.Text, recent)));
            }

            var unseen = declared.Keys.Any(name => !seen.Any(s => s.Name == name));
            events.Add(new CatalogEvent(info.Id, info.DisplayName, info.Group, type.SourceId, type.SourceName, info.Kind == EventKind.Stateful,
                info.Scope == EventScope.Device, type.Available, fields, unseen));
        }

        return new CatalogReply([.. events
            .OrderBy(e => e.SourceId == DashboardsPluginInfo.DeviceTriggersId ? 0 : e.SourceId == BuiltInSource ? 1 : 2)
            .ThenBy(e => e.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)]);
    }

    // ------------------------------------------------------------------ watching

    /// <summary>A page's open tabs: these values now, then every change pushed (the page renews it every 10 s).</summary>
    internal ValuesReply Watch(WatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WatchId) || request.WatchId.Length > 64)
        {
            throw new ArgumentException("The watch has no id.");
        }

        var keys = (request.Keys ?? []).Where(k => k is not null && !string.IsNullOrWhiteSpace(k.TypeId)).Take(DashboardsPluginInfo.MaxWidgets * 20).ToHashSet();
        lock (_gate)
        {
            if (!_watchers.ContainsKey(request.WatchId) && _watchers.Count >= 1_000)
            {
                throw new InvalidOperationException("Too many pages watch the dashboards.");
            }

            _watchers[request.WatchId] = new Watcher(keys, _time.GetUtcNow());
            RebuildWatched();
        }

        return new ValuesReply([.. keys.Select(Snapshot)]);
    }

    internal void Unwatch(string watchId)
    {
        lock (_gate)
        {
            if (_watchers.Remove(watchId ?? string.Empty))
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

    /// <summary>Every 250 ms: forget pages that went away, push the changed values, and the catalog at most once a second.</summary>
    internal void Tick()
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
            }

            var events = _ctx?.Events;
            if (events is null)
            {
                return;
            }

            if (changed.Count > 0)
            {
                events.Publish(DashboardsMethods.ValuesTopic, Serialize(new ValuesReply([.. changed.Select(Snapshot)])));
            }

            if (catalog)
            {
                events.Publish(DashboardsMethods.CatalogTopic, Serialize(Catalog()));
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

    private sealed record Watcher(HashSet<ValueKey> Keys, DateTimeOffset LastSeen);
}

/// <summary>Texts of the server part and the page (one phrase per concept).</summary>
public static class DashboardTexts
{
    public const string ConnectionLost = "Connection lost";

    public const string EventGone = "Event no longer exists";

    public const string NoValue = "No value yet";

    public const string NotANumber = "Not a number";

    public const string TriggerOnce = "Trigger the event once on the device.";

    public const string TriggersOff = "Dashboards show the values of device triggers. Turn on Device triggers on the Plugins page.";

    public const string NoTriggers = "Create a device trigger first.";

    public const string NoDashboards = "Click New dashboard, then drag an event onto the grid.";

    public const string NoDashboardsReadOnly = "No dashboards yet.";

    public const string NoTabs = "Click a dashboard on the left to open it.";

    public const string EmptyDashboard = "Drag an event or a camera from the left onto the grid, or click it to add it.";

    public const string NoCameras = "No cameras yet. Add devices on the Devices page.";

    public const string NoEvents = "No events with values yet.";

    public const string NotAllowed = "Your role does not allow live view.";

    public const string NotAvailable = "This OADM client cannot show live video. Update the client.";

    public const string ChooseWidget = "Click a widget to change it.";

    public const string ChannelHint = "Choose the channel with the buttons on the picture.";

    /// <summary>The device picker of Copy for other devices: the widget's own device and devices without video.</summary>
    public const string OwnDevice = "Shown by this widget";

    public const string NoVideo = "No video";

    public static string SourceOff(string source) => source + " is turned off";

    public static string LoadFailed(string reason) => "The dashboards could not be read: " + reason;

    public static string DeleteFailed(string reason) => "The dashboard could not be deleted: " + reason;
}

internal static partial class DashboardsLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "The saved dashboards could not be read; the list starts empty")]
    public static partial void LogUnreadableDashboards(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard values could not be sent to the pages")]
    public static partial void LogPushFailed(this ILogger logger, Exception ex);
}
