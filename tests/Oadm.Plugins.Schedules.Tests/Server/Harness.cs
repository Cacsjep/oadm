using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.Schedules.Engine;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>The managed devices of a test: O(1) by id, statuses changeable.</summary>
internal sealed class DeviceTable : IDeviceRepository
{
    private readonly ConcurrentDictionary<Guid, IDeviceInfo> _byId = new();
    private readonly List<Guid> _order = [];

    public FakeDevice Add(FakeDevice device)
    {
        _byId[device.Id] = device;
        lock (_order)
        {
            _order.Add(device.Id);
        }

        return device;
    }

    public void Set(FakeDevice device) => _byId[device.Id] = device;

    public void Remove(Guid id) => _byId.TryRemove(id, out _);

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
    {
        lock (_order)
        {
            return Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _order.Select(id => _byId.GetValueOrDefault(id)).OfType<IDeviceInfo>()]);
        }
    }

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(_byId.GetValueOrDefault(id));
}

/// <summary>A user's call with the permissions a test gives (null: every permission).</summary>
internal sealed class TestCall(ICallContext inner, IReadOnlySet<string>? permissions = null) : ICallContext
{
    public string UserName => inner.UserName;

    public string SessionId => string.Empty;

    public string DeviceScopeKey => inner.DeviceScopeKey;

    public bool Has(string permission) => permissions is null || permissions.Contains(permission);

    public bool HasAllDevices => inner.HasAllDevices;

    public IDeviceRepository Devices => inner.Devices;

    public IVapixClientFactory Vapix => inner.Vapix;

    public ITaskRunner Tasks => inner.Tasks;

    public IDeviceEventStreams? EventStreams => inner.EventStreams;

    public bool Contains(IDeviceInfo device) => inner.Contains(device);
}

/// <summary>
/// The task engine as the schedules see it: the device tasks "Run &lt;schedule&gt;" run the engine's own
/// <see cref="ScheduleEngine.ExecuteAsync"/>, at most <see cref="Limit"/> at once (the server setting), the others wait as queued
/// (Cancel ends them before they start). The actions' tasks end as <see cref="Decide"/> says, which may hold them (a gate).
/// </summary>
internal sealed class GateRunner(DeviceTable devices) : ITaskRunner
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Item> _items = [];
    private readonly Queue<Item> _queue = new();
    private int _running;

    public ScheduleEngine? Engine { get; set; }

    /// <summary>Device tasks that run at once at most.</summary>
    public int Limit { get; set; } = 1_000;

    /// <summary>The most device tasks that ran at once.</summary>
    public int MaxRunning { get; private set; }

    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The end of an action's task (plugin id, device): Done by default.</summary>
    public Func<string, Guid, Task<(TaskEndState State, string? Message)>> Decide { get; set; } = (_, _) => Task.FromResult<(TaskEndState, string?)>((TaskEndState.Done, null));

    /// <summary>Every run: plugin, devices, payload, audit.</summary>
    public ConcurrentQueue<(string PluginId, IReadOnlyList<Guid> DeviceIds, string? Payload, TaskAudit? Audit)> Runs { get; } = new();

    /// <summary>Every action task started (plugin, device).</summary>
    public ConcurrentQueue<(string PluginId, Guid DeviceId)> Actions { get; } = new();

    public int ScheduleTasks => Runs.Where(r => r.PluginId == SchedulesInfo.TaskPluginId).Sum(r => r.DeviceIds.Count);

    public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct) =>
        RunCoreAsync(pluginId, deviceIds, payloadJson, null);

    public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, TaskAudit audit, CancellationToken ct) =>
        RunCoreAsync(pluginId, deviceIds, payloadJson, audit);

    private async Task<IReadOnlyList<Guid>> RunCoreAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, TaskAudit? audit)
    {
        Runs.Enqueue((pluginId, deviceIds, payloadJson, audit));
        var ids = new List<Guid>();
        if (pluginId != SchedulesInfo.TaskPluginId)
        {
            foreach (var deviceId in deviceIds)
            {
                Actions.Enqueue((pluginId, deviceId));
                var (state, message) = await Decide(pluginId, deviceId).ConfigureAwait(false);
                var item = new Item(Guid.NewGuid(), deviceId, payloadJson) { Outcome = new TaskOutcome(deviceId, state, message) };
                lock (_gate)
                {
                    _items[item.Id] = item;
                }

                ids.Add(item.Id);
            }

            return ids;
        }

        lock (_gate)
        {
            foreach (var deviceId in deviceIds)
            {
                var item = new Item(Guid.NewGuid(), deviceId, payloadJson);
                _items[item.Id] = item;
                _queue.Enqueue(item);
                ids.Add(item.Id);
            }
        }

        Pump();
        return ids;
    }

    private void Pump()
    {
        while (true)
        {
            Item item;
            lock (_gate)
            {
                if (_running >= Limit || _queue.Count == 0)
                {
                    return;
                }

                item = _queue.Dequeue();
                if (item.Outcome is not null)
                {
                    continue;
                }

                _running++;
                MaxRunning = Math.Max(MaxRunning, _running);
                item.Started = true;
            }

            _ = Task.Run(() => ExecuteAsync(item));
        }
    }

    private async Task ExecuteAsync(Item item)
    {
        var ctx = new FakeTaskContext(new FakeCamera());
        TaskOutcome outcome;
        try
        {
            var device = await devices.FindAsync(item.DeviceId, CancellationToken.None) ?? throw new InvalidOperationException(DeviceMessages.Removed);
            await Engine!.ExecuteAsync(new IdTaskContext(item.Id, ctx), device, item.Payload, item.Cancel.Token);
            outcome = new TaskOutcome(item.DeviceId, ctx.Warnings.Count > 0 ? TaskEndState.DoneWithWarnings : TaskEndState.Done, ctx.Warnings.FirstOrDefault());
        }
        catch (OperationCanceledException)
        {
            outcome = new TaskOutcome(item.DeviceId, TaskEndState.Cancelled, null);
        }
        catch (Exception ex)
        {
            outcome = new TaskOutcome(item.DeviceId, TaskEndState.Failed, ex.Message);
        }

        lock (_gate)
        {
            item.Outcome = outcome;
            _running--;
        }

        Pump();
    }

    public bool Cancel(Guid taskId)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(taskId, out var item) || item.Outcome is not null)
            {
                return false;
            }

            if (!item.Started)
            {
                item.Outcome = new TaskOutcome(item.DeviceId, TaskEndState.Cancelled, null);
                return true;
            }

            item.Cancel.Cancel();
            return true;
        }
    }

    /// <summary>IsActive throws (a broken host), to break a run in the middle.</summary>
    public bool BreakIsActive { get; set; }

    public bool? IsActive(Guid taskId)
    {
        if (BreakIsActive)
        {
            throw new InvalidOperationException("The task list broke.");
        }

        lock (_gate)
        {
            return _items.TryGetValue(taskId, out var item) && item.Outcome is null;
        }
    }

    public Task<TaskOutcome?> GetOutcomeAsync(Guid taskId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(taskId, out var item) ? item.Outcome : null);
        }
    }

    private sealed class Item(Guid id, Guid deviceId, string? payload)
    {
        public Guid Id { get; } = id;

        public Guid DeviceId { get; } = deviceId;

        public string? Payload { get; } = payload;

        public bool Started { get; set; }

        public TaskOutcome? Outcome { get; set; }

        public CancellationTokenSource Cancel { get; } = new();
    }
}

