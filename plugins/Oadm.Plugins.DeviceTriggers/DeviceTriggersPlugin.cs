using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DeviceTriggers.Runtime;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers;

/// <summary>
/// Core plugin "Device triggers" (off by default; see the plugin README). An
/// administrator picks an event that the chosen devices offer (read live from them), optionally narrowed to source values;
/// OADM keeps a permanent WebSocket event stream to each device (<see cref="TriggerRuntime"/>) and publishes every trigger
/// as two event types for the event rules: the trigger itself and "&lt;Trigger&gt;: connection lost". The types change
/// with the triggers (<see cref="IDynamicEventSource"/>). Triggers live in the plugin setting <see cref="SettingsKey"/>.
/// Device groups (docs/spec/device-scope.md, Shared objects): every trigger belongs to the device scope of the user who made
/// it (<see cref="TriggerDefinition.Owner"/>) and watches only that scope's devices, so its events
/// come only from them; users of a group see and change only their group's triggers; a deleted group's triggers watch
/// nothing until a user with all devices saves them.
/// </summary>
public sealed partial class DeviceTriggersPlugin : ICorePlugin, IDynamicEventSource, IAsyncDisposable
{
    public const string SettingsKey = "triggers";

    private readonly DeviceTriggersOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _resolve = new(1, 1); // one resolve at a time: a newer list is never overwritten by an older one
    private readonly Lock _sync = new();
    private List<TriggerDefinition> _triggers = [];
    private Dictionary<Guid, Resolution> _resolved = [];
    private Dictionary<Guid, int> _notWatched = [];
    private ITimer? _resolveTimer;
    private ICorePluginContext? _ctx;
    private TriggerRuntime? _runtime;
    private EventListReads? _reads;
    private CancellationTokenSource? _running;

    public DeviceTriggersPlugin()
        : this(new DeviceTriggersOptions())
    {
    }

    public DeviceTriggersPlugin(DeviceTriggersOptions options, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler? EventTypesChanged;

    public string Id => DeviceTriggersPluginInfo.PluginId;

    public string DisplayName => DeviceTriggersPluginInfo.DisplayName;

    public string? IconKey => DeviceTriggersPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Automation;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>
    /// Two types per trigger: the event and "&lt;Trigger&gt;: connection lost", each with the devices the trigger covers now
    /// (chosen, tagged, all; <see cref="EventTypeInfo.DeviceIds"/>).
    /// </summary>
    public IReadOnlyList<EventTypeInfo> EventTypes
    {
        get
        {
            lock (_sync)
            {
                return [.. _triggers.SelectMany(t => TypesOf(t, _resolved.TryGetValue(t.Id, out var resolved) ? resolved.Value.DeviceIds : t.DeviceIds))];
            }
        }
    }

    /// <summary>The stored triggers (tests).</summary>
    public IReadOnlyList<TriggerDefinition> Triggers
    {
        get
        {
            lock (_sync)
            {
                return [.. _triggers];
            }
        }
    }

    internal TriggerRuntime? Runtime => _runtime;

    private ILogger Logger => _ctx?.Logger ?? NullLogger.Instance;

    public UserRole RequiredRole(string method) => DeviceTriggersMethods.IsAdminMethod(method) ? UserRole.Admin : UserRole.Operator;

    /// <summary>Saving and turning on count the devices every trigger watches together (one limit for the server).</summary>
    public string? SystemAccessReason(string method) => method is DeviceTriggersMethods.Save or DeviceTriggersMethods.SetEnabled
        ? "counts the devices all triggers watch together (a limit of the server), shows no device data"
        : null;

    /// <summary>Changing triggers is one right ("manage", the Admin methods); seeing them is "show".</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Manage, "Change triggers", "Create, change, turn on or off, copy and delete triggers.") { OperatorDefault = false },
    ];

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        _running = new CancellationTokenSource();
        var stored = await LoadAsync(ctx, ct).ConfigureAwait(false);
        lock (_sync)
        {
            _triggers = stored;
        }

