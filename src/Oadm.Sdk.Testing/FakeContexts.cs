using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Testing;

/// <summary>A plugin's settings in memory (the server keeps them per plugin).</summary>
public sealed class FakeSettings : IPluginSettings
{
    /// <summary>The stored values (JSON) by key.</summary>
    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    /// <inheritdoc />
    public Task SetAsync(string key, string? valueJson, CancellationToken ct)
    {
        if (valueJson is null)
        {
            Values.TryRemove(key, out _);
        }
        else
        {
            Values[key] = valueJson;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The server's context of one task: the device's <see cref="Vapix"/> client, the steps (<see cref="Steps"/>, run the
/// plugin with <see cref="StepRun.RunAsync"/> to end them like the engine), warnings, log lines and progress.
/// </summary>
public sealed class FakeTaskContext : ITaskExecutionContext
{
    /// <summary>A context for the device answered by <paramref name="vapix"/>.</summary>
    public FakeTaskContext(IVapixClient vapix)
    {
        Vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));
        Steps = new TaskStepList(onWarning: ReportWarning);
    }

    /// <summary>The steps the plugin planned and ran.</summary>
    public TaskStepList Steps { get; }

    /// <summary>Every warning (<see cref="ReportWarning"/> and step warnings): the task ends Done with warnings.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Every log line, "Level: message".</summary>
    public List<string> LogLines { get; } = [];

    /// <summary>The last reported progress (0-100), -1 before any.</summary>
    public int Progress { get; private set; } = -1;

    /// <inheritdoc />
    public Guid TaskId { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public IVapixClient Vapix { get; }

    /// <inheritdoc />
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <inheritdoc />
    public ICorePlugin? Owner { get; init; }

    /// <inheritdoc />
    public IUploadedFiles Files => UploadedFiles ?? throw new NotSupportedException("Set UploadedFiles for a task that reads uploads.");

    /// <summary>The uploads the task can read; null = none.</summary>
    public IUploadedFiles? UploadedFiles { get; init; }

    /// <inheritdoc />
    public IPluginLicense? License { get; init; }

    /// <inheritdoc />
    public IProLicense? Pro { get; init; }

    /// <summary>The device's route (<see cref="Sites.IDeviceNetwork"/>): the server's own network unless a test sets a site's.</summary>
    public Sites.IDeviceNetwork? Network { get; init; } = new FakeDeviceNetwork();

    /// <inheritdoc />
    public void PlanSteps(params string[] names) => Steps.Plan(names);

    /// <inheritdoc />
    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    /// <inheritdoc />
    public void ReportProgress(int percent, string? message = null) => Progress = percent;

    /// <inheritdoc />
    public void ReportWarning(string message) => Warnings.Add(message);

    /// <inheritdoc />
    public void Log(TaskLogLevel level, string message) => LogLines.Add($"{level}: {message}");
}

/// <summary>
/// The server's context of one action run: the target's <see cref="Vapix"/> client (null for actions without devices),
/// the event log lines (<see cref="Lines"/>) and the placeholders <see cref="Text(string?)"/> fills.
/// </summary>
public sealed class FakeActionContext(IVapixClient? vapix = null) : IActionContext
{
    /// <summary>The event log lines of the run.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>Placeholder values without braces, e.g. <c>["rule"] = "Door open"</c>.</summary>
    public Dictionary<string, string> Placeholders { get; } = new(StringComparer.Ordinal);

    /// <summary>The plugin's settings.</summary>
    public FakeSettings PluginSettings { get; } = new();

    /// <inheritdoc />
    public IVapixClient? Vapix => vapix;

    /// <inheritdoc />
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <inheritdoc />
    public ICorePlugin? Owner { get; init; }

    /// <inheritdoc />
    public IPluginSettings Settings => PluginSettings;

    /// <inheritdoc />
    public IPluginLicense? License { get; init; }

    /// <inheritdoc />
    public void Log(string message) => Lines.Add(message);

    /// <inheritdoc />
    public string Text(string? text) =>
        Placeholders.Aggregate(text ?? "", (current, p) => current.Replace("{" + p.Key + "}", p.Value, StringComparison.Ordinal));
}

/// <summary>A task started through <see cref="FakeCoreContext"/>.</summary>
public sealed record FakeTaskRun(string PluginId, IReadOnlyList<Guid> DeviceIds, string? PayloadJson, string Owner);

/// <summary>
/// The server's context of a core plugin: the devices (<see cref="DeviceList"/>), a camera per device
/// (<see cref="Cameras"/>), the events the plugin publishes to its page (<see cref="Published"/>), its settings, the
/// tasks it starts (<see cref="TaskRuns"/>) and an optional data folder.
/// </summary>
public sealed class FakeCoreContext : ICorePluginContext, IDeviceRepository, IVapixClientFactory, IPluginEvents, ITaskRunner
{
    /// <summary>The managed devices.</summary>
    public List<IDeviceInfo> DeviceList { get; } = [];

    /// <summary>The camera of each device by device id.</summary>
    public Dictionary<Guid, FakeCamera> Cameras { get; } = [];

    /// <summary>Every event published to the page, in order.</summary>
    public Channel<PluginEvent> Published { get; } = Channel.CreateUnbounded<PluginEvent>();

    /// <summary>The plugin's settings.</summary>
    public FakeSettings PluginSettings { get; } = new();

    /// <summary>Every task the plugin started.</summary>
    public List<FakeTaskRun> TaskRuns { get; } = [];

    /// <summary>Adds a device with its camera and returns the device.</summary>
    public FakeDevice Add(FakeDevice device, FakeCamera? camera = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceList.Add(device);
        Cameras[device.Id] = camera ?? new FakeCamera();
        return device;
    }

    /// <inheritdoc />
    public IDeviceRepository Devices => this;

    /// <inheritdoc />
    public IVapixClientFactory Vapix => this;

    /// <inheritdoc />
    public ITaskRunner Tasks => this;

    /// <inheritdoc />
    public IPluginSettings Settings => PluginSettings;

    /// <summary>The plugin's records, in memory.</summary>
    public MemoryPluginRecords PluginRecords { get; } = new();

    /// <inheritdoc />
    public IPluginRecords? Records => PluginRecords;

    /// <inheritdoc />
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <inheritdoc />
    public IPluginEvents Events => this;

    /// <inheritdoc />
    public string? DataDirectory { get; init; }

    /// <inheritdoc />
    public IPluginLicense? License { get; init; }

    /// <inheritdoc />
    public IProLicense? Pro { get; init; }

    /// <summary>The services of other plugins (<see cref="FakePluginServices"/>); a test offers fakes here.</summary>
    public FakePluginServices FakeServices { get; } = new();

    /// <inheritdoc />
    public IPluginServices? Services => FakeServices;

    /// <summary>The bound sites and their relays (<see cref="FakeSites"/>); empty until a test adds a site.</summary>
    public FakeSites FakeSites { get; } = new();

    /// <summary>The routes and in-memory listeners of the plugin (<see cref="FakePluginNetwork"/>).</summary>
    public FakePluginNetwork FakeNetwork => field ??= new FakePluginNetwork(FakeSites);

    /// <inheritdoc />
    public Sites.IPluginNetwork? Network => FakeNetwork;

    /// <inheritdoc />
    public Sites.ISites? Sites => FakeSites;

    /// <inheritdoc />
    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. DeviceList]);

    /// <inheritdoc />
    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(DeviceList.Find(d => d.Id == id));

    /// <inheritdoc />
    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        Cameras.TryGetValue(deviceId, out var camera) ? Task.FromResult<IVapixClient>(camera) : throw new KeyNotFoundException($"No camera for device {deviceId}.");

    /// <inheritdoc />
    public void Publish(string topic, string? payloadJson) => Published.Writer.TryWrite(new PluginEvent(topic, payloadJson));

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        TaskRuns.Add(new FakeTaskRun(pluginId, deviceIds, payloadJson, owner));
        return Task.FromResult<IReadOnlyList<Guid>>([.. deviceIds.Select(_ => Guid.NewGuid())]);
    }
}

/// <summary>
/// An OADM PRO license for <see cref="FakeCoreContext.Pro"/> and <see cref="FakeTaskContext.Pro"/>: set
/// <see cref="Current"/> to change it; <see cref="IProLicense.Changed"/> follows.
/// </summary>
public sealed class FakeProLicense : IProLicense
{
    /// <summary>An active PRO license, by default without a device limit or last day.</summary>
    public static FakeProLicense ActivePro(string plan = "Unlimited", long? maxDevices = null, DateOnly? expires = null, bool trial = false) =>
        new() { Current = new ProLicenseInfo(ProLicenseStatus.Active, ProLicenseTexts.Active(plan, expires, trial), "Test customer", plan, maxDevices, expires, trial) };

    /// <inheritdoc />
    public ProLicenseInfo Current
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    } = ProLicenseInfo.None;

    /// <inheritdoc />
    public event EventHandler? Changed;
}