/// <summary>
/// The engine with its fakes: a fake clock in UTC at Monday 2026-11-23 10:00, the devices, the gate runner, the owner "anna" with
/// every right and every device (change <see cref="Owner"/>), the event rules.
/// </summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    public EngineHarness(SchedulesOptions? options = null, bool withUserCalls = true, TimeZoneInfo? zone = null)
    {
        Time.SetLocalTimeZone(zone ?? TimeZoneInfo.Utc);
        Runner = new GateRunner(Devices);
        OwnerTasks = new GateRunner(Devices);
        Users.Set("anna", new TestCall(FakeCallContext.Over(Devices, tasks: OwnerTasks, userName: "anna")));
        var settings = new FakeSettings();
        Store = new ScheduleStore(settings, Records);
        Vault = new NodeVault(settings, null, null, null, Time);
        _options = options ?? new SchedulesOptions();
        _withUserCalls = withUserCalls;
        Engine = NewEngine();
    }

    private readonly SchedulesOptions _options;
    private readonly bool _withUserCalls;

    /// <summary>The device tasks left running by an engine that stopped (kept by the plugin across engines).</summary>
    public LeftTasks Left { get; } = new();

    /// <summary>The current engine (<see cref="NewEngine"/> replaces it, like the plugin turned off and on).</summary>
    public ScheduleEngine Engine { get; private set; }

    /// <summary>A new engine over the same store, devices, runner and left tasks; the runners run its device tasks from now on.</summary>
    public ScheduleEngine NewEngine()
    {
        Engine = new ScheduleEngine(
            new EngineDeps(Runner, Devices, new NoVapix(), _withUserCalls ? Users : null, Store, Vault, Rules, Left),
            ActionCatalog.Default,
            _options,
            Time);
        Runner.Engine = Engine;
        OwnerTasks.Engine = Engine;
        return Engine;
    }

    public FakeTimeProvider Time { get; } = new(Start);

    public DeviceTable Devices { get; } = new();

    public GateRunner Runner { get; }

    public GateRunner OwnerTasks { get; }

    public FakeUserCalls Users { get; } = new();

    public TestRecords Records { get; } = new();

    public ScheduleStore Store { get; }

    public NodeVault Vault { get; }

    public FakeEventPublisher Rules { get; } = new();

    public IReadOnlyList<FakeDevice> AddDevices(int count, DeviceStatus status = DeviceStatus.Ok, string? tag = null) =>
        [.. Enumerable.Range(0, count).Select(i => Devices.Add(new FakeDevice
        {
            Address = $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}",
            Serial = $"ACCC8E{i:X6}",
            Status = status,
            Tags = tag is null ? [] : [tag],
        }))];

    public void Owner(ICallContext? call)
    {
        if (call is null)
        {
            Users.Remove("anna");
        }
        else
        {
            Users.Set("anna", call);
        }
    }

    /// <summary>A stored schedule of anna (saved at <see cref="Start"/>), turned on, restart only by default.</summary>
    public async Task<StoredSchedule> AddAsync(Func<Schedule, Schedule>? change = null, string scope = ICallContext.AllDevicesScopeKey)
    {
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Nightly",
            Owner = "anna",
            Saved = Start,
            Devices = new ScheduleDevices { Kind = ScheduleDevicesKind.All },
            When = new ScheduleWhen { Recurrence = Recurrence.Daily, Time = new TimeOnly(23, 0) },
            Actions = [Action("a1", NodeKind.Restart)],
        };
        schedule = change?.Invoke(schedule) ?? schedule;
        var stored = new StoredSchedule { Schedule = schedule, OwnerScopeKey = scope, Handled = Start };
        await Store.SaveAsync(stored, CancellationToken.None);
        return stored;
    }

    public static FlowNode Action(string id, NodeKind kind, object? settings = null, bool continueOnError = true) =>
        new() { Id = id, Kind = kind, ContinueOnError = continueOnError, Settings = settings is null ? null : NodeSettings.Write(settings) };

    /// <summary>The ended runs of a schedule (newest first).</summary>
    public IReadOnlyList<ScheduleRun> Runs(Guid scheduleId) => [.. Store.RunsOf(scheduleId).Select(r => r.Run)];

    /// <summary>Waits until the schedule's newest run has ended and answers it.</summary>
    public async Task<ScheduleRun> EndedAsync(Guid scheduleId, string what = "the run ended")
    {
        await Eventually.TrueAsync(() => Store.RunsOf(scheduleId) is { Count: > 0 } runs && runs[0].Run.Result != RunResult.Running && !Engine.IsRunning(scheduleId), what);
        return Store.RunsOf(scheduleId)[0].Run;
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        Store.Dispose();
        Vault.Dispose();
    }

    private sealed class NoVapix : IVapixClientFactory
    {
        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(new FakeCamera());
    }
}

