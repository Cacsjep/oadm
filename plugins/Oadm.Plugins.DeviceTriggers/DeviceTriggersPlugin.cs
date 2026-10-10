using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DeviceTriggers.Runtime;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers;

/// <summary>
/// Core plugin "Device triggers" (off by default; user decisions 2026-10-10, <c>docs/specs/device-triggers.md</c>). An
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
    private readonly Lock _sync = new();
    private List<TriggerDefinition> _triggers = [];
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
        _runtime.Apply(stored);
        var enabled = stored.Count(t => t.Enabled);
        LogStarted(Logger, stored.Count, enabled);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_running is { } running)
        {
            _running = null;
            await running.CancelAsync().ConfigureAwait(false);
            if (_reads is { } reads)
            {
                await reads.StopAsync().ConfigureAwait(false);
            }

            running.Dispose();
        }

        if (_runtime is { } runtime)
        {
            _runtime = null;
            await runtime.DisposeAsync().ConfigureAwait(false);
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
                return TriggerJson.Serialize(new GetReply(trigger, trigger is null || _runtime is null ? [] : _runtime.StatesOf(trigger)));
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
    }

    /// <summary>The event types of a trigger. Disabled triggers keep theirs, so their rules stay valid.</summary>
    internal static IEnumerable<EventTypeInfo> TypesOf(TriggerDefinition trigger)
    {
        var what = trigger.FilterText.Length == 0 ? trigger.EventName : $"{trigger.EventName} · {trigger.FilterText}";
        var values = trigger.Values.Count == 0 ? string.Empty : " Values: " + string.Join(", ", trigger.Values.Select(v => "{" + v + "}")) + ".";
        yield return new EventTypeInfo(
            DeviceTriggersPluginInfo.TypeId(trigger.Id),
            trigger.Name,
            DeviceTriggersPluginInfo.EventGroup,
            trigger.Stateful ? EventKind.Stateful : EventKind.Stateless)
        {
            Description = what + "." + values,
        };
        yield return new EventTypeInfo(
            DeviceTriggersPluginInfo.LostTypeId(trigger.Id),
            trigger.Name + DeviceTriggersPluginInfo.LostSuffix,
            DeviceTriggersPluginInfo.EventGroup,
            EventKind.Stateful)
        {
            Description = $"No events of this trigger from the device for {DeviceTriggersPluginInfo.LostDelay.TotalSeconds:0} s. Values: {{reason}}.",
        };
    }

    private TriggerSummary Summary(TriggerDefinition trigger)
    {
        var (on, lost, waiting, last) = _runtime?.CountsOf(trigger) ?? (0, 0, 0, null);
        return new TriggerSummary(trigger.Id, trigger.Name, trigger.Enabled, trigger.EventName, trigger.FilterText, trigger.Stateful, trigger.DeviceIds.Count, on, lost, waiting, last);
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
            Filters = [.. (incoming.Filters ?? []).Where(f => !string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(f.Value)).DistinctBy(f => f.Name)],
            Values = [.. (incoming.Values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal)],
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

        _runtime?.Apply(updated);
        ctx.Events?.Publish(DeviceTriggersMethods.ChangedTopic, TriggerJson.Serialize(new ChangedEvent([])));
    }

    /// <summary>Devices removed from OADM leave their triggers (user decision).</summary>
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device triggers: removed devices could not be taken out of the triggers")]
    private static partial void LogRemoveFailed(ILogger logger, Exception ex);
}
