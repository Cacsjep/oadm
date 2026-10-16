using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DeviceTriggers.Runtime;
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

    /// <summary>Two types per trigger: the event and "&lt;Trigger&gt;: connection lost".</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes
    {
        get
        {
            lock (_sync)
            {
                return [.. _triggers.SelectMany(TypesOf)];
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
        _runtime.Changed += (_, ids) => ctx.Events?.Publish(DeviceTriggersMethods.ChangedTopic, TriggerJson.Serialize(new ChangedEvent([.. ids])));
        _runtime.DevicesRemoved += (_, ids) => _ = RemoveDevicesAsync(ids);
        EventTypesChanged?.Invoke(this, EventArgs.Empty);
        await ResolveAsync(ct).ConfigureAwait(false);
        _resolveTimer = _time.CreateTimer(_ => _ = ResolveSafelyAsync(), null, _options.ResolveInterval, _options.ResolveInterval);
        var enabled = stored.Count(t => t.Enabled);
        LogStarted(Logger, stored.Count, enabled);
    }

    public async Task StopAsync(CancellationToken ct)
    {
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

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("The device triggers plugin is not running.");
        switch (method)
        {
            case DeviceTriggersMethods.List:
                return TriggerJson.Serialize(new ListReply([.. Triggers.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(Summary)]));
            case DeviceTriggersMethods.Get:
            {
                var id = TriggerJson.Deserialize<IdRequest>(payloadJson).Id;
                var trigger = Triggers.FirstOrDefault(t => t.Id == id);
                return TriggerJson.Serialize(new GetReply(trigger, trigger is null || _runtime is null ? [] : _runtime.StatesOf(Resolved(trigger))));
            }

            case DeviceTriggersMethods.StartRead:
                return TriggerJson.Serialize(_reads!.Start(TriggerJson.Deserialize<StartReadRequest>(payloadJson).DeviceIds ?? [], _running?.Token ?? CancellationToken.None));
            case DeviceTriggersMethods.ReadStatus:
                return TriggerJson.Serialize(_reads!.StatusOf(TriggerJson.Deserialize<ReadStatusRequest>(payloadJson).JobId ?? string.Empty)
                    ?? throw new ArgumentException("The read of the event lists ended. Read the events again."));
            case DeviceTriggersMethods.Save:
                return TriggerJson.Serialize(await SaveAsync(ctx, TriggerJson.Deserialize<SaveRequest>(payloadJson).Trigger, ct).ConfigureAwait(false));
            case DeviceTriggersMethods.Delete:
            {
                var id = TriggerJson.Deserialize<IdRequest>(payloadJson).Id;
                await ChangeAsync(ctx, list => list.RemoveAll(t => t.Id == id) > 0, typesChanged: true, ct).ConfigureAwait(false);
                return null;
            }

            case DeviceTriggersMethods.SetEnabled:
            {
                var request = TriggerJson.Deserialize<SetEnabledRequest>(payloadJson);
                if (request.Enabled && Triggers.FirstOrDefault(t => t.Id == request.Id) is { Enabled: false } turningOn
                    && WatchedCount([.. Triggers.Where(t => t.Enabled), turningOn], await ctx.Devices.ListAsync(ct).ConfigureAwait(false)) > _options.MaxWatchedDevices)
                {
                    throw new ArgumentException(DeviceTriggersPluginInfo.TooManyDevicesText);
                }

                await ChangeAsync(ctx, list =>
                {
                    var i = list.FindIndex(t => t.Id == request.Id);
                    if (i < 0 || list[i].Enabled == request.Enabled)
                    {
                        return false;
                    }

                    list[i] = list[i] with { Enabled = request.Enabled };
                    return true;
                }, typesChanged: false, ct).ConfigureAwait(false);
                return null;
            }

            case DeviceTriggersMethods.Duplicate:
                return TriggerJson.Serialize(await DuplicateAsync(ctx, TriggerJson.Deserialize<IdRequest>(payloadJson).Id, ct).ConfigureAwait(false));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _write.Dispose();
        _resolve.Dispose();
    }

    /// <summary>The event types of a trigger. Disabled triggers keep theirs, so their rules stay valid.</summary>
    internal static IEnumerable<EventTypeInfo> TypesOf(TriggerDefinition trigger)
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
        };
        yield return new EventTypeInfo(
            DeviceTriggersPluginInfo.LostTypeId(trigger.Id),
            trigger.Name + DeviceTriggersPluginInfo.LostSuffix,
            DeviceTriggersPluginInfo.EventGroup,
            EventKind.Stateful,
            Fields: [new EventField("reason", "Reason") { Filter = false }])
        {
            Description = $"No events of this trigger from the device for {DeviceTriggersPluginInfo.LostDelay.TotalSeconds:0} s.\n\nValues:\n• {{reason}}",
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

    private async Task<SaveReply> SaveAsync(ICorePluginContext ctx, TriggerDefinition? incoming, CancellationToken ct)
    {
        if (incoming is null)
        {
            return new SaveReply(null, "The trigger is empty.", null);
        }

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
        };
        var others = Triggers.Where(t => t.Id != trigger.Id).ToList();
        var errors = TriggerValidation.Check(trigger, others.Select(o => o.Name)).ToDictionary();
        if (!errors.ContainsKey(TriggerValidation.DevicesField))
        {
            var devices = (await ctx.Devices.ListAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
            var unknown = trigger.DeviceIds.Count(id => !devices.ContainsKey(id));
            var unsupported = trigger.DeviceIds.Where(id => devices.TryGetValue(id, out var d) && d.Apis.Count > 0
                && !d.Apis.Supports(DeviceTriggersPluginInfo.ApiId, DeviceTriggersPluginInfo.ApiVersion)).Select(id => devices[id].Address).ToList();
            if (unknown > 0)
            {
                errors[TriggerValidation.DevicesField] = unknown == 1 ? "A chosen device was removed from OADM." : $"{unknown} chosen devices were removed from OADM.";
            }
            else if (unsupported.Count > 0)
            {
                errors[TriggerValidation.DevicesField] = $"{string.Join(", ", unsupported.Take(3))}{(unsupported.Count > 3 ? $" and {unsupported.Count - 3} more" : string.Empty)}: {DeviceTriggersPluginInfo.NotSupportedReason}.";
            }
        }

        if (!errors.ContainsKey(TriggerValidation.DevicesField) && trigger.Enabled
            && WatchedCount([.. others.Where(o => o.Enabled), trigger], await ctx.Devices.ListAsync(ct).ConfigureAwait(false)) > _options.MaxWatchedDevices)
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
        }, typesChanged: true, ct).ConfigureAwait(false);
        return new SaveReply(saved, null, null);
    }

    private async Task<SaveReply> DuplicateAsync(ICorePluginContext ctx, Guid id, CancellationToken ct)
    {
        var source = Triggers.FirstOrDefault(t => t.Id == id);
        if (source is null)
        {
            return new SaveReply(null, "The trigger no longer exists.", null);
        }

        if (Triggers.Count >= DeviceTriggersPluginInfo.MaxTriggers)
        {
            return new SaveReply(null, $"At most {DeviceTriggersPluginInfo.MaxTriggers} triggers.", null);
        }

        var names = Triggers.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = CopyName(source.Name, names);
        var copy = source with { Id = Guid.NewGuid(), Name = name, Enabled = false };
        await ChangeAsync(ctx, list =>
        {
            list.Add(copy);
            return true;
        }, typesChanged: true, ct).ConfigureAwait(false);
        return new SaveReply(copy, null, null);
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
    private async Task ChangeAsync(ICorePluginContext ctx, Func<List<TriggerDefinition>, bool> change, bool typesChanged, CancellationToken ct)
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

        await ResolveAsync(ct).ConfigureAwait(false);
        ctx.Events?.Publish(DeviceTriggersMethods.ChangedTopic, TriggerJson.Serialize(new ChangedEvent([])));
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
        var devices = stored.Any(t => t.AllDevices || t.Tags.Count > 0) ? await ctx.Devices.ListAsync(ct).ConfigureAwait(false) : [];
        var next = new Dictionary<Guid, Resolution>();
        var notWatched = new Dictionary<Guid, int>();
        var changed = false;
        lock (_sync)
        {
            // All turned-on triggers together watch at most MaxWatchedDevices devices: the chosen devices always (saving
            // checks they fit), then the devices of tags and All devices that are watched already, then new ones while
            // room is left. A trigger that misses devices that way says so on its row.
            var watched = stored.Where(t => t.Enabled).SelectMany(t => t.DeviceIds).ToHashSet();
            var before = _resolved.Values.Where(r => r.Value.Enabled).SelectMany(r => r.Value.Implicit).ToHashSet();
            var wanted = stored.Where(t => t.Enabled && (t.AllDevices || t.Tags.Count > 0))
                .SelectMany(t => devices.Where(d => t.CoversImplicitly(d.Tags) && CanSendEvents(d)).Select(d => d.Id))
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
                if (trigger.AllDevices || trigger.Tags.Count > 0)
                {
                    var chosen = trigger.DeviceIds.ToHashSet();
                    var covered = devices
                        .Where(d => !chosen.Contains(d.Id) && trigger.CoversImplicitly(d.Tags) && CanSendEvents(d))
                        .Select(d => d.Id)
                        .ToList();
                    var implicitIds = (trigger.Enabled ? covered.Where(watched.Contains) : covered).ToHashSet();
                    if (implicitIds.Count < covered.Count)
                    {
                        notWatched[trigger.Id] = covered.Count - implicitIds.Count;
                    }

                    resolved = trigger with { DeviceIds = [.. trigger.DeviceIds, .. implicitIds], Implicit = implicitIds };
                }

                var previous = _resolved.GetValueOrDefault(trigger.Id);
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

    /// <summary>Devices the turned-on <paramref name="triggers"/> watch together: chosen, tagged and all devices.</summary>
    internal static int WatchedCount(IReadOnlyCollection<TriggerDefinition> triggers, IReadOnlyList<IDeviceInfo> devices)
    {
        var watched = triggers.SelectMany(t => t.DeviceIds).ToHashSet();
        foreach (var trigger in triggers.Where(t => t.AllDevices || t.Tags.Count > 0))
        {
            watched.UnionWith(devices.Where(d => trigger.CoversImplicitly(d.Tags) && CanSendEvents(d)).Select(d => d.Id));
        }

        return watched.Count;
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
            }, typesChanged: false, CancellationToken.None).ConfigureAwait(false);
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