        _reads = new EventListReads(ctx, _options, _time);
        _runtime = new TriggerRuntime(ctx, _options, _time);
        _runtime.Changed += (_, ids) => PublishChanged(ids);
        _runtime.DevicesRemoved += (_, ids) => _ = RemoveDevicesAsync(ids);
        if (ctx.DeviceScopes is { } scopes)
        {
            scopes.Changed += OnScopeChanged;
        }

        EventTypesChanged?.Invoke(this, EventArgs.Empty);
        await ResolveAsync(ct).ConfigureAwait(false);
        _resolveTimer = _time.CreateTimer(_ => _ = ResolveSafelyAsync(), null, _options.ResolveInterval, _options.ResolveInterval);
        var enabled = stored.Count(t => t.Enabled);
        LogStarted(Logger, stored.Count, enabled);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_ctx?.DeviceScopes is { } scopes)
        {
            scopes.Changed -= OnScopeChanged;
        }

        // The timer first (no new rounds), then a resolve in flight finishes (it may still apply to the runtime) and
        // later ones find no runtime; only then the runtime closes its connections.
        if (_resolveTimer is { } timer)
        {
            _resolveTimer = null;
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        var running = _running;
        _running = null;
        if (running is not null)
        {
            await running.CancelAsync().ConfigureAwait(false);
        }

        TriggerRuntime? stopping;
        await _resolve.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            stopping = _runtime;
            _runtime = null;
        }
        finally
        {
            _resolve.Release();
        }

        if (running is not null)
        {
            if (_reads is { } reads)
            {
                await reads.StopAsync().ConfigureAwait(false);
            }

            running.Dispose();
        }

        if (stopping is not null)
        {
            await stopping.DisposeAsync().ConfigureAwait(false);
        }

        _reads = null;
    }

    /// <summary>In-process calls without a user (the host itself, tests): every trigger, owned by all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(NotRunning)), method, payloadJson, ct);

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var ctx = _ctx ?? throw new InvalidOperationException(NotRunning);
        switch (method)
        {
            case DeviceTriggersMethods.List:
                return TriggerJson.Serialize(new ListReply([.. Visible(caller).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(Summary)]));
            case DeviceTriggersMethods.Get:
            {
                var id = TriggerJson.Deserialize<IdRequest>(payloadJson).Id;
                var trigger = Visible(caller).FirstOrDefault(t => t.Id == id);
                var states = trigger is null || _runtime is null ? [] : _runtime.StatesOf(Resolved(trigger));
                if (trigger is not null && !caller.HasAllDevices)
                {
                    // Stored ids of devices that left the group (a tag or site change) are not the caller's to see.
                    var mine = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
                    trigger = trigger with { DeviceIds = [.. trigger.DeviceIds.Where(mine.Contains)] };
                    states = [.. states.Where(s => mine.Contains(s.DeviceId))];
                }

                return TriggerJson.Serialize(new GetReply(trigger, states));
            }

            case DeviceTriggersMethods.StartRead:
            {
                var ids = TriggerJson.Deserialize<StartReadRequest>(payloadJson).DeviceIds ?? [];
                await CallDevices.RequireAsync(caller, ids, ct).ConfigureAwait(false);
                return TriggerJson.Serialize(_reads!.Start(caller, ids, _running?.Token ?? CancellationToken.None));
            }

            case DeviceTriggersMethods.ReadStatus:
                return TriggerJson.Serialize(_reads!.StatusOf(caller, TriggerJson.Deserialize<ReadStatusRequest>(payloadJson).JobId ?? string.Empty)
                    ?? throw new ArgumentException("The read of the event lists ended. Read the events again."));
            case DeviceTriggersMethods.Save:
                return TriggerJson.Serialize(await SaveAsync(ctx, caller, TriggerJson.Deserialize<SaveRequest>(payloadJson).Trigger, ct).ConfigureAwait(false));
            case DeviceTriggersMethods.Delete:
            {
                var id = TriggerJson.Deserialize<IdRequest>(payloadJson).Id;
                var owner = Visible(caller).FirstOrDefault(t => t.Id == id)?.Owner;
                await ChangeAsync(ctx, list => list.RemoveAll(t => t.Id == id && OwnerScope.Sees(caller, t.Owner)) > 0, typesChanged: true, owner, ct).ConfigureAwait(false);
                return null;
            }

            case DeviceTriggersMethods.SetEnabled:
            {
                var request = TriggerJson.Deserialize<SetEnabledRequest>(payloadJson);
                var target = Visible(caller).FirstOrDefault(t => t.Id == request.Id);
                if (request.Enabled && target is { Enabled: false } turningOn
                    && await WatchedCountAsync([.. Triggers.Where(t => t.Enabled), turningOn], ct).ConfigureAwait(false) > _options.MaxWatchedDevices)
                {
                    throw new ArgumentException(DeviceTriggersPluginInfo.TooManyDevicesText);
                }

                await ChangeAsync(ctx, list =>
                {
                    var i = target is null ? -1 : list.FindIndex(t => t.Id == request.Id);
                    if (i < 0 || list[i].Enabled == request.Enabled)
                    {
                        return false;
                    }

                    list[i] = list[i] with { Enabled = request.Enabled };
                    return true;
                }, typesChanged: false, target?.Owner, ct).ConfigureAwait(false);
                return null;
            }

            case DeviceTriggersMethods.Duplicate:
                return TriggerJson.Serialize(await DuplicateAsync(ctx, caller, TriggerJson.Deserialize<IdRequest>(payloadJson).Id, ct).ConfigureAwait(false));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    private const string NotRunning = "The device triggers plugin is not running.";

    /// <summary>The triggers the caller sees: all for users with all devices, the group's own for users of a group.</summary>
    private IEnumerable<TriggerDefinition> Visible(ICallContext caller) => Triggers.Where(t => OwnerScope.Sees(caller, t.Owner));

    /// <summary>A group's rules changed or the group was deleted: its triggers follow its devices at once.</summary>
    private void OnScopeChanged(object? sender, string scopeKey)
    {
        if (Triggers.Any(t => string.Equals(OwnerScope.Of(t.Owner), scopeKey, StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunDetached(ResolveSafelyAsync);
        }
    }

    /// <summary>
    /// Tells the open pages which triggers changed: users with all devices every id, the users of a group the ids of its
    /// triggers only.
    /// </summary>
    private void PublishChanged(IReadOnlyCollection<Guid> ids, string? owner = null)
    {
        if (_ctx?.Events is not { } events)
        {
            return;
        }

        events.Publish(DeviceTriggersMethods.ChangedTopic, TriggerJson.Serialize(new ChangedEvent([.. ids])), PluginEventAudience.AllDevices);
        var owners = Triggers.Where(t => ids.Contains(t.Id)).Select(t => OwnerScope.Of(t.Owner));
        if (owner is not null)
        {
            owners = owners.Append(OwnerScope.Of(owner));
        }

        foreach (var group in owners.Where(o => o != ICallContext.AllDevicesScopeKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mine = Triggers.Where(t => ids.Contains(t.Id) && string.Equals(OwnerScope.Of(t.Owner), group, StringComparison.OrdinalIgnoreCase)).Select(t => t.Id).ToList();
            events.Publish(DeviceTriggersMethods.ChangedTopic, TriggerJson.Serialize(new ChangedEvent(mine)), PluginEventAudience.DeviceScope(group));
        }
    }

    /// <summary>
    /// Background work started from a user's call (the resolve and the connections it opens): it runs without the call, like
    /// every other round of the trigger runtime.
    /// </summary>
    private static Task RunDetached(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _write.Dispose();
        _resolve.Dispose();
    }

    /// <summary>The event types of a trigger. Disabled triggers keep theirs, so their rules stay valid.</summary>
    internal static IEnumerable<EventTypeInfo> TypesOf(TriggerDefinition trigger) => TypesOf(trigger, trigger.DeviceIds);

    private static IEnumerable<EventTypeInfo> TypesOf(TriggerDefinition trigger, IReadOnlyList<Guid> devices)
    {
        var what = trigger.FilterText.Length == 0 ? trigger.EventName : $"{trigger.EventName} · {trigger.FilterText}";
        // The values (placeholders for the actions) one per line below the text, as the info icon of the rule dialog shows it.
        var values = trigger.Values.Count == 0 ? string.Empty : "\n\nValues:\n" + string.Join("\n", trigger.Values.Select(v => "• {" + v + "}"));
        var declared = trigger.ValueFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        yield return new EventTypeInfo(
            DeviceTriggersPluginInfo.TypeId(trigger.Id),
            trigger.Name,
            DeviceTriggersPluginInfo.EventGroup,
            trigger.Stateful ? EventKind.Stateful : EventKind.Stateless,
            Fields: [.. trigger.Values.Select(v => declared.TryGetValue(v, out var f)
                ? new EventField(v, string.IsNullOrWhiteSpace(f.DisplayName) ? v : f.DisplayName) { Kind = f.Kind, Filter = false }
                : new EventField(v, v) { Filter = false })])
        {
            Description = what + "." + values,
            DeviceIds = devices,
            OwnerScopeKey = OwnerScope.Of(trigger.Owner),
        };
        yield return new EventTypeInfo(
            DeviceTriggersPluginInfo.LostTypeId(trigger.Id),
            trigger.Name + DeviceTriggersPluginInfo.LostSuffix,
            DeviceTriggersPluginInfo.EventGroup,
            EventKind.Stateful,
            Fields: [new EventField("reason", "Reason") { Filter = false }])
        {
            Description = $"No events of this trigger from the device for {DeviceTriggersPluginInfo.LostDelay.TotalSeconds:0} s.\n\nValues:\n• {{reason}}",
            DeviceIds = devices,
            OwnerScopeKey = OwnerScope.Of(trigger.Owner),
        };
    }

    private TriggerSummary Summary(TriggerDefinition trigger)
    {
        var resolved = Resolved(trigger);
        var (on, lost, waiting, last) = _runtime?.CountsOf(resolved) ?? (0, 0, 0, null);
        int notWatched;
        lock (_sync)
        {
            notWatched = _notWatched.GetValueOrDefault(trigger.Id);
        }

        return new TriggerSummary(trigger.Id, trigger.Name, trigger.Enabled, trigger.EventName, trigger.FilterText, trigger.Stateful, resolved.DeviceIds.Count, on, lost, waiting, last, notWatched);
    }

    /// <summary>The trigger with every device it covers now (chosen, tagged, all); the stored one until resolved.</summary>
    internal TriggerDefinition Resolved(TriggerDefinition trigger)
    {
        lock (_sync)
        {
            return _resolved.TryGetValue(trigger.Id, out var resolved) ? resolved.Value : trigger;
        }
    }

    /// <summary>
    /// Saves a trigger. A new one belongs to the caller's devices; a change keeps the owner (another group's trigger is
    /// treated as unknown, a new one); a user with all devices who saves a deleted group's trigger gives it to all devices.
    /// The chosen devices of a group's trigger must be in the group (else NOT_FOUND, nothing stored); names are unique among
    /// the owner's triggers.
    /// </summary>
    private async Task<SaveReply> SaveAsync(ICorePluginContext ctx, ICallContext caller, TriggerDefinition? incoming, CancellationToken ct)
    {
        if (incoming is null)
        {
            return new SaveReply(null, "The trigger is empty.", null);
        }

        var stored = incoming.Id == Guid.Empty ? null : Visible(caller).FirstOrDefault(t => t.Id == incoming.Id);
        var owner = OwnerScope.ForSave(ctx, caller, stored?.Owner, stored is null);

        // A user with all devices saving a group's trigger: its devices that left the group are dropped (counted).
        var left = await OwnerScope.LeftOwnerAsync(ctx, caller, owner, incoming.DeviceIds ?? [], stored?.DeviceIds ?? [], ct).ConfigureAwait(false);
        if (left.Count > 0)
        {
            incoming = incoming with { DeviceIds = [.. (incoming.DeviceIds ?? []).Where(id => !left.Contains(id))] };
        }

        await OwnerScope.RequireDevicesAsync(ctx, caller, owner, incoming.DeviceIds ?? [], ct).ConfigureAwait(false);

        var trigger = incoming with
        {
            Name = incoming.Name.Trim(),
            DeviceIds = [.. (incoming.DeviceIds ?? []).Distinct()],
            Tags = [.. (incoming.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
            Filters = [.. (incoming.Filters ?? []).Where(f => !string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(f.Value)).DistinctBy(f => f.Name)],
            Values = [.. (incoming.Values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal)],
            ValueFields = [.. (incoming.ValueFields ?? []).Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Name) && Enum.IsDefined(f.Kind)).DistinctBy(f => f.Name)],
            FilterText = incoming.FilterText ?? string.Empty,
            EventName = incoming.EventName ?? string.Empty,
            Id = stored?.Id ?? (incoming.Id == Guid.Empty || Triggers.Any(t => t.Id == incoming.Id) ? Guid.Empty : incoming.Id),
            Owner = owner == ICallContext.AllDevicesScopeKey ? null : owner,
        };
        var others = Triggers.Where(t => t.Id != trigger.Id).ToList();
        var errors = TriggerValidation.Check(trigger, others.Where(o => OwnerScope.Of(o.Owner) == owner).Select(o => o.Name)).ToDictionary();
        if (!errors.ContainsKey(TriggerValidation.DevicesField))
        {
            var devices = (await OwnerScope.DevicesOf(ctx, caller, owner).ListAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
            var unknown = trigger.DeviceIds.Count(id => !devices.ContainsKey(id));
            var unsupported = trigger.DeviceIds.Where(id => devices.TryGetValue(id, out var d) && d.Apis.Count > 0
                && !d.Apis.Supports(DeviceTriggersPluginInfo.ApiId, DeviceTriggersPluginInfo.ApiVersion)).Select(id => devices[id].Address).ToList();
            if (unknown > 0)
            {
                errors[TriggerValidation.DevicesField] = DeviceMessages.NoLongerAvailable(unknown);
            }
            else if (unsupported.Count > 0)
            {
                errors[TriggerValidation.DevicesField] = $"{string.Join(", ", unsupported.Take(3))}{(unsupported.Count > 3 ? $" and {unsupported.Count - 3} more" : string.Empty)}: {DeviceTriggersPluginInfo.NotSupportedReason}.";
            }
        }

        if (!errors.ContainsKey(TriggerValidation.DevicesField) && trigger.Enabled
            && await WatchedCountAsync([.. others.Where(o => o.Enabled), trigger], ct).ConfigureAwait(false) > _options.MaxWatchedDevices)
        {
            errors[TriggerValidation.DevicesField] = DeviceTriggersPluginInfo.TooManyDevicesText;
        }

        if (errors.Count > 0)
        {
            return new SaveReply(null, null, errors);
        }

        var isNew = trigger.Id == Guid.Empty || others.Count == Triggers.Count;
        if (isNew && Triggers.Count >= DeviceTriggersPluginInfo.MaxTriggers)
        {
            return new SaveReply(null, $"At most {DeviceTriggersPluginInfo.MaxTriggers} triggers.", null);
        }

        if (trigger.Id == Guid.Empty)
        {
            trigger = trigger with { Id = Guid.NewGuid() };
        }

        var saved = trigger;
        await ChangeAsync(ctx, list =>
        {
            var i = list.FindIndex(t => t.Id == saved.Id);
            if (i < 0)
            {
                list.Add(saved);
            }
            else
            {
                list[i] = saved;
            }

            return true;
        }, typesChanged: true, saved.Owner, ct).ConfigureAwait(false);
        return new SaveReply(saved, null, null) { Dropped = left.Count };
    }

    /// <summary>
    /// A copy of a trigger the caller sees, owned by the caller's devices (turned off). A group user's copy keeps only the
    /// devices the user sees now: stored ids of devices that left the group are dropped (counted in <c>Dropped</c>).
    /// </summary>
    private async Task<SaveReply> DuplicateAsync(ICorePluginContext ctx, ICallContext caller, Guid id, CancellationToken ct)
    {
        var source = Visible(caller).FirstOrDefault(t => t.Id == id);
        if (source is null)
        {
            return new SaveReply(null, "The trigger no longer exists.", null);
        }

        if (Triggers.Count >= DeviceTriggersPluginInfo.MaxTriggers)
        {
            return new SaveReply(null, $"At most {DeviceTriggersPluginInfo.MaxTriggers} triggers.", null);
        }

        var owner = OwnerScope.ForSave(ctx, caller, null, isNew: true);
        var dropped = 0;
        if (!caller.HasAllDevices && source.DeviceIds.Count > 0)
        {
            var mine = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
            List<Guid> kept = [.. source.DeviceIds.Where(mine.Contains)];
            dropped = source.DeviceIds.Count - kept.Count;
            source = source with { DeviceIds = kept };
        }

        var names = Triggers.Where(t => OwnerScope.Of(t.Owner) == owner).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = CopyName(source.Name, names);
        var copy = source with { Id = Guid.NewGuid(), Name = name, Enabled = false, Owner = owner == ICallContext.AllDevicesScopeKey ? null : owner };
        await ChangeAsync(ctx, list =>
        {
            list.Add(copy);
            return true;
        }, typesChanged: true, copy.Owner, ct).ConfigureAwait(false);
        return new SaveReply(copy, null, null) { Dropped = dropped };
    }

    /// <summary>"&lt;name&gt; (copy)", "(copy 2)", ... within the name limit.</summary>
    internal static string CopyName(string name, IReadOnlySet<string> taken)
    {
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? " (copy)" : $" (copy {n})";
            var stem = name.Length + suffix.Length > DeviceTriggersPluginInfo.MaxNameLength ? name[..(DeviceTriggersPluginInfo.MaxNameLength - suffix.Length)].TrimEnd() : name;
            var candidate = stem + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Changes the stored list (serialized), then the event types and the connections follow.</summary>
    private async Task ChangeAsync(ICorePluginContext ctx, Func<List<TriggerDefinition>, bool> change, bool typesChanged, string? owner, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        List<TriggerDefinition> updated;
        try
        {
            updated = [.. Triggers];
            if (!change(updated))
            {
                return;
            }

            await ctx.Settings.SetAsync(SettingsKey, TriggerJson.Serialize(updated), ct).ConfigureAwait(false);
            lock (_sync)
            {
                _triggers = updated;
            }
        }
        finally
        {
            _write.Release();
        }

        if (typesChanged)
        {
            EventTypesChanged?.Invoke(this, EventArgs.Empty);
        }

        // The resolve reads every device and opens connections: background work, never in the user's call.
        await RunDetached(() => ResolveAsync(ct)).ConfigureAwait(false);
        PublishChanged([], owner);
    }

    /// <summary>
    /// Resolves every trigger to the devices it covers now: the chosen ones, plus with <see cref="TriggerDefinition.AllDevices"/>
    /// or <see cref="TriggerDefinition.Tags"/> every managed device (with that tag) that can send events. Runs after every
    /// change and every <see cref="DeviceTriggersOptions.ResolveInterval"/>, so new and newly tagged devices join; the
    /// connections follow only when a set changed. Resolves run one at a time and each reads the stored triggers once it
    /// runs, so the runtime always gets the lists in order and an older one never replaces a newer one.
    /// </summary>
    private async Task ResolveAsync(CancellationToken ct)
    {
        await _resolve.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ResolveLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _resolve.Release();
        }
    }

    private async Task ResolveLockedAsync(CancellationToken ct)
    {
        if (_ctx is not { } ctx || _runtime is not { } runtime)
        {
            return;
        }

        var stored = Triggers;
        var scoped = stored.Any(t => OwnerScope.Of(t.Owner) != ICallContext.AllDevicesScopeKey);
        var devices = scoped || stored.Any(t => t.AllDevices || t.Tags.Count > 0) ? await ctx.Devices.ListAsync(ct).ConfigureAwait(false) : [];
        var byId = scoped ? devices.ToDictionary(d => d.Id) : null;
        bool Covers(TriggerDefinition t, IDeviceInfo d) => Owns(ctx, t, d);
        var next = new Dictionary<Guid, Resolution>();
        var notWatched = new Dictionary<Guid, int>();
        var changed = false;
        var devicesChanged = false; // a trigger's devices (tag or All devices) changed: its types carry them
        lock (_sync)
        {
            // All turned-on triggers together watch at most MaxWatchedDevices devices: the chosen devices always (saving
            // checks they fit), then the devices of tags and All devices that are watched already, then new ones while
            // room is left. A trigger that misses devices that way says so on its row.
            var watched = stored.Where(t => t.Enabled).SelectMany(t => t.DeviceIds).ToHashSet();
            var before = _resolved.Values.Where(r => r.Value.Enabled).SelectMany(r => r.Value.Implicit).ToHashSet();
            var wanted = stored.Where(t => t.Enabled && (t.AllDevices || t.Tags.Count > 0))
                .SelectMany(t => devices.Where(d => Covers(t, d) && t.CoversImplicitly(d.Tags) && CanSendEvents(d)).Select(d => d.Id))
                .Where(id => !watched.Contains(id))
                .Distinct()
                .OrderBy(id => before.Contains(id) ? 0 : 1)
                .ToList();
            foreach (var id in wanted)
            {
                if (watched.Count >= _options.MaxWatchedDevices)
                {
                    break;
                }

                watched.Add(id);
            }

            foreach (var trigger in stored)
            {
                var resolved = trigger;
                if (byId is not null && OwnerScope.Of(trigger.Owner) != ICallContext.AllDevicesScopeKey)
                {
                    // A group's trigger: only the chosen devices the group still has (none for a deleted group).
                    var kept = trigger.DeviceIds.Where(id => byId.TryGetValue(id, out var d) && Covers(trigger, d)).ToList();
                    if (kept.Count != trigger.DeviceIds.Count)
                    {
                        resolved = trigger with { DeviceIds = kept };
                    }
                }

                if (trigger.AllDevices || trigger.Tags.Count > 0)
                {
                    var chosen = trigger.DeviceIds.ToHashSet();
                    var covered = devices
                        .Where(d => !chosen.Contains(d.Id) && Covers(trigger, d) && trigger.CoversImplicitly(d.Tags) && CanSendEvents(d))
                        .Select(d => d.Id)
                        .ToList();
                    var implicitIds = (trigger.Enabled ? covered.Where(watched.Contains) : covered).ToHashSet();
                    if (implicitIds.Count < covered.Count)
                    {
                        notWatched[trigger.Id] = covered.Count - implicitIds.Count;
                    }

                    resolved = resolved with { DeviceIds = [.. resolved.DeviceIds, .. implicitIds], Implicit = implicitIds };
                }

                var previous = _resolved.GetValueOrDefault(trigger.Id);
                devicesChanged |= previous is not null && !previous.Value.DeviceIds.SequenceEqual(resolved.DeviceIds);
                changed |= previous is null || !ReferenceEquals(previous.Source, trigger) || !previous.Value.DeviceIds.SequenceEqual(resolved.DeviceIds);
                next[trigger.Id] = previous is not null && ReferenceEquals(previous.Source, trigger) && previous.Value.DeviceIds.SequenceEqual(resolved.DeviceIds)
                    ? previous
                    : new Resolution(trigger, resolved);
            }

            changed |= next.Count != _resolved.Count;
            _resolved = next;
            _notWatched = notWatched;
        }

        if (changed)
        {
            runtime.Apply([.. next.Values.Select(r => r.Value)]);
        }

        if (devicesChanged)
        {
            EventTypesChanged?.Invoke(this, EventArgs.Empty); // the types carry the devices: subscribers (Dashboards) follow
        }
    }

    private async Task ResolveSafelyAsync()
    {
        if (_running is not { } running)
        {
            return;
        }

        try
        {
            await ResolveAsync(running.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The plugin stops.
        }
#pragma warning disable CA1031 // The next round tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogResolveFailed(Logger, ex);
        }
    }

    /// <summary>
    /// Devices the turned-on <paramref name="triggers"/> watch together: chosen, tagged and all devices, each within its
    /// owner's devices. Reads every device (<see cref="ICorePluginContext.System"/>, declared in <see cref="SystemAccessReason"/>):
    /// the limit is the server's; only the count is used.
    /// </summary>
    private async Task<int> WatchedCountAsync(IReadOnlyCollection<TriggerDefinition> triggers, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException(NotRunning);
        var devices = await ctx.System.Devices.ListAsync(ct).ConfigureAwait(false);
        var watched = triggers.SelectMany(t => t.DeviceIds).ToHashSet();
        foreach (var trigger in triggers.Where(t => t.AllDevices || t.Tags.Count > 0))
        {
            watched.UnionWith(devices.Where(d => Owns(ctx, trigger, d) && trigger.CoversImplicitly(d.Tags) && CanSendEvents(d)).Select(d => d.Id));
        }

        return watched.Count;
    }

    /// <summary>Whether a device is in the devices of the trigger's owner (every device for all devices; none for a deleted group).</summary>
    private static bool Owns(ICorePluginContext ctx, TriggerDefinition trigger, IDeviceInfo device)
    {
        var owner = OwnerScope.Of(trigger.Owner);
        return owner == ICallContext.AllDevicesScopeKey || (ctx.DeviceScopes?.Contains(owner, device) ?? false);
    }

    /// <summary>A device of a tag or of all devices: the stream API listed, or its API list not read yet.</summary>
    private static bool CanSendEvents(IDeviceInfo device) =>
        device.Apis.Count == 0 || device.Apis.Supports(DeviceTriggersPluginInfo.ApiId, DeviceTriggersPluginInfo.ApiVersion);

    private sealed record Resolution(TriggerDefinition Source, TriggerDefinition Value);

    /// <summary>Devices removed from OADM leave their triggers.</summary>
    private async Task RemoveDevicesAsync(IReadOnlyCollection<Guid> devices)
    {
        if (_ctx is not { } ctx)
        {
            return;
        }

        try
        {
            await ChangeAsync(ctx, list =>
            {
                var changed = false;
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i].DeviceIds.Any(devices.Contains))
                    {
                        list[i] = list[i] with { DeviceIds = [.. list[i].DeviceIds.Where(d => !devices.Contains(d))] };
                        changed = true;
                    }
                }

                return changed;
            }, typesChanged: false, null, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Tried again on the next sweep.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRemoveFailed(Logger, ex);
        }
    }

    private static async Task<List<TriggerDefinition>> LoadAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        var json = await ctx.Settings.GetAsync(SettingsKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<TriggerDefinition>>(json, TriggerJson.Options)?.Where(t => t.Id != Guid.Empty).ToList() ?? [];
        }
        catch (System.Text.Json.JsonException ex)
        {
            LogLoadFailed(ctx.Logger ?? NullLogger.Instance, ex);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device triggers: {Count} triggers, {Enabled} turned on")]
    private static partial void LogStarted(ILogger logger, int count, int enabled);

    [LoggerMessage(Level = LogLevel.Error, Message = "Device triggers: the stored triggers could not be read")]
    private static partial void LogLoadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device triggers: the devices of the triggers could not be read")]
    private static partial void LogResolveFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device triggers: removed devices could not be taken out of the triggers")]
    private static partial void LogRemoveFailed(ILogger logger, Exception ex);
}
