using Oadm.Plugins.DeviceTriggers.Device;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceTriggers.Runtime;

/// <summary>
/// The live side of the device triggers (see the plugin README): one
/// permanent WebSocket per device carrying every turned-on trigger of that device (<see cref="DeviceConnection"/>), the
/// on/off state per trigger and device, and the second event "&lt;Trigger&gt;: connection lost".
/// <list type="bullet">
/// <item>On/off events: a trigger is on for a device while at least one matching source is on (port 3 or port 4 with
/// "Any"). After every (re)connect the device reports the current states; that report replaces what OADM had, so only
/// real changes reach the rules. During a problem the last state is kept.</item>
/// <item>A trigger reports to the rules (<see cref="IEventPublisher.ReplaceStates"/>) once every device of it sent its
/// first report or has a problem; then each change is one <see cref="IEventPublisher.SetState"/>.</item>
/// <item>A problem (connection, login, the event no longer on the device) that lasts
/// <see cref="DeviceTriggersOptions.LostDelay"/> turns "connection lost" on; it turns off with the next report.</item>
/// </list>
/// </summary>
internal sealed class TriggerRuntime : IAsyncDisposable
{
    private readonly ICorePluginContext _ctx;
    private readonly DeviceTriggersOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, TriggerDefinition> _active = [];
    private readonly Dictionary<(Guid Trigger, Guid Device), Slot> _slots = [];
    private readonly HashSet<Guid> _reported = [];
    private readonly Dictionary<Guid, int> _pending = []; // per trigger: slots without a report and without a problem yet
    private readonly HashSet<Guid> _offReported = []; // turned-off triggers whose events were reported as everything off
    private readonly Dictionary<Guid, DeviceConnection> _connections = [];
    private readonly HashSet<Guid> _dirty = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _ticker;
    private DateTimeOffset _lastSweep;
    private int _disposed;

    public TriggerRuntime(ICorePluginContext ctx, DeviceTriggersOptions options, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        _lastSweep = _time.GetUtcNow();
        ConnectGate = new SemaphoreSlim(Math.Max(1, _options.ConnectParallelism));
        _ticker = Task.Run(TickAsync, CancellationToken.None);
    }

    /// <summary>Triggers whose states or definitions changed since the last call (for the page), raised from the ticker.</summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? Changed;

    /// <summary>Devices that are no longer managed: the plugin removes them from its triggers.</summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? DevicesRemoved;

    private IEventPublisher? Publisher => _ctx.EventPublisher;

    /// <summary>Bounds the devices that connect at the same time (event list + stream), shared by every connection.</summary>
    internal SemaphoreSlim ConnectGate { get; }

    /// <summary>Open device connections (tests).</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_sync)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>
    /// Applies the stored triggers: a new or changed trigger starts fresh (its rules see it go off first), a trigger with
    /// only devices added or removed keeps the others, a renamed one keeps everything. Connections follow.
    /// </summary>
    public void Apply(IReadOnlyCollection<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        List<DeviceConnection> stale = [];
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return; // a resolve that read the runtime before the plugin stopped: no connections on a disposed runtime
            }

            var wanted = triggers.Where(t => t.Enabled).ToDictionary(t => t.Id);
            var affected = new HashSet<Guid>(); // devices whose trigger set changed: only their connections set a new filter
            foreach (var (id, old) in _active.ToList())
            {
                if (!wanted.TryGetValue(id, out var now) || !SameEvent(old, now))
                {
                    Stop(old);
                    _active.Remove(id);
                    _offReported.Add(id); // everything off was just reported
                    affected.UnionWith(old.DeviceIds);
                }
            }

