using System.Collections.Frozen;
using System.Globalization;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

public sealed class RuleEngineOptions
{
    /// <summary>A plugin event type whose source has not reported after this long counts as reported with nothing on.</summary>
    public TimeSpan SourceReadyTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Schedules, ready timeouts and certificate days are checked this often.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Certificate expiry of the built-in events is recomputed this often.</summary>
    public TimeSpan CertificateCheckInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Commands (events, device changes) waiting for the engine; beyond it new events are dropped and logged. Rule changes
    /// are never dropped: they wait for room (the store already committed them).
    /// </summary>
    public int QueueCapacity { get; init; } = 100_000;
}

/// <summary>The live state of one rule for the Rules tab.</summary>
public sealed record RuleRuntimeStatus(Guid RuleId, int ActiveCount, string? WaitingFor, DateTime? LastFiredUtc);

/// <summary>
/// The event rules engine ("Event rules" in CLAUDE.md). One loop owns all state:
/// the current on states of every stateful event (per type and device), the managed devices, and per rule the subjects
/// (devices the trigger fired for) it is on for. Publishers and the device feed only queue commands.
/// <list type="bullet">
/// <item>Simple mode: the first condition is the trigger; further conditions (stateful events, schedules) must hold.
/// A further event condition with <see cref="DeviceSelectorMode.EventDevice"/> means the trigger's device, any other
/// selector means "on for any of these devices".</item>
/// <item>A stateful rule turns on for a subject when all conditions hold and off when one ends; a stateless trigger
/// fires while the others hold. Actions on "the device of the event" (and actions without devices) run per subject;
/// actions on chosen / tag / all devices run when the first subject turns on and when the last one turns off.</item>
/// <item>Limits per rule: the dead time per subject between two "turns on", and the max runs per minute. A suppressed "turns on"
/// also suppresses its "turns off".</item>
/// <item>Restart: the subjects a rule was on for are stored. A rule is evaluated once every event type it uses is ready
/// (its source reported with ReplaceStates, or the source is off, or <see cref="RuleEngineOptions.SourceReadyTimeout"/>
/// passed); then only real differences run actions.</item>
/// </list>
/// </summary>
public sealed partial class RuleEngine : IAsyncDisposable
{
    private readonly EventCatalog _catalog;
    private readonly RuleStore _store;
    private readonly EventLog _log;
    private readonly IActionDispatcher _actions;
    private readonly DeviceRepository? _deviceRepository;
    private readonly IDeviceChangeFeed? _deviceFeed;
    private readonly TimeProvider _time;
    private readonly RuleEngineOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<Command> _commands;
    private readonly CancellationTokenSource _stop = new();

    // Owned by the loop.
    private readonly Dictionary<string, Dictionary<Guid, IReadOnlyDictionary<string, string>>> _on = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _waitingSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EventSubscription> _subscriptions = [];
    private int _subscriptionCount;
    private readonly HashSet<string> _knownTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, DeviceRef> _devices = [];
    private readonly Dictionary<Guid, Runtime> _rules = [];
    // Pending state writes; the last change of a (rule, subject) wins. Dropped only after a successful write.
    private readonly Dictionary<(Guid Rule, Guid Subject), (bool Add, DateTime Since)> _stateOps = [];
    private readonly Dictionary<Guid, DateTime> _firedDirty = [];
    private DateTime _lastCertificateCheck = DateTime.MinValue;
    private DateTime _startedUtc;

    private Task? _loop;
    private Task? _ticker;
    private long _dropped;
    private int _disposed;
    private volatile FrozenSet<string> _watched = FrozenSet<string>.Empty;
    private volatile Dictionary<Guid, RuleRuntimeStatus> _status = [];

