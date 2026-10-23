using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Events;

/// <summary>
/// The event rules on a real database: a fake event source plugin (<see cref="Source"/>), recording actions, the rule
/// engine, the action runner and the event log. Time is a <see cref="FakeTimeProvider"/>.
/// </summary>
internal sealed class EventRulesRig : IAsyncDisposable
{
    public const string SourceId = "test.source";
    public const string Blur = "test.blur";
    public const string Input = "test.input";
    public const string Pulse = "test.pulse";
    public const string ServerFlag = "test.server-flag";

    private TestDatabase _db = null!;
    private bool _stopped;
    private Oadm.Core.Sites.ISiteStates? _sites;

    /// <summary>The site events (only when the rig was started with sites).</summary>
    public SiteEventSource? SiteEvents { get; private set; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero)); // a Monday

    public PluginRegistry Registry { get; } = new();

    public FakeSource Source { get; } = new();

    public RecordingAction OnDevice { get; } = new("test.on-device", needsDevice: true, supportsTurnOff: true);

    public RecordingAction NoDevice { get; } = new("test.no-device", needsDevice: false, supportsTurnOff: true);

    public EventCatalog Catalog { get; private set; } = null!;

    public RuleStore Store { get; private set; } = null!;

    public EventLog Log { get; private set; } = null!;

    public ActionRunner Runner { get; private set; } = null!;

    public RuleEngine Engine { get; private set; } = null!;

    public EventRulesService Rules { get; private set; } = null!;

    /// <summary>The view of a user with all devices (the default caller of the rig).</summary>
    public Oadm.Core.DeviceGroups.DeviceView AllView { get; private set; } = null!;

    public Oadm.Core.DeviceGroups.DeviceViewFactory Views => _db.Get<Oadm.Core.DeviceGroups.DeviceViewFactory>();

    public Oadm.Core.DeviceGroups.DeviceGroupStore Groups => _db.Get<Oadm.Core.DeviceGroups.DeviceGroupStore>();

    public DeviceTagStore Tags => _db.Get<DeviceTagStore>();

    public Oadm.Core.Settings.ActionDestinations Destinations => _db.Get<Oadm.Core.Settings.ActionDestinations>();

    public RuleOwners Owners { get; private set; } = null!;

    public DeviceRepository Devices => _db.Get<DeviceRepository>();

    public IDbContextFactory<OadmDbContext> DbFactory => _db.Get<IDbContextFactory<OadmDbContext>>();

    /// <summary>Set before start: actions wait for <see cref="ActionRunner.PluginsStarted"/> like on a server start.</summary>
    public bool HoldActions { get; set; }

    /// <summary>Set before start: the engine queue size.</summary>
    public int QueueCapacity { get; set; } = 100_000;

    public IEventPublisher Publisher => Engine.PublisherFor(SourceId);

    public PluginOrigin SourceOrigin { get; } = new("test.source-package", "1.0", null) { DisplayName = "Test source" };

    public PluginOrigin ActionOrigin { get; } = new("test.actions", "1.0", null) { DisplayName = "Test actions" };

    public static async Task<EventRulesRig> StartAsync(string? dataDirectory = null, Action<EventRulesRig>? beforeStart = null, Oadm.Core.Sites.ISiteStates? sites = null)
    {
        var rig = new EventRulesRig { _sites = sites };
        rig._db = await TestDatabase.CreateAsync(dataDirectory, s =>
        {
            if (sites is not null)
            {
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(s, sites);
            }
        });
        rig.Registry.RegisterCorePlugin(rig.Source, rig.SourceOrigin);
        rig.Registry.RegisterActionPlugin(rig.OnDevice, rig.ActionOrigin);
        rig.Registry.RegisterActionPlugin(rig.NoDevice, rig.ActionOrigin);
        beforeStart?.Invoke(rig);
        await rig.StartEngineAsync();
        return rig;
    }

    public string DataDirectory => _db.Paths.DataDirectory;

    /// <summary>What the runner wrote to the server log (level and text).</summary>
    public CapturingLoggerFactory ServerLog { get; } = new();

    public async Task StartEngineAsync()
    {
        var dbFactory = _db.Get<IDbContextFactory<OadmDbContext>>();
        if (_sites is not null)
        {
            SiteEvents = new SiteEventSource(dbFactory, _sites, () => Engine.PublisherFor(SiteEventSource.EventSourceId));
            await SiteEvents.LoadAsync(CancellationToken.None);
        }

        Catalog = new EventCatalog(Registry, SiteEvents is null ? null : [SiteEvents]);
        Store = new RuleStore(dbFactory, new PluginSecretProtector(_db.Get<CredentialProtector>()), Time);
        Log = new EventLog(dbFactory, Time);
        Owners = new RuleOwners(_db.Get<Oadm.Core.DeviceGroups.OwnerScopes>(), Destinations);
        Runner = new ActionRunner(Catalog, Store, Log, Devices, new FakeVapixFactory(), _db.Get<IPluginSettingsProvider>(), ServerLog, owners: Owners);
        Engine = new RuleEngine(Catalog, Store, Log, Runner, Devices, _db.Get<IDeviceChangeFeed>(), Time,
            new RuleEngineOptions { TickInterval = TimeSpan.FromHours(10), QueueCapacity = QueueCapacity }, owners: Owners);
        Rules = new EventRulesService(Catalog, Store, Engine, Devices, Views, Owners, Tags);
        AllView = await Views.ForScopeAsync(Oadm.Core.Auth.DeviceScope.All, "admin", CancellationToken.None);
        await Engine.StartAsync(CancellationToken.None);
        if (SiteEvents is { } siteEvents)
        {
            siteEvents.EventTypesChanged += (_, _) => Engine.SourcesChanged();
            siteEvents.Start();
        }

        if (!HoldActions)
        {
            Runner.PluginsStarted();
        }
    }

    /// <summary>Stops the engine and the runner (keeps the database), like a server stop.</summary>
    public async Task StopEngineAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (SiteEvents is not null)
        {
            await SiteEvents.DisposeAsync();
        }

        await Engine.DisposeAsync();
        await Runner.DisposeAsync();
        await Log.DisposeAsync();
        Store.Dispose();
    }

    public async Task<Device> AddDeviceAsync(string address, params string[] tags)
    {
        var device = new Device
        {
            Serial = "ACCC8E" + Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture),
            Address = address,
            Model = "P3265-V",
            Status = DeviceStatus.Ok,
            Category = DeviceCategory.Camera,
            Tags = [.. tags],
        };
        return await Devices.AddAsync(device, CancellationToken.None);
    }

    /// <summary>Waits until the engine processed everything and every dispatched action finished.</summary>
    public async Task SettleAsync()
    {
        await Engine.DrainAsync(CancellationToken.None);
        await Runner.WaitIdleAsync(CancellationToken.None);
        await Engine.DrainAsync(CancellationToken.None);
        await Runner.WaitIdleAsync(CancellationToken.None);
    }

    public async Task<RuleDefinition> SaveAsync(RuleDefinition rule)
    {
        var (saved, _, _) = await Rules.SaveAsync(rule, AllView, null, true, CancellationToken.None);
        await SettleAsync();
        return saved;
    }

    public static RuleDefinition Rule(string triggerType, params RuleAction[] actions) => new()
    {
        Name = "Rule " + triggerType,
        Enabled = true,
        CooldownSeconds = 0,
        Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = triggerType, Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
        Actions = [.. actions],
    };

    public RuleAction OnEventDevice() => new() { ActionId = OnDevice.Id, Targets = new DeviceSelector { Mode = DeviceSelectorMode.EventDevice } };

    public RuleAction OnDevices(params Guid[] ids) => new() { ActionId = OnDevice.Id, Targets = new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [.. ids] } };

    public RuleAction WithoutDevice(JsonObject? settings = null) => new() { ActionId = NoDevice.Id, Settings = settings ?? [] };

    public async Task<IReadOnlyList<EventLogLine>> LogLinesAsync()
    {
        await Log.FlushAsync(CancellationToken.None);
        var (lines, _) = await Log.ListAsync(0, 0, null, CancellationToken.None);
        return [.. lines.Reverse()];
    }

    public async ValueTask DisposeAsync()
    {
        await StopEngineAsync();
        await _db.DisposeAsync();
    }

    public async ValueTask CloseKeepDataAsync()
    {
        await StopEngineAsync();
        await _db.CloseAsync();
    }
}