            foreach (var (id, trigger) in wanted)
            {
                if (_active.TryGetValue(id, out var old))
                {
                    _active[id] = trigger;
                    if (ReferenceEquals(old.DeviceIds, trigger.DeviceIds) && ReferenceEquals(old.Implicit, trigger.Implicit))
                    {
                        continue; // same definition and devices (a rename keeps everything)
                    }

                    var before = old.DeviceIds.ToHashSet();
                    var after = trigger.DeviceIds.ToHashSet();
                    foreach (var removed in before.Where(d => !after.Contains(d)))
                    {
                        RemoveSlot(trigger, removed);
                        affected.Add(removed);
                    }

                    foreach (var added in after.Where(d => !before.Contains(d)))
                    {
                        _slots[(id, added)] = new Slot();
                        _pending[id] = _pending.GetValueOrDefault(id) + 1;
                        affected.Add(added);
                    }

                    // A device that moved between chosen and tag / all devices is checked differently (missing event).
                    affected.UnionWith(after.Where(d => before.Contains(d) && old.Implicit.Contains(d) != trigger.Implicit.Contains(d)));
                    CheckReady(id);
                }
                else
                {
                    Start(trigger);
                    affected.UnionWith(trigger.DeviceIds);
                }

                _dirty.Add(id);
            }

            ReportDisabled(triggers);
            var devices = _active.Values.SelectMany(t => t.DeviceIds).ToHashSet();
            foreach (var (device, connection) in _connections.ToList())
            {
                if (!devices.Contains(device))
                {
                    _connections.Remove(device);
                    stale.Add(connection);
                }
                else if (affected.Contains(device))
                {
                    connection.Signal();
                }
            }

