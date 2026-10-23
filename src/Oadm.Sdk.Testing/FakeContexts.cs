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

    /// <summary>
    /// The addresses other managed devices on this task's route use (<see cref="ManagedAddressesAsync"/>, compared as IP
    /// addresses); empty = none.
    /// </summary>
    public ISet<string> OtherDeviceAddresses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ManagedAddressesAsync(IReadOnlyCollection<string> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count > 256)
        {
            throw new ArgumentException("At most 256 addresses can be checked at once.", nameof(addresses));
        }

        var used = OtherDeviceAddresses.Select(ParseAddress).OfType<global::System.Net.IPAddress>().ToHashSet();
        return Task.FromResult<IReadOnlySet<string>>(addresses
            .Where(a => ParseAddress(a) is { } ip && used.Contains(ip))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static global::System.Net.IPAddress? ParseAddress(string? text) =>
        global::System.Net.IPAddress.TryParse(text?.Trim().Trim('[', ']'), out var ip) ? ip : null;

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
public sealed record FakeTaskRun(string PluginId, IReadOnlyList<Guid> DeviceIds, string? PayloadJson, string Owner)
{
    /// <summary>The ids of the run's tasks, one per device in device order.</summary>
    public IReadOnlyList<Guid> TaskIds { get; init; } = [];
}

/// <summary>An event a plugin published through <see cref="FakeCoreContext"/>, with its audience (the old publish: all devices).</summary>
public sealed record FakePublishedEvent(string Topic, string? PayloadJson, PluginEventAudience Audience);

/// <summary>
/// The server's context of a core plugin: the devices (<see cref="DeviceList"/>), a camera per device
/// (<see cref="Cameras"/>), the events the plugin publishes to its page (<see cref="Published"/>, with their audience in
/// <see cref="PublishedEvents"/>), its settings, the tasks it starts (<see cref="TaskRuns"/>) and an optional data folder.
/// Call methods like the host does with <see cref="InvokeAsync"/> / <see cref="OpenDownloadAsync"/> / <see cref="QueryAsync"/>:
/// during the call of a user with a device group (<see cref="CallAsGroup"/>) the context's own <see cref="Devices"/>,
/// <see cref="Vapix"/>, <see cref="Tasks"/> and <see cref="System"/> refuse with <see cref="DeviceAccessRefusedException"/>
/// (also in work the plugin starts from the call and in that user's jobs), exactly like on the server. The tasks it starts
/// stay active (<see cref="ITaskRunner.IsActive"/>) until they are cancelled or the test ends them with <see cref="EndTask"/>.
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

    private readonly ConcurrentDictionary<Guid, FakeTask> _tasks = new();

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

    /// <summary>Every device, guarded like the server: in a group user's call only for methods with a <see cref="ICorePlugin.SystemAccessReason"/>.</summary>
    public ISystemAccess System => field ??= new FakeSystemAccess(this);

    /// <summary>Every event published, with its audience, in order.</summary>
    public ConcurrentQueue<FakePublishedEvent> PublishedEvents { get; } = new();

    /// <summary>The full accesses the context refused during group users' calls ("Devices", "Vapix", "System.Devices", ...).</summary>
    public ConcurrentQueue<string> RefusedAccesses { get; } = new();

    /// <summary>How many VAPIX clients were handed out per device (through the context or a call).</summary>
    public IReadOnlyDictionary<Guid, int> ClientsCreated => _clients;

    private readonly ConcurrentDictionary<Guid, int> _clients = new();

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
    public ISecretProtector? Secrets { get; init; }

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

    /// <summary>The plugin's jobs (<see cref="FakePluginJobs"/>): owned by the user who started them.</summary>
    public FakePluginJobs FakeJobs { get; } = new();

    /// <inheritdoc />
    public IPluginJobs? Jobs => FakeJobs;

    /// <summary>The device groups (<see cref="FakeDeviceScopes"/>); a test adds and deletes groups here.</summary>
    public FakeDeviceScopes FakeScopes => field ??= new FakeDeviceScopes(this);

    /// <inheritdoc />
    public IDeviceScopes? DeviceScopes => FakeScopes;

    /// <summary>A call of a user with all devices (<see cref="FakeCallContext.AllDevices"/>).</summary>
    public FakeCallContext CallAsAllDevices(string userName = "admin") => FakeCallContext.AllDevices(this, userName);

    /// <summary>A call of a user limited to a group of <paramref name="deviceIds"/> (<see cref="FakeCallContext.WithGroup"/>).</summary>
    public FakeCallContext CallAsGroup(IEnumerable<Guid> deviceIds, string userName = "operator") => FakeCallContext.WithGroup(this, deviceIds, userName);

    /// <inheritdoc />
    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
    {
        Check("Devices");
        return Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. DeviceList]);
    }

    /// <inheritdoc />
    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
    {
        Check("Devices");
        return Task.FromResult(DeviceList.Find(d => d.Id == id));
    }

    /// <inheritdoc />
    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
    {
        Check("Vapix");
        return ClientFor(deviceId);
    }

    /// <inheritdoc />
    public void Publish(string topic, string? payloadJson) => Publish(topic, payloadJson, PluginEventAudience.AllDevices);

    /// <inheritdoc />
    public void Publish(string topic, string? payloadJson, PluginEventAudience audience)
    {
        ArgumentNullException.ThrowIfNull(audience);
        PublishedEvents.Enqueue(new FakePublishedEvent(topic, payloadJson, audience));
        Published.Writer.TryWrite(new PluginEvent(topic, payloadJson));
    }

    /// <summary>
    /// The events published so far as the page of <paramref name="watcher"/> receives them on the server: only those whose
    /// audience covers the watcher (the old publish: watchers with all devices), rows of a <see cref="PluginEventAudienceKind.DeviceRows"/>
    /// event filtered to the watcher's devices.
    /// </summary>
    public IReadOnlyList<PluginEvent> EventsSeenBy(ICallContext watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        return [.. PublishedEvents.Select(e => FakeHostRules.Deliver(this, e, watcher)).OfType<PluginEvent>()];
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        Check("Tasks");
        return Run(pluginId, deviceIds, payloadJson, owner);
    }

    /// <summary>Cancels a task the plugin started: true when it was still active.</summary>
    bool ITaskRunner.Cancel(Guid taskId)
    {
        Check("Tasks");
        return EndTask(taskId);
    }

    /// <summary>True while a task the plugin started is active, false once it ended, null for an unknown task.</summary>
    bool? ITaskRunner.IsActive(Guid taskId)
    {
        Check("Tasks");
        return TaskActive(taskId);
    }

    /// <summary>Ends a task the plugin started (as if it finished): true when it was still active.</summary>
    public bool EndTask(Guid taskId) => _tasks.TryGetValue(taskId, out var task) && Interlocked.Exchange(ref task.Ended, 1) == 0;

    /// <summary>
    /// Calls <paramref name="method"/> like the host: refuses a method the plugin keeps for users with all devices
    /// (<see cref="ICorePlugin.RequiresAllDevices"/>) for a group user before the plugin runs, and refuses the context's full
    /// accesses during the call of a group user (except <see cref="System"/> in methods with a
    /// <see cref="ICorePlugin.SystemAccessReason"/>).
    /// </summary>
    public Task<string?> InvokeAsync(ICorePlugin plugin, ICallContext call, string method, string? payloadJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        RequireOwn(call);
        return FakeHostRules.CallAsync(plugin, call, method, () => plugin.InvokeAsync(call, method, payloadJson, ct));
    }

    /// <summary>Opens a download like the host (the rules of <see cref="InvokeAsync"/>).</summary>
    public Task<PluginDownload> OpenDownloadAsync(ICorePlugin plugin, ICallContext call, string method, string? payloadJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        RequireOwn(call);
        if (plugin is not ICorePluginDownloads downloads)
        {
            throw new NotSupportedException($"The plugin '{plugin.Id}' has no downloads.");
        }

        return FakeHostRules.CallAsync(plugin, call, method, () => downloads.OpenDownloadAsync(call, method, payloadJson, ct));
    }

    /// <summary>
    /// Runs a task plugin query like the host: <paramref name="device"/> must be in the call's devices (else
    /// <see cref="DevicesNotAvailableException"/>, nothing sent), the query context's <c>Devices</c> are the call's devices,
    /// its VAPIX client is the device's camera.
    /// </summary>
    public async Task<string?> QueryAsync(ITaskPluginQuery plugin, ICallContext call, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(device);
        if (!call.Contains(device))
        {
            throw new DevicesNotAvailableException();
        }

        var vapix = await ClientFor(device.Id).ConfigureAwait(false);
        var context = new FakeQueryContext(vapix, Logger, call.Devices, this, device);
        using (FakeHostRules.Use(new FakeHostRules.Mark(call.HasAllDevices, false)))
        {
            return await plugin.QueryAsync(context, call, device, method, payloadJson, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A call of another context would mix two servers: refused.</summary>
    private void RequireOwn(ICallContext call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call is FakeCallContext fake && !ReferenceEquals(fake.Core, this))
        {
            throw new ArgumentException("The call belongs to another FakeCoreContext.", nameof(call));
        }
    }

    /// <summary>The camera of a device without the call checks (calls and the system access).</summary>
    internal Task<IVapixClient> ClientFor(Guid deviceId)
    {
        if (!Cameras.TryGetValue(deviceId, out var camera))
        {
            throw new KeyNotFoundException($"No camera for device {deviceId}.");
        }

        _clients.AddOrUpdate(deviceId, 1, (_, n) => n + 1);
        return Task.FromResult<IVapixClient>(camera);
    }

    /// <summary>Records a task run without the call checks; <paramref name="startedBy"/> is the user of a call, null for the context.</summary>
    internal Task<IReadOnlyList<Guid>> Run(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, string? startedBy = null)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        IReadOnlyList<Guid> ids = [.. deviceIds.Select(_ => Guid.NewGuid())];
        for (var i = 0; i < ids.Count; i++)
        {
            _tasks[ids[i]] = new FakeTask(deviceIds[i], startedBy);
        }

        lock (TaskRuns)
        {
            TaskRuns.Add(new FakeTaskRun(pluginId, deviceIds, payloadJson, owner) { TaskIds = ids });
        }

        return Task.FromResult(ids);
    }

    /// <summary>The device of a task the plugin started and the user whose call started it, or null for an unknown task.</summary>
    internal (Guid DeviceId, string? StartedBy)? TaskOf(Guid taskId) => _tasks.TryGetValue(taskId, out var task) ? (task.DeviceId, task.StartedBy) : null;

    /// <summary>Whether a task is active without the call checks (null: unknown).</summary>
    internal bool? TaskActive(Guid taskId) => _tasks.TryGetValue(taskId, out var task) ? Volatile.Read(ref task.Ended) == 0 : null;

    private sealed class FakeTask(Guid deviceId, string? startedBy)
    {
#pragma warning disable SA1401, CA1051 // Interlocked needs the field.
        public int Ended;
#pragma warning restore SA1401, CA1051

        public Guid DeviceId { get; } = deviceId;

        public string? StartedBy { get; } = startedBy;
    }

    /// <summary>The server's refusal of the full accesses in a group user's call.</summary>
    internal void Check(string member, bool system = false)
    {
        if (FakeHostRules.Refuses(system))
        {
            RefusedAccesses.Enqueue(member);
            throw new DeviceAccessRefusedException();
        }
    }

    private sealed class FakeSystemAccess(FakeCoreContext core) : ISystemAccess, IDeviceRepository, IVapixClientFactory, ITaskRunner
    {
        public IDeviceRepository Devices => this;

        public IVapixClientFactory Vapix => this;

        public ITaskRunner Tasks => this;

        public IDeviceEventStreams? EventStreams => null;

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
        {
            core.Check("System.Devices", system: true);
            return Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. core.DeviceList]);
        }

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
        {
            core.Check("System.Devices", system: true);
            return Task.FromResult(core.DeviceList.Find(d => d.Id == id));
        }

        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            core.Check("System.Vapix", system: true);
            return core.ClientFor(deviceId);
        }

        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            core.Check("System.Tasks", system: true);
            return core.Run(pluginId, deviceIds, payloadJson, owner);
        }
    }

    private sealed class FakeQueryContext(IVapixClient vapix, ILogger logger, IDeviceRepository devices, FakeCoreContext core, IDeviceInfo device) : ITaskQueryContext
    {
        public IVapixClient Vapix { get; } = vapix;

        public ILogger Logger { get; } = logger;

        public IDeviceRepository? Devices { get; } = devices;

        /// <summary>Like the server: every managed device on the queried device's route, also outside the call's devices.</summary>
        public Task<IReadOnlySet<string>> ManagedAddressesAsync(IReadOnlyCollection<string> addresses, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(addresses);
            var used = core.DeviceList
                .Where(d => d.SiteId == device.SiteId)
                .Select(d => ParseAddress(d.Address))
                .OfType<global::System.Net.IPAddress>()
                .ToHashSet();
            return Task.FromResult<IReadOnlySet<string>>(addresses
                .Where(a => ParseAddress(a) is { } ip && used.Contains(ip))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        private static global::System.Net.IPAddress? ParseAddress(string? text) =>
            global::System.Net.IPAddress.TryParse(text?.Trim().Trim('[', ']'), out var ip) ? ip : null;
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