/// <summary>A core plugin that only declares events: a stateful device event, a second one, a pulse and a server flag.</summary>
public sealed class FakeSource : ICorePlugin, IEventSource
{
    public string Id => EventRulesRig.SourceId;

    public string DisplayName => "Test source";

    public string? IconKey => null;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public IReadOnlyList<EventTypeInfo> EventTypes { get; } =
    [
        new(EventRulesRig.Blur, "Blur detected", "Test", EventKind.Stateful),
        new(EventRulesRig.Input, "Input active", "Test", EventKind.Stateful, Fields: [new EventField("port", "Port")]),
        new(EventRulesRig.Pulse, "Button pressed", "Test", EventKind.Stateless),
        new(EventRulesRig.ServerFlag, "Server flag", "Test", EventKind.Stateful, EventScope.Server),
    ];

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>An action that records every run; <see cref="FailWith"/> makes it throw.</summary>
public sealed class RecordingAction(string id, bool needsDevice, bool supportsTurnOff) : IActionPlugin
{
    public ConcurrentQueue<(ActionEdge Edge, Guid? Device, string Text, Guid RuleId)> Runs { get; } = new();

    public string? FailWith { get; set; }

    public TimeSpan Delay { get; set; }

    /// <summary>Runs on these devices wait for the task first (a device that does not answer).</summary>
    public ConcurrentDictionary<Guid, Task> WaitFor { get; } = new();

    public string Id { get; } = id;

    public string DisplayName => "Record " + Id;

    public string? IconKey => null;

    public bool NeedsDevice { get; } = needsDevice;

    public bool SupportsTurnOff { get; } = supportsTurnOff;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Text("text", "Text"),
        ActionField.Password("secret", "Secret"),
        ActionField.Number("count", "Count", 1, 10, 1) with { Required = false },
    ];

    public IReadOnlyList<(ActionEdge Edge, Guid? Device)> Edges => [.. Runs.Select(r => (r.Edge, r.Device))];

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (invocation.Device is { } device && WaitFor.TryGetValue(device.Id, out var wait))
        {
            await wait.WaitAsync(ct);
        }

        Runs.Enqueue((invocation.Edge, invocation.Device?.Id, ctx.Text(invocation.Get("text")) + "|" + invocation.Get("secret"), invocation.RuleId));
        if (FailWith is { } message)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class FakeVapixFactory : IVapixClientFactory
{
    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(new FakeClient());

    private sealed class FakeClient : IVapixClient
    {
        public Uri BaseAddress { get; } = new("http://127.0.0.1/");

        public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

        public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>Keeps every log line (level and formatted text) of every logger it creates.</summary>
public sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
{
    public System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Text, Exception? Error)> Lines { get; } = new();

    public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
    {
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerFactory owner) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