            foreach (var device in devices.Where(d => !_connections.ContainsKey(d)))
            {
                var connection = new DeviceConnection(device, this, _ctx, _options, _time);
                _connections[device] = connection;
                connection.Start();
            }
        }

        foreach (var connection in stale)
        {
            _ = connection.DisposeAsync().AsTask();
        }
    }

    /// <summary>The turned-on triggers of a device (a snapshot for its connection).</summary>
    public IReadOnlyList<TriggerDefinition> TriggersFor(Guid device)
    {
        lock (_sync)
        {
            return [.. _active.Values.Where(t => _slots.ContainsKey((t.Id, device)))];
        }
    }

    /// <summary>The state of a trigger on each of its devices (page).</summary>
    public IReadOnlyList<TriggerDeviceState> StatesOf(TriggerDefinition trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        lock (_sync)
        {
            return [.. trigger.DeviceIds.Select(device => _active.ContainsKey(trigger.Id) && _slots.TryGetValue((trigger.Id, device), out var slot)
                ? new TriggerDeviceState(device, slot.Connection, slot.Problem, slot.IsOn, slot.LastEventUtc, slot.LastEventText)
                : new TriggerDeviceState(device, TriggerConnection.Off, null, false, null, null))];
        }
    }

    /// <summary>Counts for the trigger list: on, lost, waiting (connecting or a problem not yet lost), the last event.</summary>
    public (int On, int Lost, int Waiting, DateTimeOffset? Last) CountsOf(TriggerDefinition trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        lock (_sync)
        {
            int on = 0, lost = 0, waiting = 0;
            DateTimeOffset? last = null;
            if (!_active.ContainsKey(trigger.Id))
            {
                return (0, 0, 0, null);
            }

            foreach (var device in trigger.DeviceIds)
            {
                if (!_slots.TryGetValue((trigger.Id, device), out var slot))
                {
                    continue;
                }

                on += slot.IsOn ? 1 : 0;
                lost += slot.Connection == TriggerConnection.Lost ? 1 : 0;
                waiting += slot.Connection is TriggerConnection.Connecting or TriggerConnection.Problem ? 1 : 0;
                if (slot.LastEventUtc is { } time && (last is null || time > last))
                {
                    last = time;
                }
            }

            return (on, lost, waiting, last);
        }
    }

    /// <summary>A connection (re)starts: devices without a pending problem show "Connecting".</summary>
    public void Connecting(Guid device)
    {
        lock (_sync)
        {
            foreach (var (trigger, slot) in SlotsOf(device))
            {
                if (slot.ProblemSince is null && slot.Connection != TriggerConnection.Connecting)
                {
                    slot.Connection = TriggerConnection.Connecting;
                    _dirty.Add(trigger);
                }
            }
        }
    }

    /// <summary>The text of a device that came in through a tag or all devices and does not offer the trigger's event.</summary>
    public const string NotOfferedText = "Does not offer this event";

    /// <summary>
    /// A device of a tag or of all devices does not offer the event of <paramref name="triggers"/>: left out quietly (off,
    /// no "connection lost"), shown as <see cref="NotOfferedText"/>; it counts as reported for the event rules.
    /// </summary>
    public void NotOffered(Guid device, IReadOnlyCollection<Guid> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        lock (_sync)
        {
            foreach (var (trigger, slot) in SlotsOf(device))
            {
                if (!triggers.Contains(trigger))
                {
                    continue;
                }

                if (slot.IsOn)
                {
                    slot.IsOn = false;
                    slot.OnData = null;
                    Publisher?.SetState(DeviceTriggersPluginInfo.TypeId(trigger), device, false);
                }

                if (slot.LostOn)
                {
                    slot.LostOn = false;
                    Publisher?.SetState(DeviceTriggersPluginInfo.LostTypeId(trigger), device, false);
                }

                slot.Sources.Clear();
                Settle(trigger, slot);
                slot.Settled = true;
                slot.Healthy = true;
                slot.ProblemSince = null;
                if (slot.Connection != TriggerConnection.Off || slot.Problem != NotOfferedText)
                {
                    slot.Connection = TriggerConnection.Off;
                    slot.Problem = NotOfferedText;
                    _dirty.Add(trigger);
                }

                CheckReady(trigger);
            }
        }
    }

    /// <summary>
    /// A problem with some (or, for null, all) triggers of a device. The last state stays; "connection lost" follows after
    /// <see cref="DeviceTriggersOptions.LostDelay"/>.
    /// </summary>
    public void Problem(Guid device, IReadOnlyCollection<Guid>? triggers, string message)
    {
        var now = _time.GetUtcNow();
        lock (_sync)
        {
            foreach (var (trigger, slot) in SlotsOf(device))
            {
                if (triggers is not null && !triggers.Contains(trigger))
                {
                    continue;
                }

                Settle(trigger, slot);
                slot.Healthy = false;
                slot.ProblemSince ??= now;
                if (slot.Problem != message || slot.Connection is TriggerConnection.Connecting or TriggerConnection.Connected)
                {
                    slot.Problem = message;
                    slot.Connection = slot.LostOn ? TriggerConnection.Lost : TriggerConnection.Problem;
                    _dirty.Add(trigger);
                }

                CheckReady(trigger);
            }
        }
    }

    /// <summary>
    /// The device's report after a filter was set: the current states of <paramref name="triggers"/> (what arrived within
    /// the settle time) replace what OADM had; one-shot events in it fire. The triggers are healthy again.
    /// </summary>
    public void Report(Guid device, IReadOnlyCollection<TriggerDefinition> triggers, IReadOnlyList<StreamNotification> report)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(report);
        lock (_sync)
        {
            foreach (var trigger in triggers)
            {
                if (!_active.TryGetValue(trigger.Id, out var current) || !_slots.TryGetValue((trigger.Id, device), out var slot))
                {
                    continue;
                }

                var sources = new Dictionary<string, (bool On, IReadOnlyDictionary<string, string> Data)>(StringComparer.Ordinal);
                foreach (var notification in report.Where(n => EventStreamJson.Matches(current, n)))
                {
                    if (current.Stateful)
                    {
                        sources[EventStreamJson.SourceKey(notification)] = (EventStreamJson.IsOn(notification.Data.GetValueOrDefault(current.StateItem ?? string.Empty)), Merge(notification));
                    }
                    else
                    {
                        FireLocked(current, device, slot, notification);
                    }
                }

                slot.Healthy = true;
                slot.ProblemSince = null;
                slot.Problem = null;
                slot.Connection = TriggerConnection.Connected;
                if (slot.LostOn)
                {
                    slot.LostOn = false;
                    Publisher?.SetState(DeviceTriggersPluginInfo.LostTypeId(current.Id), device, false);
                }

                if (current.Stateful)
                {
                    slot.Sources.Clear();
                    foreach (var (key, value) in sources)
                    {
                        slot.Sources[key] = value;
                    }

                    var latest = report.LastOrDefault(n => EventStreamJson.Matches(current, n));
                    UpdateLocked(current, device, slot, latest?.Time);
                }

                Settle(current.Id, slot);
                slot.Settled = true;
                _dirty.Add(current.Id);
                CheckReady(current.Id);
            }
        }
    }

    /// <summary>A live event of a device (after its report): updates the matching triggers or fires them.</summary>
    public void Notify(Guid device, StreamNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_sync)
        {
            foreach (var (triggerId, slot) in SlotsOf(device))
            {
                var trigger = _active[triggerId];
                if (!slot.Healthy || !EventStreamJson.Matches(trigger, notification))
                {
                    continue;
                }

                if (trigger.Stateful)
                {
                    slot.Sources[EventStreamJson.SourceKey(notification)] = (EventStreamJson.IsOn(notification.Data.GetValueOrDefault(trigger.StateItem ?? string.Empty)), Merge(notification));
                    UpdateLocked(trigger, device, slot, notification.Time ?? _time.GetUtcNow());
                }
                else
                {
                    FireLocked(trigger, device, slot, notification);
                }

                _dirty.Add(triggerId);
            }
        }
    }

    /// <summary>The device is no longer managed (its connection found no record).</summary>
    public void DeviceGone(Guid device) => DevicesRemoved?.Invoke(this, [device]);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _ticker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        List<DeviceConnection> connections;
        lock (_sync)
        {
            connections = [.. _connections.Values];
            _connections.Clear();
            foreach (var trigger in _active.Values.ToList())
            {
                Stop(trigger);
            }

            _active.Clear();
            _offReported.Clear();
        }

        foreach (var connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
        ConnectGate.Dispose();
    }

    /// <summary>Same event on the devices: name and devices may differ, everything else must match.</summary>
    private static bool SameEvent(TriggerDefinition a, TriggerDefinition b) =>
        a.Topic == b.Topic && a.Stateful == b.Stateful && a.StateItem == b.StateItem
        && a.Filters.Count == b.Filters.Count && a.Filters.All(f => b.Filters.Contains(f));

    private static Dictionary<string, string> Merge(StreamNotification notification)
    {
        var data = new Dictionary<string, string>(notification.Source, StringComparer.Ordinal);
        foreach (var (name, value) in notification.Data)
        {
            data[name] = value;
        }

        return data;
    }

    private static string Describe(IReadOnlyDictionary<string, string> values) =>
        string.Join(", ", values.Select(v => $"{v.Key} {v.Value}"));

    private void Start(TriggerDefinition trigger)
    {
        _active[trigger.Id] = trigger;
        _offReported.Remove(trigger.Id);
        var count = 0;
        foreach (var device in trigger.DeviceIds)
        {
            if (_slots.TryAdd((trigger.Id, device), new Slot()))
            {
                count++;
            }
        }

        _pending[trigger.Id] = count;

        // Nobody is "connection lost" yet; one-shot events have nothing to report.
        Publisher?.ReplaceStates(DeviceTriggersPluginInfo.LostTypeId(trigger.Id), []);
        if (!trigger.Stateful)
        {
            _reported.Add(trigger.Id);
        }

        CheckReady(trigger.Id); // no devices right now (a tag without devices): reported at once as everything off
    }

    /// <summary>
    /// Turned-off triggers keep their event types (their rules stay valid): reported once as everything off, so their rules
    /// are evaluated at once (the trigger is off) instead of waiting for a report that never comes.
    /// </summary>
    private void ReportDisabled(IReadOnlyCollection<TriggerDefinition> triggers)
    {
        var disabled = new HashSet<Guid>();
        foreach (var trigger in triggers.Where(t => !t.Enabled))
        {
            disabled.Add(trigger.Id);
            if (_offReported.Add(trigger.Id))
            {
                if (trigger.Stateful)
                {
                    Publisher?.ReplaceStates(DeviceTriggersPluginInfo.TypeId(trigger.Id), []);
                }

                Publisher?.ReplaceStates(DeviceTriggersPluginInfo.LostTypeId(trigger.Id), []);
            }
        }

        _offReported.IntersectWith(disabled);
    }

    /// <summary>The slot reported or has a problem: it no longer holds back the trigger's first full report.</summary>
    private void Settle(Guid trigger, Slot slot)
    {
        if (!slot.Counted)
        {
            slot.Counted = true;
            _pending[trigger] = _pending.GetValueOrDefault(trigger) - 1;
        }
    }

    /// <summary>Turned off, deleted or changed: everything of the trigger counts as off.</summary>
    private void Stop(TriggerDefinition trigger)
    {
        foreach (var device in trigger.DeviceIds)
        {
            _slots.Remove((trigger.Id, device));
        }

        if (trigger.Stateful)
        {
            Publisher?.ReplaceStates(DeviceTriggersPluginInfo.TypeId(trigger.Id), []);
        }

        Publisher?.ReplaceStates(DeviceTriggersPluginInfo.LostTypeId(trigger.Id), []);
        _reported.Remove(trigger.Id);
        _pending.Remove(trigger.Id);
        _dirty.Add(trigger.Id);
    }

    private void RemoveSlot(TriggerDefinition trigger, Guid device)
    {
        if (!_slots.Remove((trigger.Id, device), out var slot))
        {
            return;
        }

        Settle(trigger.Id, slot);

        if (trigger.Stateful && slot.IsOn && _reported.Contains(trigger.Id))
        {
            Publisher?.SetState(DeviceTriggersPluginInfo.TypeId(trigger.Id), device, false);
        }

        if (slot.LostOn)
        {
            Publisher?.SetState(DeviceTriggersPluginInfo.LostTypeId(trigger.Id), device, false);
        }
    }

    private List<(Guid Trigger, Slot Slot)> SlotsOf(Guid device) =>
        [.. _active.Keys.Select(t => (Trigger: t, Slot: _slots.GetValueOrDefault((t, device)))).Where(e => e.Slot is not null).Select(e => (e.Trigger, e.Slot!))];

    /// <summary>On while at least one matching source is on; publishes a change once the trigger reported.</summary>
    private void UpdateLocked(TriggerDefinition trigger, Guid device, Slot slot, DateTimeOffset? time)
    {
        var on = slot.Sources.Values.Where(s => s.On).Select(s => s.Data).ToList();
        var isOn = on.Count > 0;
        var data = isOn ? on[^1] : null;
        var changed = isOn != slot.IsOn || (isOn && !SameData(slot.OnData, data));
        if (!changed)
        {
            return;
        }

        slot.IsOn = isOn;
        slot.OnData = data;
        if (slot.Settled || time is not null)
        {
            slot.LastEventUtc = time ?? _time.GetUtcNow();
            slot.LastEventText = isOn ? "On · " + Describe(data!) : "Off";
        }

        if (_reported.Contains(trigger.Id))
        {
            Publisher?.SetState(DeviceTriggersPluginInfo.TypeId(trigger.Id), device, isOn, data);
        }
    }

    private void FireLocked(TriggerDefinition trigger, Guid device, Slot slot, StreamNotification notification)
    {
        var data = Merge(notification);
        slot.LastEventUtc = notification.Time ?? _time.GetUtcNow();
        slot.LastEventText = "Happened · " + Describe(data);
        Publisher?.Fire(DeviceTriggersPluginInfo.TypeId(trigger.Id), device, data);
    }

    private static bool SameData(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        a is not null && b is not null && a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var v) && v == e.Value);

    /// <summary>The first full report of a stateful trigger: once every device reported or has a problem.</summary>
    private void CheckReady(Guid triggerId)
    {
        if (_reported.Contains(triggerId) || !_active.TryGetValue(triggerId, out var trigger))
        {
            return;
        }

        if (_pending.GetValueOrDefault(triggerId) > 0)
        {
            return; // O(1) per call; the slots are walked once, when the trigger reports
        }

        _reported.Add(triggerId);
        var slots = trigger.DeviceIds.Select(d => (Device: d, Slot: _slots.GetValueOrDefault((triggerId, d)))).Where(e => e.Slot is not null).ToList();

        // The values first (the type is not ready yet, so nothing runs), then the full report: the rules that turn on
        // now have the event's values for their actions.
        foreach (var (device, slot) in slots.Where(e => e.Slot!.IsOn))
        {
            Publisher?.SetState(DeviceTriggersPluginInfo.TypeId(triggerId), device, true, slot!.OnData);
        }

        Publisher?.ReplaceStates(DeviceTriggersPluginInfo.TypeId(triggerId), [.. slots.Where(e => e.Slot!.IsOn).Select(e => e.Device)]);
    }

    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(_options.TickInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Tick();
                if (_time.GetUtcNow() - _lastSweep >= _options.DeviceSweepInterval)
                {
                    _lastSweep = _time.GetUtcNow();
                    await SweepAsync(_stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Problem timers ("connection lost") and the page notification; tests call it with a fake clock.</summary>
    internal void Tick()
    {
        var now = _time.GetUtcNow();
        List<Guid> dirty;
        lock (_sync)
        {
            foreach (var ((triggerId, device), slot) in _slots)
            {
                if (slot.ProblemSince is { } since && !slot.LostOn && now - since >= _options.LostDelay)
                {
                    slot.LostOn = true;
                    slot.Connection = TriggerConnection.Lost;
                    Publisher?.SetState(DeviceTriggersPluginInfo.LostTypeId(triggerId), device, true, new Dictionary<string, string> { ["reason"] = slot.Problem ?? string.Empty });
                    _dirty.Add(triggerId);
                }
            }

            dirty = [.. _dirty];
            _dirty.Clear();
        }

        if (dirty.Count > 0)
        {
            Changed?.Invoke(this, dirty);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        HashSet<Guid> used;
        lock (_sync)
        {
            used = [.. _active.Values.SelectMany(t => t.DeviceIds)];
        }

        if (used.Count == 0)
        {
            return;
        }

        try
        {
            var known = (await _ctx.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
            var gone = used.Where(d => !known.Contains(d)).ToHashSet(); // looked up once per stored device
            if (gone.Count > 0)
            {
                DevicesRemoved?.Invoke(this, gone);
            }
        }
#pragma warning disable CA1031 // The next sweep tries again.
        catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>One trigger on one device.</summary>
    private sealed class Slot
    {
        public Dictionary<string, (bool On, IReadOnlyDictionary<string, string> Data)> Sources { get; } = new(StringComparer.Ordinal);

        public bool IsOn { get; set; }

        public IReadOnlyDictionary<string, string>? OnData { get; set; }

        /// <summary>The device sent its report at least once since the trigger started.</summary>
        public bool Settled { get; set; }

        public bool Healthy { get; set; }

        public TriggerConnection Connection { get; set; } = TriggerConnection.Connecting;

        public string? Problem { get; set; }

        public DateTimeOffset? ProblemSince { get; set; }

        public bool LostOn { get; set; }

        /// <summary>Taken off the trigger's pending count (reported or a problem, see <c>Settle</c>).</summary>
        public bool Counted { get; set; }

        public DateTimeOffset? LastEventUtc { get; set; }

        public string? LastEventText { get; set; }
    }

}