    public RuleEngine(
        EventCatalog catalog,
        RuleStore store,
        EventLog log,
        IActionDispatcher actions,
        DeviceRepository? deviceRepository = null,
        IDeviceChangeFeed? deviceFeed = null,
        TimeProvider? time = null,
        RuleEngineOptions? options = null,
        ILogger<RuleEngine>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _deviceRepository = deviceRepository;
        _deviceFeed = deviceFeed;
        _time = time ?? TimeProvider.System;
        _options = options ?? new RuleEngineOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full (events are dropped); WriteAsync waits
            SingleReader = true,
        });
    }

    /// <summary>Raised (on the engine loop) after the state of a rule changed; the gRPC watch coalesces it.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Live state per rule (snapshot).</summary>
    public IReadOnlyDictionary<Guid, RuleRuntimeStatus> Status => _status;

    /// <summary>Whether an enabled rule uses the event type, or a plugin subscribed to the events.</summary>
    public bool IsWatched(string typeId) => Volatile.Read(ref _subscriptionCount) > 0 || _watched.Contains(typeId);

    /// <summary>The event subscriptions handed to a core plugin (<c>ICorePluginContext.EventSubscriptions</c>).</summary>
    public IEventSubscriptions SubscriptionsFor(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return new Subscriptions(this, owner);
    }

    /// <summary>The publisher handed to a core plugin (<c>ICorePluginContext.EventPublisher</c>).</summary>
    public IEventPublisher PublisherFor(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return new Publisher(this, sourceId);
    }

    /// <summary>Loads the devices, the rules and their stored states, then starts the loop. Call once at server start.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _startedUtc = _time.GetUtcNow().UtcDateTime;
        if (_deviceFeed is not null)
        {
            _deviceFeed.Changed += OnDeviceChanged;
        }

        _catalog.Registry.EventTypesChanged += OnEventTypesChanged;
        if (_deviceRepository is not null)
        {
            foreach (var device in await _deviceRepository.ListDevicesAsync(ct).ConfigureAwait(false))
            {
                _devices[device.Id] = DeviceRef.From(device);
            }
        }

        var states = await _store.LoadStatesAsync(ct).ConfigureAwait(false);
        foreach (var stored in await _store.ListAsync(ct).ConfigureAwait(false))
        {
            if (!stored.Definition.Enabled)
            {
                continue;
            }

            var runtime = new Runtime(stored, _catalog);
            if (states.TryGetValue(stored.Definition.Id, out var active))
            {
                foreach (var (subject, since) in active)
                {
                    runtime.Active[subject] = since;
                }
            }

            _rules[stored.Definition.Id] = runtime;
        }

        // Built-in device status: known at once.
        foreach (var type in BuiltInEvents.Types)
        {
            _ready.Add(type.Id);
        }

        RecomputeBuiltIn(_devices.Keys);
        _lastCertificateCheck = _startedUtc;
        SyncSources(initial: true);
        RefreshWatched();
        EvaluateAllReady();
        await FlushStatesAsync(ct).ConfigureAwait(false);
        PublishStatus();
        _loop = Task.Run(LoopAsync, CancellationToken.None);
        _ticker = Task.Run(TickAsync, CancellationToken.None);
        LogStarted(_rules.Count);
    }

    /// <summary>Applies a saved, changed, turned on or off, or deleted rule; returns when the engine has processed it.</summary>
    public Task ApplyRuleChangeAsync(StoredRule? previous, StoredRule? current, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new RuleChanged(previous, current, done);
        if (_commands.Writer.TryWrite(command))
        {
            return done.Task.WaitAsync(ct);
        }

        return QueueRuleChangeAsync(command, ct);
    }

    /// <summary>
    /// The queue is full of events: wait for room, not cancellable by the caller (the store already committed the change,
    /// so the engine must apply it). Only a stopped engine refuses it; the next start loads the stored rules anyway.
    /// </summary>
    private async Task QueueRuleChangeAsync(RuleChanged command, CancellationToken ct)
    {
        try
        {
            await _commands.Writer.WriteAsync(command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return; // stopping
        }

        await command.Done.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A plugin package was turned on or off, or a source's event types changed.</summary>
    public void SourcesChanged() => Enqueue(new SourcesChangedCommand());

    /// <summary>Waits until every command queued so far was processed (tests).</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new Barrier(done), ct).ConfigureAwait(false);
        await done.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs the periodic checks now (tests with a fake clock).</summary>
    public void Tick() => Enqueue(new TickCommand());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_deviceFeed is not null)
        {
            _deviceFeed.Changed -= OnDeviceChanged;
        }

        _catalog.Registry.EventTypesChanged -= OnEventTypesChanged;
        _commands.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { _loop, _ticker })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The loop has stopped: write what is still pending, bounded, not tied to the stop token.
        if (_loop is not null)
        {
            using var last = new CancellationTokenSource(FinalFlushTimeout);
            try
            {
                await FlushStatesAsync(last.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Shutdown: what cannot be written is logged; the next start re-evaluates.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPersistFailed(ex);
            }
        }

        _stop.Dispose();
    }

    private void OnDeviceChanged(object? sender, DeviceChange change) => Enqueue(new DeviceCommand(change));

    private void OnEventTypesChanged(object? sender, EventArgs e) => SourcesChanged();

    /// <summary>Queues a command that must not be dropped (subscribe, unsubscribe): waits for room when the queue is full.</summary>
    private void EnqueueControl(Command command)
    {
        if (!_commands.Writer.TryWrite(command))
        {
            _ = _commands.Writer.WriteAsync(command).AsTask().ContinueWith(
                static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private void Enqueue(Command command)
    {
        if (!_commands.Writer.TryWrite(command) && Interlocked.Increment(ref _dropped) % 1000 == 1)
        {
            LogQueueFull();
        }
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (await _commands.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var barriers = new List<TaskCompletionSource>();
                var processed = 0;
                while (processed < 5_000 && _commands.Reader.TryRead(out var command))
                {
                    processed++;
                    try
                    {
                        if (command is Barrier barrier)
                        {
                            barriers.Add(barrier.Done);
                            continue;
                        }

                        Handle(command);
                    }
#pragma warning disable CA1031 // One bad event must not stop the rules.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogCommandFailed(ex, command.GetType().Name);
                        if (command is RuleChanged failed)
                        {
                            failed.Done.TrySetException(ex);
                        }
                    }
                }

                try
                {
                    await FlushStatesAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPersistFailed(ex);
                }

                PublishStatus();
                foreach (var done in barriers)
                {
                    done.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(_options.TickInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Enqueue(new TickCommand());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Handle(Command command)
    {
        switch (command)
        {
            case StateCommand state:
                if (IsFromActiveSource(state.SourceId, state.TypeId, out var stateType) && stateType!.Stateful)
                {
                    SetState(stateType, state.DeviceId ?? Guid.Empty, state.IsOn, state.Data);
                }

                break;
            case ReplaceCommand replace:
                if (IsFromActiveSource(replace.SourceId, replace.TypeId, out var replaceType) && replaceType!.Stateful)
                {
                    ReplaceStates(replaceType, replace.OnDevices);
                }

                break;
            case FireCommand fire:
                if (IsFromActiveSource(fire.SourceId, fire.TypeId, out var fireType) && !fireType!.Stateful)
                {
                    FireEvent(fireType, fire.DeviceId ?? Guid.Empty, fire.Data);
                }

                break;
            case DeviceCommand device:
                HandleDevice(device.Change);
                break;
            case RuleChanged changed:
                HandleRuleChanged(changed);
                changed.Done.TrySetResult();
                break;
            case SourcesChangedCommand:
                SyncSources(initial: false);
                break;
            case TickCommand:
                HandleTick();
                break;
            case SubscribeCommand subscribe:
                StartSubscription(subscribe.Subscription);
                break;
            case UnsubscribeCommand unsubscribe:
                _subscriptions.Remove(unsubscribe.Subscription);
                break;
        }
    }

    // ---------- subscriptions ----------
    private void StartSubscription(EventSubscription subscription)
    {
        if (subscription.IsDisposed)
        {
            return;
        }

        _subscriptions.Add(subscription);
        var now = _time.GetUtcNow();
        foreach (var (typeId, set) in _on)
        {
            foreach (var (device, data) in set)
            {
                subscription.Post(new PublishedEvent(typeId, device == Guid.Empty ? null : device, PublishedEventKind.On, data, now, FromSnapshot: true));
            }
        }
    }

    /// <summary>Hands an event to every subscription (never waits: each has its own queue).</summary>
    private void Publish(string typeId, Guid device, PublishedEventKind kind, IReadOnlyDictionary<string, string>? data)
    {
        if (_subscriptions.Count == 0)
        {
            return;
        }

        var published = new PublishedEvent(typeId, device == Guid.Empty ? null : device, kind, data ?? EmptyData, _time.GetUtcNow());
        foreach (var subscription in _subscriptions)
        {
            subscription.Post(published);
        }
    }

    private bool IsFromActiveSource(string sourceId, string typeId, out CatalogEventType? type)
    {
        type = _catalog.FindEventType(typeId);
        return type is not null && type.Available && string.Equals(type.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
            && !_offSources.Contains(sourceId);
    }

    // ---------- event states ----------
    private void SetState(CatalogEventType type, Guid device, bool isOn, IReadOnlyDictionary<string, string>? data)
    {
        var set = OnSet(type.Id);
        var was = set.TryGetValue(device, out var oldData);
        if (isOn)
        {
            set[device] = data ?? (was ? oldData! : EmptyData);
        }
        else
        {
            set.Remove(device);
        }

        if (was != isOn)
        {
            LogEvent(type, device, isOn);
            Publish(type.Id, device, isOn ? PublishedEventKind.On : PublishedEventKind.Off, isOn ? set[device] : null);
            OnTypeChanged(type.Id, [device]);
        }
        else if (isOn && data is not null && !SameData(oldData!, data))
        {
            Publish(type.Id, device, PublishedEventKind.On, data);
            OnTypeChanged(type.Id, [device]); // e.g. another port: filters may match now
        }
    }

    private void ReplaceStates(CatalogEventType type, IReadOnlyCollection<Guid> onDevices)
    {
        var set = OnSet(type.Id);
        var wanted = onDevices.Count == 0 ? [] : onDevices.ToHashSet();
        var changed = new List<Guid>();
        foreach (var device in set.Keys.Where(d => !wanted.Contains(d)).ToList())
        {
            set.Remove(device);
            changed.Add(device);
            LogEvent(type, device, false);
            Publish(type.Id, device, PublishedEventKind.Off, null);
        }

        foreach (var device in wanted.Where(d => !set.ContainsKey(d)))
        {
            set[device] = EmptyData;
            changed.Add(device);
            LogEvent(type, device, true);
            Publish(type.Id, device, PublishedEventKind.On, null);
        }

        var becameReady = _ready.Add(type.Id);
        _waitingSince.Remove(type.Id);
        if (becameReady)
        {
            EvaluateAllReady();
        }
        else if (changed.Count > 0)
        {
            OnTypeChanged(type.Id, changed);
        }
    }

    private Dictionary<Guid, IReadOnlyDictionary<string, string>> OnSet(string typeId)
    {
        if (!_on.TryGetValue(typeId, out var set))
        {
            set = [];
            _on[typeId] = set;
        }

        return set;
    }

    private bool IsOn(string typeId, Guid device, Dictionary<string, string> filter)
    {
        if (!_on.TryGetValue(typeId, out var set) || !set.TryGetValue(device, out var data))
        {
            return false;
        }

        return Matches(data, filter);
    }

    private static bool SameData(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var value) && string.Equals(value, e.Value, StringComparison.Ordinal));

    private static bool Matches(IReadOnlyDictionary<string, string> data, Dictionary<string, string> filter) =>
        filter.Count == 0 || filter.All(f => data.TryGetValue(f.Key, out var value) && string.Equals(value, f.Value, StringComparison.OrdinalIgnoreCase));

    private void OnTypeChanged(string typeId, IReadOnlyCollection<Guid> devices)
    {
        foreach (var runtime in _rules.Values)
        {
            if (!runtime.Types.Contains(typeId) || !IsEvaluable(runtime))
            {
                continue;
            }

            if (!runtime.Stateful)
            {
                // A one-shot rule only fires on its trigger; state changes of its other conditions change nothing now.
                continue;
            }

            var full = runtime.Definition.Conditions.Skip(1).Any(c =>
                c.Kind == RuleConditionKind.Event && string.Equals(c.EventTypeId, typeId, StringComparison.OrdinalIgnoreCase)
                && c.Devices.Mode != DeviceSelectorMode.EventDevice);
            if (full)
            {
                Reconcile(runtime, AllCandidates(runtime), typeId);
            }
            else
            {
                Reconcile(runtime, runtime.TriggerScope == EventScope.Server ? [Guid.Empty] : devices, typeId);
            }

        }
    }

    // ---------- rule evaluation ----------
    private bool IsEvaluable(Runtime runtime) => runtime.Types.All(_ready.Contains);

    private void EvaluateAllReady()
    {
        foreach (var runtime in _rules.Values)
        {
            if (runtime.Stateful && IsEvaluable(runtime))
            {
                Reconcile(runtime, AllCandidates(runtime), runtime.TriggerTypeId);
            }
        }
    }

    /// <summary>Every subject the rule may be on for: the trigger's on devices plus the ones it is on for now.</summary>
    private HashSet<Guid> AllCandidates(Runtime runtime)
    {
        var candidates = new HashSet<Guid>(runtime.Active.Keys);
        candidates.UnionWith(runtime.Suppressed);
        if (_on.TryGetValue(runtime.TriggerTypeId, out var set))
        {
            candidates.UnionWith(set.Keys);
        }

        return candidates;
    }

    /// <summary>Whether a stateful rule holds for a subject now; <paramref name="shared"/> = <see cref="SharedHold"/>.</summary>
    private bool Holds(Runtime runtime, Guid subject, bool shared)
    {
        if (runtime.TriggerScope == EventScope.Device && (subject == Guid.Empty || !Selects(runtime.Trigger, subject)))
        {
            return false;
        }

        return shared && IsOn(runtime.TriggerTypeId, subject, runtime.Trigger.Condition.Data) && SubjectHold(runtime, subject);
    }

    /// <summary>
    /// The conditions after the trigger that do not depend on the subject: schedules, server events and "on for any of
    /// these devices". Evaluated once per reconcile, not per subject.
    /// </summary>
    private bool SharedHold(Runtime runtime)
    {
        var local = _time.GetLocalNow().DateTime;
        foreach (var condition in runtime.Others)
        {
            if (condition.Condition.Kind == RuleConditionKind.Schedule)
            {
                if (condition.Condition.Schedule is null || !RuleTimes.IsInside(condition.Condition.Schedule, local))
                {
                    return false;
                }

                continue;
            }

            if (condition.PerSubject)
            {
                continue;
            }

            var typeId = condition.TypeId;
            if (condition.Type is { DeviceScope: false })
            {
                if (!IsOn(typeId, Guid.Empty, condition.Condition.Data))
                {
                    return false;
                }
            }
            else if (!_on.TryGetValue(typeId, out var set) || !set.Any(e => Selects(condition, e.Key) && Matches(e.Value, condition.Condition.Data)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The conditions after the trigger on "the same device".</summary>
    private bool SubjectHold(Runtime runtime, Guid subject)
    {
        foreach (var condition in runtime.Others)
        {
            if (condition.PerSubject && !IsOn(condition.TypeId, subject, condition.Condition.Data))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a selector picks a managed device (a late state of a removed device never counts).</summary>
    private bool Selects(ConditionRuntime selector, Guid device) => selector.Condition.Devices.Mode switch
    {
        DeviceSelectorMode.All => _devices.ContainsKey(device),
        DeviceSelectorMode.Devices or DeviceSelectorMode.Tags =>
            _devices.TryGetValue(device, out var info)
            && (selector.DeviceIds.Contains(device) || (selector.Tags.Count > 0 && selector.Tags.Overlaps(info.Tags))),
        _ => false,
    };

    /// <summary>Re-evaluates a stateful rule for <paramref name="subjects"/> and runs the edges.</summary>
    private void Reconcile(Runtime runtime, IEnumerable<Guid> subjects, string causeTypeId)
    {
        bool? shared = null;
        foreach (var subject in subjects.ToList())
        {
            shared ??= SharedHold(runtime);
            var holds = Holds(runtime, subject, shared.Value);
            var isOn = runtime.Active.ContainsKey(subject) || runtime.Suppressed.Contains(subject);
            if (holds && !isOn)
            {
                TurnOn(runtime, subject, causeTypeId);
            }
            else if (!holds && isOn)
            {
                TurnOff(runtime, subject, causeTypeId);
            }
        }
    }

    private void TurnOn(Runtime runtime, Guid subject, string causeTypeId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var suppressed = Limit(runtime, subject, now);
        LogRule(runtime, subject, "Rule turned on", suppressed ?? EventName(causeTypeId), suppressed is null ? EventLogResult.Info : EventLogResult.Skipped);
        if (suppressed is not null)
        {
            runtime.Suppressed.Add(subject);
            return;
        }

        var aggregate = runtime.Active.Count == 0;
        runtime.Active[subject] = now;
        runtime.LastOnEdge[subject] = now;
        runtime.LastFiredUtc = now;
        _firedDirty[runtime.Id] = now;
        _stateOps[(runtime.Id, subject)] = (true, now);
        Dispatch(runtime, ActionEdge.TurnedOn, subject, aggregate, causeTypeId, true);
    }

    private void TurnOff(Runtime runtime, Guid subject, string causeTypeId)
    {
        LogRule(runtime, subject, "Rule turned off", EventName(causeTypeId), EventLogResult.Info);
        if (runtime.Suppressed.Remove(subject))
        {
            return; // its "turns on" ran no actions
        }

        runtime.Active.Remove(subject);
        _stateOps[(runtime.Id, subject)] = (false, default);
        Dispatch(runtime, ActionEdge.TurnedOff, subject, runtime.Active.Count == 0, causeTypeId, false);
    }

    private void FireEvent(CatalogEventType type, Guid device, IReadOnlyDictionary<string, string>? data)
    {
        Publish(type.Id, device, PublishedEventKind.Fired, data);
        var logged = false;
        foreach (var runtime in _rules.Values)
        {
            if (runtime.Stateful || !string.Equals(runtime.TriggerTypeId, type.Id, StringComparison.OrdinalIgnoreCase) || !IsEvaluable(runtime))
            {
                continue;
            }

            if (!logged)
            {
                LogEvent(type, device, null);
                logged = true;
            }

            if ((type.DeviceScope && !Selects(runtime.Trigger, device)) || !Matches(data ?? EmptyData, runtime.Trigger.Condition.Data)
                || !SharedHold(runtime) || !SubjectHold(runtime, device))
            {
                continue;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var suppressed = Limit(runtime, device, now);
            LogRule(runtime, device, "Rule fired", suppressed ?? type.Info.DisplayName, suppressed is null ? EventLogResult.Info : EventLogResult.Skipped);
            if (suppressed is not null)
            {
                continue;
            }

            runtime.LastOnEdge[device] = now;
            runtime.LastFiredUtc = now;
            _firedDirty[runtime.Id] = now;
            Dispatch(runtime, ActionEdge.Fired, device, aggregate: true, type.Id, null, data);
        }
    }

    /// <summary>Null when the run may go ahead, else why it was suppressed (cooldown, runs per minute).</summary>
    private string? Limit(Runtime runtime, Guid subject, DateTime now)
    {
        var cooldown = TimeSpan.FromSeconds(runtime.Definition.CooldownSeconds);
        if (cooldown > TimeSpan.Zero && runtime.LastOnEdge.TryGetValue(subject, out var last) && now - last < cooldown)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Dead time ({runtime.Definition.CooldownSeconds} s after the last run)");
        }

        while (runtime.Runs.Count > 0 && now - runtime.Runs.Peek() >= TimeSpan.FromMinutes(1))
        {
            runtime.Runs.Dequeue();
        }

        if (runtime.Runs.Count >= runtime.Definition.MaxRunsPerMinute)
        {
            if (runtime.LastPauseLog is not { } logged || now - logged >= TimeSpan.FromMinutes(1))
            {
                runtime.LastPauseLog = now;
                _log.Write(EventLogKind.System, EventLogResult.Skipped, "Rule paused", ruleId: runtime.Id, rule: runtime.Name,
                    message: string.Create(CultureInfo.InvariantCulture, $"Max per minute: at most {runtime.Definition.MaxRunsPerMinute} runs per minute"));
            }

            return "Max per minute";
        }

        runtime.Runs.Enqueue(now);
        return null;
    }

    private void Dispatch(Runtime runtime, ActionEdge edge, Guid subject, bool aggregate, string causeTypeId, bool? state, IReadOnlyDictionary<string, string>? data = null)
    {
        var type = _catalog.FindEventType(causeTypeId);
        _devices.TryGetValue(subject, out var device);
        var eventData = data ?? (_on.TryGetValue(causeTypeId, out var set) && set.TryGetValue(subject, out var current) ? current : EmptyData);
        var trigger = new ActionTrigger(
            causeTypeId,
            type?.Info.DisplayName ?? causeTypeId,
            subject == Guid.Empty ? null : subject,
            device?.Address,
            device?.Model,
            state,
            _time.GetUtcNow(),
            eventData);
        _actions.Dispatch(new RuleEdgeJob(runtime.Definition, runtime.Stateful, edge, subject == Guid.Empty ? null : subject, aggregate, trigger));
    }

    // ---------- rule changes ----------
    private void HandleRuleChanged(RuleChanged change)
    {
        var id = change.Current?.Definition.Id ?? change.Previous?.Definition.Id ?? Guid.Empty;
        if (_rules.Remove(id, out var old))
        {
            // Turning off, changing or deleting a rule that is on runs its off actions first.
            foreach (var subject in old.Active.Keys.ToList())
            {
                old.Active.Remove(subject);
                _stateOps[(old.Id, subject)] = (false, default);
                if (old.Stateful)
                {
                    LogRule(old, subject, "Rule turned off", change.Current is null ? "The rule was deleted" : change.Current.Definition.Enabled ? "The rule was changed" : "The rule was turned off", EventLogResult.Info);
                    Dispatch(old, ActionEdge.TurnedOff, subject, aggregate: old.Active.Count == 0, old.TriggerTypeId, false);
                }
            }
        }

        if (change.Current is { Definition.Enabled: true } current)
        {
            var runtime = new Runtime(current, _catalog);
            _rules[runtime.Id] = runtime;
            WaitForUndeclaredTypes();
            if (runtime.Stateful && IsEvaluable(runtime))
            {
                Reconcile(runtime, AllCandidates(runtime), runtime.TriggerTypeId);
            }
        }

        RefreshWatched();
    }

    // ---------- sources (plugins on / off) ----------
    private void SyncSources(bool initial)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in _catalog.EventTypes.Where(t => t.SourceId != BuiltInEvents.SourceId))
        {
            declared.Add(type.Id);
            var added = !initial && !_knownTypes.Contains(type.Id);
            if (!type.Stateful)
            {
                _ready.Add(type.Id); // one-shot events have no state to report
                if (!type.Available)
                {
                    off.Add(type.SourceId);
                }

                continue;
            }

            if (!type.Available)
            {
                off.Add(type.SourceId);
                if (_ready.Add(type.Id) | (_on.TryGetValue(type.Id, out var set) && set.Count > 0))
                {
                    _waitingSince.Remove(type.Id);
                    var devices = _on.TryGetValue(type.Id, out var current) ? current.Keys.ToList() : [];
                    _on.Remove(type.Id);
                    foreach (var device in devices)
                    {
                        Publish(type.Id, device, PublishedEventKind.Off, null);
                    }

                    if (!initial)
                    {
                        OnTypeChanged(type.Id, devices);
                    }
                }
            }
            else if (initial || added || _offSources.Contains(type.SourceId))
            {
                // Turned on, a new type of a running source, or server start: wait for the source's first full report.
                _ready.Remove(type.Id);
                _waitingSince[type.Id] = now;
            }
        }

        // Types a source no longer declares (e.g. a deleted device trigger): off, and their rules turn off.
        foreach (var gone in _knownTypes.Where(t => !declared.Contains(t)).ToList())
        {
            _waitingSince.Remove(gone);
            _ready.Add(gone);
            if (_on.Remove(gone, out var set) && set.Count > 0 && !initial)
            {
                foreach (var device in set.Keys)
                {
                    Publish(gone, device, PublishedEventKind.Off, null);
                }

                OnTypeChanged(gone, [.. set.Keys]);
            }
        }

        _knownTypes.Clear();
        _knownTypes.UnionWith(declared);
        _offSources.Clear();
        _offSources.UnionWith(off);
        WaitForUndeclaredTypes();
        if (!initial)
        {
            foreach (var runtime in _rules.Values)
            {
                runtime.Refresh(_catalog);
            }

            EvaluateAllReady();
        }
    }

    /// <summary>
    /// Types rules use that no source declares (yet): a source that declares its types once it runs (device triggers,
    /// started after the engine) or one that was removed. They wait like a source after a start, then count as off.
    /// </summary>
    private void WaitForUndeclaredTypes()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var typeId in _rules.Values.SelectMany(r => r.Types))
        {
            if (!_knownTypes.Contains(typeId) && !_ready.Contains(typeId) && !_waitingSince.ContainsKey(typeId)
                && !BuiltInEvents.Types.Any(t => string.Equals(t.Id, typeId, StringComparison.OrdinalIgnoreCase)))
            {
                _waitingSince[typeId] = now;
            }
        }
    }

    // ---------- devices and built-in events ----------
    private void HandleDevice(DeviceChange change)
    {
        if (change.Kind == DeviceChangeKind.Removed || change.Device is null)
        {
            if (!_devices.Remove(change.DeviceId))
            {
                return;
            }

            var types = _on.Where(t => t.Value.Remove(change.DeviceId)).Select(t => t.Key).ToList();
            foreach (var runtime in _rules.Values.Where(r => r.Stateful && IsEvaluable(r)))
            {
                if (runtime.Active.ContainsKey(change.DeviceId) || runtime.Suppressed.Contains(change.DeviceId) || types.Any(runtime.Types.Contains))
                {
                    Reconcile(runtime, AllCandidates(runtime).Append(change.DeviceId), types.FirstOrDefault() ?? runtime.TriggerTypeId);
                }
            }

            return;
        }

        var previous = _devices.GetValueOrDefault(change.DeviceId);
        var current = DeviceRef.From(change.Device);
        _devices[change.DeviceId] = current;
        if (previous is not null && !previous.Tags.SetEquals(current.Tags))
        {
            foreach (var runtime in _rules.Values.Where(r => r.Stateful && r.UsesTags && IsEvaluable(r)))
            {
                Reconcile(runtime, AllCandidates(runtime).Append(change.DeviceId), runtime.TriggerTypeId);
            }
        }

        RecomputeBuiltIn([change.DeviceId]);
    }

    private void RecomputeBuiltIn(IEnumerable<Guid> deviceIds)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var id in deviceIds.ToList())
        {
            if (!_devices.TryGetValue(id, out var device))
            {
                continue;
            }

            SetBuiltIn(BuiltInEvents.Unreachable, id, device.Status == DeviceStatus.Unreachable);
            SetBuiltIn(BuiltInEvents.CredentialsRequired, id, device.Status == DeviceStatus.CredentialsRequired);
            SetBuiltIn(BuiltInEvents.CertificateChanged, id, device.Status == DeviceStatus.CertificateChanged);
            var notAfter = device.CertNotAfterUtc;
            SetBuiltIn(BuiltInEvents.CertificateExpired, id, notAfter is { } end && end <= now);
            SetBuiltIn(BuiltInEvents.CertificateExpiresSoon, id, notAfter is { } soon && soon > now && soon - now <= TimeSpan.FromDays(BuiltInEvents.ExpiresSoonDays));
        }
    }

    private void SetBuiltIn(string typeId, Guid device, bool isOn)
    {
        var type = _catalog.FindEventType(typeId)!;
        var set = OnSet(typeId);
        if (set.ContainsKey(device) != isOn)
        {
            SetState(type, device, isOn, null);
        }
    }

    private void HandleTick()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (typeId, since) in _waitingSince.ToList())
        {
            if (now - since >= _options.SourceReadyTimeout)
            {
                _waitingSince.Remove(typeId);
                _ready.Add(typeId);
                LogSourceTimeout(typeId);
                EvaluateAllReady();
            }
        }

        if (now - _lastCertificateCheck >= _options.CertificateCheckInterval)
        {
            _lastCertificateCheck = now;
            RecomputeBuiltIn(_devices.Keys);
        }

        foreach (var runtime in _rules.Values.Where(r => r.Stateful && r.HasSchedule && IsEvaluable(r)))
        {
            Reconcile(runtime, AllCandidates(runtime), runtime.TriggerTypeId);
        }
    }

    // ---------- persistence and status ----------
    private async Task FlushStatesAsync(CancellationToken ct)
    {
        // Only the loop (or start / stop, when it does not run) touches the pending changes: nothing is added while
        // this awaits. A failed write keeps them for the next flush.
        if (_stateOps.Count > 0)
        {
            await _store.WriteStatesAsync(
                [.. _stateOps.Where(e => e.Value.Add).Select(e => (e.Key.Rule, e.Key.Subject, e.Value.Since))],
                [.. _stateOps.Where(e => !e.Value.Add).Select(e => (e.Key.Rule, e.Key.Subject))],
                ct).ConfigureAwait(false);
            _stateOps.Clear();
        }

        if (_firedDirty.Count > 0)
        {
            await _store.SetLastFiredAsync(new Dictionary<Guid, DateTime>(_firedDirty), ct).ConfigureAwait(false);
            _firedDirty.Clear();
        }
    }

    private void PublishStatus()
    {
        var status = new Dictionary<Guid, RuleRuntimeStatus>();
        foreach (var runtime in _rules.Values)
        {
            var waiting = runtime.Types.Where(t => !_ready.Contains(t)).Select(t => _catalog.FindEventType(t)?.SourceName ?? "its events").Distinct().ToList();
            status[runtime.Id] = new RuleRuntimeStatus(runtime.Id, runtime.Active.Count, waiting.Count == 0 ? null : string.Join(", ", waiting), runtime.LastFiredUtc);
        }

        var changed = status.Count != _status.Count || status.Any(s => !_status.TryGetValue(s.Key, out var old) || old != s.Value);
        _status = status;
        if (changed)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshWatched() =>
        _watched = _rules.Values.SelectMany(r => r.Types).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // ---------- event log ----------
    private void LogEvent(CatalogEventType type, Guid device, bool? isOn)
    {
        if (!_watched.Contains(type.Id))
        {
            return; // events no rule uses are not logged
        }

        _log.Write(
            EventLogKind.Event,
            EventLogResult.Info,
            type.Info.DisplayName,
            device == Guid.Empty ? null : device,
            DeviceText(device),
            message: isOn is null ? "Happened" : isOn.Value ? "On" : "Off");
    }

    private void LogRule(Runtime runtime, Guid subject, string what, string message, EventLogResult result) =>
        _log.Write(EventLogKind.Rule, result, what, subject == Guid.Empty ? null : subject, DeviceText(subject), runtime.Id, runtime.Name, message);

    private string EventName(string typeId) => _catalog.FindEventType(typeId)?.Info.DisplayName ?? typeId;

    private string DeviceText(Guid device) =>
        device == Guid.Empty ? string.Empty
        : _devices.TryGetValue(device, out var info) ? DeviceRef.Text(info.Address, info.Model)
        : DeviceMessages.Removed;

    private static readonly IReadOnlyDictionary<string, string> EmptyData = new Dictionary<string, string>();

    /// <summary>The last write of the rule states at server stop.</summary>
    private static readonly TimeSpan FinalFlushTimeout = TimeSpan.FromSeconds(5);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event rules started with {Count} enabled rules")]
    private partial void LogStarted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event rules: the queue is full, events are dropped")]
    private partial void LogQueueFull();

    [LoggerMessage(Level = LogLevel.Error, Message = "Event rules: {Command} failed")]
    private partial void LogCommandFailed(Exception ex, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event rules: rule states could not be stored")]
    private partial void LogPersistFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event rules: {TypeId} did not report after start; counted as off")]
    private partial void LogSourceTimeout(string typeId);

    // ---------- types ----------
    private abstract record Command;

    private sealed record StateCommand(string SourceId, string TypeId, Guid? DeviceId, bool IsOn, IReadOnlyDictionary<string, string>? Data) : Command;

    private sealed record ReplaceCommand(string SourceId, string TypeId, IReadOnlyCollection<Guid> OnDevices) : Command;

    private sealed record FireCommand(string SourceId, string TypeId, Guid? DeviceId, IReadOnlyDictionary<string, string>? Data) : Command;

    private sealed record DeviceCommand(DeviceChange Change) : Command;

    private sealed record RuleChanged(StoredRule? Previous, StoredRule? Current, TaskCompletionSource Done) : Command;

    private sealed record SourcesChangedCommand : Command;

    private sealed record TickCommand : Command;

    private sealed record SubscribeCommand(EventSubscription Subscription) : Command;

    private sealed record UnsubscribeCommand(EventSubscription Subscription) : Command;

    private sealed record Barrier(TaskCompletionSource Done) : Command;

    private sealed class Runtime
    {
        public Runtime(StoredRule stored, EventCatalog catalog)
        {
            Definition = stored.Definition;
            LastFiredUtc = stored.LastFiredUtc;
            TriggerTypeId = Definition.Conditions.FirstOrDefault()?.EventTypeId ?? string.Empty;
            Trigger = new ConditionRuntime(Definition.Conditions.FirstOrDefault() ?? new RuleCondition());
            Others = [.. Definition.Conditions.Skip(1).Select(c => new ConditionRuntime(c))];
            Refresh(catalog);
            Types = Definition.Conditions.Where(c => c.Kind == RuleConditionKind.Event && c.EventTypeId is not null)
                .Select(c => c.EventTypeId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            HasSchedule = Definition.Conditions.Any(c => c.Kind == RuleConditionKind.Schedule);
            UsesTags = Definition.Conditions.Any(c => c.Devices.IsChoice && c.Devices.Tags.Count > 0);
        }

        public RuleDefinition Definition { get; }

        public Guid Id => Definition.Id;

        public string Name => Definition.Name;

        public string TriggerTypeId { get; }

        /// <summary>The first condition with its device and tag sets.</summary>
        public ConditionRuntime Trigger { get; }

        /// <summary>The conditions after the trigger, with their resolved event types.</summary>
        public IReadOnlyList<ConditionRuntime> Others { get; }

        public bool Stateful { get; private set; }

        public EventScope TriggerScope { get; private set; }

        public HashSet<string> Types { get; }

        public bool HasSchedule { get; }

        public bool UsesTags { get; }

        /// <summary>Subjects the rule is on for whose "turns on" actions ran (persisted).</summary>
        public Dictionary<Guid, DateTime> Active { get; } = [];

        /// <summary>Subjects the rule is on for whose "turns on" was suppressed by a limit.</summary>
        public HashSet<Guid> Suppressed { get; } = [];

        public Dictionary<Guid, DateTime> LastOnEdge { get; } = [];

        public Queue<DateTime> Runs { get; } = new();

        public DateTime? LastPauseLog { get; set; }

        public DateTime? LastFiredUtc { get; set; }

        /// <summary>Kind and scope of the trigger, read again when the event types changed (a type declared late).</summary>
        public void Refresh(EventCatalog catalog)
        {
            foreach (var other in Others)
            {
                other.Type = other.Condition.Kind == RuleConditionKind.Event ? catalog.FindEventType(other.TypeId) : null;
            }

            if (catalog.FindEventType(TriggerTypeId) is { } trigger)
            {
                Stateful = trigger.Stateful;
                TriggerScope = trigger.Info.Scope;
            }
            else if (TriggerTypeId.Length == 0 || Active.Count == 0)
            {
                Stateful = true;
                TriggerScope = EventScope.Device;
            }
        }
    }

    /// <summary>A condition with its chosen devices and tags as sets (a choice can list thousands of devices).</summary>
    private sealed class ConditionRuntime(RuleCondition condition)
    {
        public RuleCondition Condition { get; } = condition;

        public string TypeId { get; } = condition.EventTypeId ?? string.Empty;

        public HashSet<Guid> DeviceIds { get; } = [.. condition.Devices.DeviceIds];

        public HashSet<string> Tags { get; } = new(condition.Devices.Tags, StringComparer.OrdinalIgnoreCase);

        /// <summary>The event type as the catalog knows it (null: not declared now); set by <see cref="Runtime.Refresh"/>.</summary>
        public CatalogEventType? Type { get; set; }

        /// <summary>An event condition on "the same device" as the trigger (a server event never is).</summary>
        public bool PerSubject =>
            Condition.Kind == RuleConditionKind.Event && Condition.Devices.Mode == DeviceSelectorMode.EventDevice && Type is not { DeviceScope: false };
    }

    private sealed class Publisher(RuleEngine engine, string sourceId) : IEventPublisher
    {
        public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null) =>
            engine.Enqueue(new StateCommand(sourceId, typeId, deviceId, isOn, data is null ? null : new Dictionary<string, string>(data)));

        public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null) =>
            engine.Enqueue(new FireCommand(sourceId, typeId, deviceId, data is null ? null : new Dictionary<string, string>(data)));

        public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices) =>
            engine.Enqueue(new ReplaceCommand(sourceId, typeId, [.. onDevices ?? []]));

        public bool IsWatched(string typeId) => engine.IsWatched(typeId);
    }

    private sealed class Subscriptions(RuleEngine engine, string owner) : IEventSubscriptions
    {
        public IReadOnlyList<SubscribedEventType> Types =>
            [.. engine._catalog.EventTypes.Select(t => new SubscribedEventType(t.Info, t.SourceId, t.SourceName, t.Available))];

        public event EventHandler? TypesChanged
        {
            add
            {
                engine._catalog.Registry.EventTypesChanged += value;
                engine._catalog.Registry.EnabledChanged += value;
            }

            remove
            {
                engine._catalog.Registry.EventTypesChanged -= value;
                engine._catalog.Registry.EnabledChanged -= value;
            }
        }

        public IDisposable Subscribe(Action<PublishedEvent> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var subscription = new EventSubscription(owner, handler, s =>
            {
                Interlocked.Decrement(ref engine._subscriptionCount);
                engine.EnqueueControl(new UnsubscribeCommand(s));
            }, engine._time, engine._logger);
            Interlocked.Increment(ref engine._subscriptionCount);
            engine.EnqueueControl(new SubscribeCommand(subscription));
            return subscription;
        }
    }
}

/// <summary>What the engine knows of a managed device.</summary>
public sealed record DeviceRef(string Address, string? Model, string Serial, DeviceStatus Status, DateTime? CertNotAfterUtc, HashSet<string> Tags)
{
    public static DeviceRef From(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceRef(device.Address, device.Model, device.Serial, device.Status, device.CertNotAfterUtc,
            new HashSet<string>(device.Tags, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>"10.0.0.48 (P3265-V)".</summary>
    public static string Text(string address, string? model) => string.IsNullOrEmpty(model) ? address : $"{address} ({model})";
}
