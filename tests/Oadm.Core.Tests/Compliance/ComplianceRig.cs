using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Compliance;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Tests.Events;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Compliance;

/// <summary>The compliance engine on a real database with a fake plugin that contributes a reading check type.</summary>
internal sealed class ComplianceRig : IAsyncDisposable
{
    private TestDatabase _db = null!;
    private ComplianceEngine? _engine;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));

    public PluginRegistry Registry { get; } = new();

    public FakeCompliancePlugin Plugin { get; } = new();

    public PluginOrigin PluginOrigin { get; } = new("test.compliance-package", "1.0", null) { DisplayName = "Test checks" };

    public RecordingPublisher Events { get; } = new();

    public ComplianceCatalog Catalog { get; private set; } = null!;

    public ComplianceStore Store { get; private set; } = null!;

    public ComplianceEngine Engine => _engine!;

    public ComplianceService Service { get; private set; } = null!;

    public DeviceRepository Devices => _db.Get<DeviceRepository>();

    public static async Task<ComplianceRig> StartAsync()
    {
        var rig = new ComplianceRig { _db = await TestDatabase.CreateAsync() };
        rig.Registry.RegisterCorePlugin(rig.Plugin, rig.PluginOrigin);
        rig.Catalog = new ComplianceCatalog(rig.Registry);
        rig.Store = new ComplianceStore(rig._db.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Oadm.Core.Persistence.OadmDbContext>>(), rig.Time);
        await rig.StartEngineAsync();
        return rig;
    }

    /// <summary>A new engine on the same database (a server restart).</summary>
    public async Task StartEngineAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        _engine = new ComplianceEngine(
            Catalog, Store, Devices, _db.Get<IDeviceChangeFeed>(), new FakeVapixFactory(), Time,
            new ComplianceEngineOptions { TickInterval = TimeSpan.FromSeconds(30), FlushInterval = TimeSpan.FromHours(1) },
            NullLogger<ComplianceEngine>.Instance, () => Events);
        Service = new ComplianceService(Store, _engine, Devices, new FakeVapixFactory(), NullLoggerFactory.Instance);
        await _engine.LoadAsync(CancellationToken.None);
        await _engine.StartAsync(CancellationToken.None);
    }

    public async Task<Device> AddDeviceAsync(string address, DeviceStatus status = DeviceStatus.Ok, params string[] tags) =>
        await Devices.AddAsync(new Device
        {
            Serial = "ACCC8E" + Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture),
            Address = address,
            Model = "P3265-V",
            FirmwareVersion = "12.11.77",
            Status = status,
            Category = DeviceCategory.Camera,
            HttpsEnabled = true,
            Tags = [.. tags],
        }, CancellationToken.None);

    public Task<ComplianceCheck> SaveAsync(string typeId, JsonObject? settings = null, DeviceSelector? devices = null, string name = "Check", int interval = 60) =>
        SaveAsync(new ComplianceCheck
        {
            Name = name,
            TypeId = typeId,
            Settings = settings ?? [],
            Devices = devices ?? new DeviceSelector { Mode = DeviceSelectorMode.All },
            IntervalMinutes = interval,
            Severity = ComplianceSeverity.Problem,
        });

    public async Task<ComplianceCheck> SaveAsync(ComplianceCheck check) => (await Service.SaveAsync(check, CancellationToken.None)).Check;

    /// <summary>The current result of a check on a device (null = none).</summary>
    public ComplianceResultModel? Result(Guid checkId, Guid deviceId)
    {
        using var watch = Engine.Watch();
        return watch.Snapshot.FirstOrDefault(r => r.CheckId == checkId && r.DeviceId == deviceId);
    }

    public Task WaitForAsync(Guid checkId, Guid deviceId, ComplianceOutcome outcome, string? what = null) =>
        Eventually.TrueAsync(() => Result(checkId, deviceId)?.Outcome == outcome, what ?? $"result {outcome}");

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        Store.Dispose();
        await _db.DisposeAsync();
    }
}

/// <summary>A core plugin with one check type that reads the device: <see cref="FakeReadType"/>.</summary>
internal sealed class FakeCompliancePlugin : ICorePlugin, IComplianceSource
{
    public FakeReadType Read { get; } = new("test.read");

    public FakeReadType Second { get; } = new("test.read-second");

    public string Id => "test.compliance";

    public string DisplayName => "Test checks";

    public string? IconKey => null;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public IReadOnlyList<IComplianceCheckType> ComplianceCheckTypes => [Read, Second];

    public Task StartAsync(ICorePluginContext context, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>("{}");
}

/// <summary>Reads "the device" once per run (shared key "test:state") and fails the devices in <see cref="Failing"/>.</summary>
internal sealed class FakeReadType(string id) : IComplianceCheckType
{
    public ConcurrentBag<Guid> Evaluations { get; } = [];

    public ConcurrentDictionary<Guid, int> Reads { get; } = new();

    public ConcurrentDictionary<Guid, bool> Failing { get; } = new();

    /// <summary>Set: every evaluation waits for it (a device that does not answer).</summary>
    public TaskCompletionSource? Hold { get; set; }

    public string? ThrowWith { get; set; }

    public string Id { get; } = id;

    public string DisplayName => "Read " + Id;

    public string? IconKey => null;

    public IReadOnlyList<ActionField> Fields => [ActionField.Text("value", "Value")];

    public async Task<ComplianceResult> EvaluateAsync(IComplianceContext ctx, ComplianceInvocation invocation, CancellationToken ct)
    {
        Evaluations.Add(ctx.Device.Id);
        Assert.NotNull(ctx.Vapix);
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        if (ThrowWith is { } message)
        {
            throw new InvalidOperationException(message);
        }

        var failing = await ctx.Reads.GetOrAddAsync("test:state", _ =>
        {
            Reads.AddOrUpdate(ctx.Device.Id, 1, (_, n) => n + 1);
            return Task.FromResult(Failing.ContainsKey(ctx.Device.Id));
        }, ct);
        return failing ? ComplianceResult.Failed("Bad " + invocation.Get("value")) : ComplianceResult.Passed("Good");
    }
}

/// <summary>Records the full states the engine reports per event type.</summary>
internal sealed class RecordingPublisher : IEventPublisher
{
    public ConcurrentDictionary<string, IReadOnlyCollection<Guid>> States { get; } = new();

    public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null) => throw new NotSupportedException();

    public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null) => throw new NotSupportedException();

    public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices) => States[typeId] = [.. onDevices];

    public bool IsWatched(string typeId) => true;
}