/// <summary>A task context with the runner's task id (the engine matches a device task's report by it).</summary>
internal sealed class IdTaskContext(Guid taskId, FakeTaskContext inner) : ITaskExecutionContext
{
    public Guid TaskId => taskId;

    public IVapixClient Vapix => inner.Vapix;

    public Microsoft.Extensions.Logging.ILogger Logger => inner.Logger;

    public ICorePlugin? Owner => inner.Owner;

    public IUploadedFiles Files => inner.Files;

    public void ReportProgress(int percent, string? message = null) => inner.ReportProgress(percent, message);

    public void PlanSteps(params string[] names) => inner.PlanSteps(names);

    public ITaskStep BeginStep(string name) => inner.BeginStep(name);

    public void ReportWarning(string message) => inner.ReportWarning(message);

    public void Log(TaskLogLevel level, string message) => inner.Log(level, message);
}

/// <summary>The plugin's records in memory; <see cref="FailOnce"/> makes the next write of a key throw (a full disk).</summary>
internal sealed class TestRecords : IPluginRecords
{
    private readonly MemoryPluginRecords _inner = new();

    /// <summary>The next write of this key throws once.</summary>
    public string? FailOnce { get; set; }

    public Task<IReadOnlyDictionary<string, string>> ListAsync(string collection, CancellationToken ct) => _inner.ListAsync(collection, ct);

    public Task<string?> GetAsync(string collection, string key, CancellationToken ct) => _inner.GetAsync(collection, key, ct);

    public Task SetManyAsync(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct)
    {
        if (FailOnce is { } key && records.Any(r => r.Key == key))
        {
            FailOnce = null;
            throw new InvalidOperationException("The disk is full.");
        }

        return _inner.SetManyAsync(collection, records, ct);
    }

    public Task ClearAsync(string collection, CancellationToken ct) => _inner.ClearAsync(collection, ct);
}
