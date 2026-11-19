using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Network;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>A camera on the fake network: factory-new until it has a password.</summary>
internal sealed class FakeCam
{
    public required string Serial { get; init; }

    public string Model { get; init; } = "P3265-LVE";

    public bool HasPassword { get; set; }

    public bool Answers { get; set; } = true;

    /// <summary>The camera refuses the first password (its passphrase policy).</summary>
    public bool RefusesPassword { get; set; }

    public int PasswordWrites { get; set; }

    /// <summary>The camera is still starting after the server's wait: it does not say whether it needs setup.</summary>
    public bool Starting { get; set; }
}

/// <summary>Thread-safe managed devices (the engine's flows add and read them at the same time).</summary>
internal sealed class SafeDevices : IDeviceRepository
{
    private readonly ConcurrentDictionary<Guid, IDeviceInfo> _devices = new();

    public void Add(IDeviceInfo device) => _devices[device.Id] = device;

    /// <summary>The device is removed from OADM.</summary>
    public void Remove(Guid id) => _devices.TryRemove(id, out _);

    public void AddRange(IEnumerable<IDeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            Add(device);
        }
    }

    public IDeviceInfo? BySerial(string serial) => _devices.Values.FirstOrDefault(d => d.Serial == serial);

    public int Count => _devices.Count;

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _devices.Values]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(_devices.GetValueOrDefault(id));
}

/// <summary>
/// The server's add page pipeline as the engine sees it, over <see cref="FakeCam"/>s: the look always asks the camera (also for a
/// managed device), Set password stores the login and OADM reads the camera (Ok with its APIs), the automatic add of a new
/// camera stores it as Password not set and reports it to the watchers like the server's device adder.
/// </summary>
internal sealed class FakeAutoAdd : IDeviceAutoAdd
{
    private readonly List<Action<FactoryNewDevice>> _watchers = [];
    private readonly Lock _gate = new();

    public FakeAutoAdd(SafeDevices devices)
    {
        Devices = devices;
        Root = Guid.NewGuid();
        Credentials[Root] = "root";
    }

    public SafeDevices Devices { get; }

    public Guid Root { get; }

    public ConcurrentDictionary<Guid, string> Credentials { get; } = new();

    public ConcurrentDictionary<string, FakeCam> Cams { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>When set, Set password waits for it (a gate that holds the work).</summary>
    public TaskCompletionSource? PasswordGate { get; set; }

    /// <summary>Set passwords that are waiting at <see cref="PasswordGate"/> now.</summary>
    public int WaitingAtGate => Volatile.Read(ref _waiting);

    /// <summary>False: OADM does not read the camera after its password (it stays as it is).</summary>
    public bool ReadsAfterPassword { get; set; } = true;

    private int _waiting;

    public FakeCam Cam(string serial, bool hasPassword = false) => Cams[serial] = new FakeCam { Serial = serial, HasPassword = hasPassword };

    /// <summary>A managed device of a camera, as the add page stores it (Password not set until it has one).</summary>
    public FakeDevice Managed(FakeCam cam, string address, Guid? siteId = null)
    {
        var device = new FakeDevice
        {
            Serial = cam.Serial,
            Address = address,
            Model = cam.Model,
            Status = cam.HasPassword ? DeviceStatus.Ok : DeviceStatus.PasswordNotSet,
            SiteId = siteId,
        };
        Devices.Add(device);
        return device;
    }

    /// <summary>The server reports a device that needs its first password (the device adder, the polling).</summary>
    public void Report(FakeDevice device, bool added = true, bool hadPassword = false)
    {
        Action<FactoryNewDevice>[] watchers;
        lock (_gate)
        {
            watchers = [.. _watchers];
        }

        foreach (var watcher in watchers)
        {
            watcher(new FactoryNewDevice(device.Id, device.Serial, device.Address, device.SiteId is not null, added) { HadPassword = hadPassword });
        }
    }

    public IDisposable WatchFactoryNew(Action<FactoryNewDevice> found)
    {
        lock (_gate)
        {
            _watchers.Add(found);
        }

        return new Unwatch(this, found);
    }

    public int Watchers
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count;
            }
        }
    }

    public Task<DeviceLook> LookAsync(string address, string expectedSerial, CancellationToken ct)
    {
        Calls.Enqueue($"look {expectedSerial}");
        var managed = Devices.BySerial(expectedSerial);
        if (!Cams.TryGetValue(expectedSerial, out var cam) || !cam.Answers)
        {
            return Task.FromResult(new DeviceLook(DeviceLookResult.Unreachable, managed?.Id, null, $"No Axis device answered at {address}."));
        }

        if (cam.Starting)
        {
            return Task.FromResult(new DeviceLook(DeviceLookResult.Starting, managed?.Id, cam.Model, "The camera did not finish starting within 2 minutes."));
        }

        return Task.FromResult(new DeviceLook(cam.HasPassword ? DeviceLookResult.HasPassword : DeviceLookResult.FactoryNew, managed?.Id, cam.Model, "looked"));
    }

    public async Task<DeviceFirstPasswordOutcome> SetFirstPasswordAsync(string address, string expectedSerial, Guid credentialId, string source, CancellationToken ct)
    {
        Calls.Enqueue($"password {expectedSerial}");
        if (PasswordGate is { } gate)
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                await gate.Task.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        if (!Credentials.ContainsKey(credentialId))
        {
            return new DeviceFirstPasswordOutcome(false, DeviceFirstSetupTexts.CredentialGone);
        }

        var cam = Cams[expectedSerial];
        if (cam.HasPassword)
        {
            return new DeviceFirstPasswordOutcome(false, "The device already has a password. Nothing was changed.");
        }

        if (cam.RefusesPassword)
        {
            return new DeviceFirstPasswordOutcome(false, "The device rejected the password: too short.");
        }

        cam.HasPassword = true;
        cam.PasswordWrites++;
        if (ReadsAfterPassword && Devices.BySerial(expectedSerial) is FakeDevice managed)
        {
            Devices.Add(managed with { Status = DeviceStatus.Ok, Apis = [new DeviceApi("network-settings", "1.37")] });
        }

        return new DeviceFirstPasswordOutcome(true, "Password set.");
    }

    public Task<IReadOnlyList<DeviceCredentialName>> ListCredentialsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DeviceCredentialName>>([.. Credentials.Select(c => new DeviceCredentialName(c.Key, c.Value))]);

    public Task<DeviceCredentialName> AddCredentialAsync(string userName, string password, CancellationToken ct)
    {
        Calls.Enqueue($"credential {userName}");
        var id = Guid.NewGuid();
        Credentials[id] = userName;
        return Task.FromResult(new DeviceCredentialName(id, userName));
    }

    /// <summary>The automatic add: a new camera is stored as Password not set and reported; a managed one is not added again.</summary>
    public Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct)
    {
        Calls.Enqueue($"add {expectedSerial}");
        if (Devices.BySerial(expectedSerial) is not null)
        {
            return Task.FromResult(new DeviceAutoAddOutcome(DeviceAutoAddResult.AlreadyManaged, null, "managed already"));
        }

        var cam = Cams[expectedSerial];
        var device = Managed(cam, address);
        if (!cam.HasPassword)
        {
            Report(device);
        }

        return Task.FromResult(new DeviceAutoAddOutcome(cam.HasPassword ? DeviceAutoAddResult.Added : DeviceAutoAddResult.AddedPasswordNotSet, device.Id, "Added."));
    }

    public Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct) =>
        Task.FromResult(DeviceFollowResult.NotManaged);

    private sealed class Unwatch(FakeAutoAdd owner, Action<FactoryNewDevice> found) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._watchers.Remove(found);
            }
        }
    }
}

/// <summary>The DHCP server's read-only view: acknowledgements the test hands out.</summary>
internal sealed class FakeDhcp : IDhcpServerLeases
{
    private readonly List<DhcpAck> _acks = [];
    private readonly Lock _gate = new();

    public DhcpServerSummary State { get; set; } = new(true, "Running on Ethernet", "Ethernet", "192.168.15.1", "255.255.255.0", "192.168.15.100", "192.168.15.200");

    public List<DhcpAck> Active { get; } = [];

    public int Reads { get; private set; }

    public DhcpServerSummary Summary() => State;

    /// <summary>The server acknowledges a lease.</summary>
    public void Ack(string mac, string address)
    {
        lock (_gate)
        {
            _acks.Add(new DhcpAck(_acks.Count + 1, mac, address, DateTimeOffset.UnixEpoch));
        }
    }

    public DhcpAcks AcksSince(long after, int max)
    {
        lock (_gate)
        {
            Reads++;
            var last = _acks.Count;
            after = Math.Clamp(after, 0, last);
            var list = _acks.Skip((int)after).Take(max).ToList();
            return new DhcpAcks(list, list.Count > 0 ? list[^1].Sequence : last, false);
        }
    }

    public IReadOnlyList<DhcpAck> ActiveLeases(IReadOnlyCollection<uint> ouiBlocks, int max) => [.. Active.Take(max)];
}

/// <summary>Is an address taken? A set of answering addresses, and an optional gate that holds every probe.</summary>
internal sealed class FakeProbe : IAddressProbe
{
    public ConcurrentDictionary<string, bool> Answering { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Probed { get; } = new();

    public TaskCompletionSource? Gate { get; set; }

    public async Task<AddressProbeResult> ProbeAsync(string address, CancellationToken ct)
    {
        Probed.Enqueue(address);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return Answering.ContainsKey(address) ? new AddressProbeResult(true, false) : AddressProbeResult.Silent;
    }
}

/// <summary>Reversible "encryption" that marks its output, to prove what is stored encrypted.</summary>
internal sealed class FakeSecrets : ISecretProtector
{
    public string Protect(string plaintext, string purpose) => "enc:" + purpose + ":" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(string protectedValue, string purpose)
    {
        var prefix = "enc:" + purpose + ":";
        if (!protectedValue.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new System.Security.Cryptography.CryptographicException("Wrong purpose.");
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[prefix.Length..]));
    }
}

/// <summary>A camera's VAPIX: its API list and a handler for every request (recorded in <see cref="Requests"/>).</summary>
internal sealed class ScriptedVapix : IVapixClient
{
    public Uri BaseAddress { get; } = new("http://192.168.15.101/");

    public List<DeviceApi> Apis { get; set; } = [];

    public Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

    public ConcurrentQueue<(string Method, string Path, string? Body)> Requests { get; } = new();

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeviceApi>>(Apis);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method.Method, request.RequestUri!.OriginalString, body));
        return await Handler(request, body, ct);
    }

    public static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

/// <summary>VAPIX clients by device id; an unknown device has none.</summary>
internal sealed class FakeVapixFactory : IVapixClientFactory
{
    public ConcurrentDictionary<Guid, ScriptedVapix> Clients { get; } = new();

    public ScriptedVapix For(Guid deviceId) => Clients.GetOrAdd(deviceId, _ => new ScriptedVapix());

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        Clients.TryGetValue(deviceId, out var client) ? Task.FromResult<IVapixClient>(client) : throw new InvalidOperationException("The device is not reachable.");
}

/// <summary>Plugin records whose writes fail while <see cref="FailWrites"/> is above 0 (each failure counts it down).</summary>
internal sealed class FlakyRecords(IPluginRecords inner) : IPluginRecords
{
    private int _failWrites;

    public int FailWrites
    {
        get => Volatile.Read(ref _failWrites);
        set => Volatile.Write(ref _failWrites, value);
    }

    /// <summary>The collection whose list waits for <see cref="ListGate"/>.</summary>
    public string? ListGateCollection { get; set; }

    /// <summary>While set and not completed, a list of <see cref="ListGateCollection"/> waits for it.</summary>
    public TaskCompletionSource? ListGate { get; set; }

    /// <summary>Completed when a list of <see cref="ListGateCollection"/> reached the gate.</summary>
    public TaskCompletionSource ListEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IReadOnlyDictionary<string, string>> ListAsync(string collection, CancellationToken ct)
    {
        if (ListGate is { } gate && collection == ListGateCollection)
        {
            ListEntered.TrySetResult();
            await gate.Task.WaitAsync(ct);
        }

        return await inner.ListAsync(collection, ct);
    }

    public Task SetManyAsync(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct)
    {
        int left;
        do
        {
            left = FailWrites;
        }
        while (left > 0 && Interlocked.CompareExchange(ref _failWrites, left - 1, left) != left);

        return left > 0 ? Task.FromException(new IOException("The database is busy.")) : inner.SetManyAsync(collection, records, ct);
    }

    public Task ClearAsync(string collection, CancellationToken ct) => inner.ClearAsync(collection, ct);
}

/// <summary>
/// A service on fakes with a fake clock: <see cref="NewCamera"/> puts a camera on the fake network (and in OADM),
/// <see cref="ExecuteAsync"/> runs one provisioning task like the task engine, <see cref="EndTasks"/> ends the tasks the nodes
/// started with the outcomes of <see cref="Outcomes"/>, <see cref="RunUntilAsync"/> moves the clock until a condition holds.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
    public const string Mac1 = "AC:CC:8E:00:00:01";
    public const string Serial1 = "ACCC8E000001";

    private bool _loaded;

    public Harness(ProvisioningOptions? options = null)
    {
        AutoAdd = new FakeAutoAdd(Devices);
        Options = options ?? new ProvisioningOptions();
        Vault = new NodeVault(Ctx.PluginSettings, new FakeSecrets(), DataFolder, Ctx.FakeUploads, Time);
        Engine = NewEngine();
    }

    /// <summary>The plugin's data folder of this harness (unique; deleted at the end).</summary>
    public string DataFolder { get; } = Path.Combine(Path.GetTempPath(), "oadm-prov-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The typed secrets and kept files, with a marking fake protector and the context's uploads.</summary>
    public NodeVault Vault { get; }

    /// <summary>The cameras' VAPIX for the nodes that use it.</summary>
    public FakeVapixFactory Vapix { get; } = new();

    /// <summary>The tasks of the administrator who turned provisioning on (Upgrade AXIS OS online); null like after a restart.</summary>
    public ITaskRunner? StarterTasks { get; set; }

    /// <summary>The profile the tasks run (the plugin's active profile without errors); null: none.</summary>
    public Profile? Active { get; set; }

    /// <summary>A secret kept in the vault, as the dialog's storeSecret does.</summary>
    public Task<Guid> SecretAsync(string value) => Vault.AddSecretAsync(value, CancellationToken.None);

    /// <summary>A file kept in the vault, as the dialog's storeFile does.</summary>
    public Task<StoredFile> FileAsync(string name, byte[] content) => Vault.AddFileAsync(name, new MemoryStream(content), CancellationToken.None);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));

    public FakeCoreContext Ctx { get; } = new();

    public SafeDevices Devices { get; } = new();

    public FakeAutoAdd AutoAdd { get; }

    public FakeDhcp Dhcp { get; } = new();

    public FakeProbe Probe { get; } = new();

    /// <summary>The context's records; <see cref="FlakyRecords.FailWrites"/> makes the next writes fail.</summary>
    public FlakyRecords Records => _records ??= new FlakyRecords(Ctx.PluginRecords);

    private FlakyRecords? _records;

    public ProvisioningOptions Options { get; }

    public ProvisioningService Engine { get; private set; }

    /// <summary>Plugin id -> how its tasks end (default Done).</summary>
    public ConcurrentDictionary<string, (TaskEndState State, string? Message)> Outcomes { get; } = new(StringComparer.Ordinal);

    /// <summary>How one task run ends, before <see cref="Outcomes"/>; null: by its plugin id.</summary>
    public Func<FakeTaskRun, (TaskEndState State, string? Message)?>? Decide { get; set; }

    /// <summary>A new service on the same stores (a server restart).</summary>
    public ProvisioningService NewEngine() => new(
        new EngineServices(AutoAdd, Ctx, Devices, Ctx.PluginSettings, Records, () => Dhcp, Probe, Vapix, Vault),
        _ => Task.FromResult(Active),
        NodeRegistry.Default,
        Options,
        Time);

    public async Task LoadAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await Engine.LoadAsync(CancellationToken.None);
        }
    }

    public async Task RestartAsync()
    {
        await Engine.DisposeAsync();
        Engine = NewEngine();
        await Engine.LoadAsync(CancellationToken.None);
    }

    public static string Mac(int i) => $"AC:CC:8E:00:{i / 256:X2}:{i % 256:X2}";

    public static string Serial(int i) => Mac(i).Replace(":", string.Empty, StringComparison.Ordinal);

    public static string Address(int i) => $"192.168.15.{100 + i}";

    /// <summary>A factory-new camera (or one with a password) on the fake network; <paramref name="managed"/>: in OADM too.</summary>
    public (FakeCam Cam, FakeDevice? Device) NewCamera(int i, bool hasPassword = false, bool managed = true, Action<FakeCam>? configure = null)
    {
        var cam = AutoAdd.Cam(Serial(i), hasPassword);
        configure?.Invoke(cam);
        return (cam, managed ? AutoAdd.Managed(cam, Address(i)) : null);
    }

    /// <summary>The device of camera <paramref name="i"/> as OADM has it now.</summary>
    public FakeDevice Device(int i) => (FakeDevice)Devices.BySerial(Serial(i))!;

    /// <summary>A lease of the DHCP server to camera <paramref name="i"/>.</summary>
    public void Ack(int i, string? address = null) => Engine.OnAck(Mac(i), address ?? Address(i));

    /// <summary>The provisioning tasks started so far.</summary>
    public List<FakeTaskRun> ProvisionRuns
    {
        get
        {
            lock (Ctx.TaskRuns)
            {
                return [.. Ctx.TaskRuns.Where(r => r.PluginId == ProvisioningInfo.TaskPluginId)];
            }
        }
    }

    /// <summary>The device ids of every provisioning task started so far.</summary>
    public List<Guid> ProvisionedDevices => [.. ProvisionRuns.SelectMany(r => r.DeviceIds)];

    /// <summary>
    /// Runs the provisioning task of <paramref name="deviceId"/> of <paramref name="run"/> like the task engine (the steps end
    /// as the engine ends them), ending the nodes' tasks and moving the clock until it ends; then the fake task ends too.
    /// </summary>
    public async Task<TaskRunResult> ExecuteAsync(FakeTaskRun run, Guid deviceId)
    {
        var index = run.DeviceIds.ToList().IndexOf(deviceId);
        var taskId = run.TaskIds[index];
        var device = await Devices.FindAsync(deviceId, CancellationToken.None) ?? throw new InvalidOperationException("No such device.");
        var inner = new FakeTaskContext(new ScriptedVapix());
        var ctx = new IdTaskContext(inner, taskId);
        Exception? failure = null;
        var execute = StepRun.RunAsync(inner.Steps, () => Engine.ExecuteAsync(ctx, device, run.PayloadJson, CancellationToken.None));
        await RunUntilAsync(() => execute.IsCompleted, "the provisioning task ended");
        try
        {
            await execute;
        }
#pragma warning disable CA1031 // The result carries the failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        Ctx.EndTask(taskId, failure is null ? (inner.Warnings.Count > 0 ? TaskEndState.DoneWithWarnings : TaskEndState.Done) : TaskEndState.Failed, failure?.Message);
        return new TaskRunResult(inner, failure);
    }

    /// <summary>Runs the provisioning task of the only device of the last provisioning run.</summary>
    public Task<TaskRunResult> ExecuteLastAsync() => ExecuteAsync(ProvisionRuns[^1], ProvisionRuns[^1].DeviceIds.Single());

    /// <summary>Ends every active task of the nodes with its configured outcome (never a provisioning task: <see cref="ExecuteAsync"/> runs those).</summary>
    public void EndTasks()
    {
        List<FakeTaskRun> runs;
        lock (Ctx.TaskRuns)
        {
            runs = [.. Ctx.TaskRuns.Where(r => r.PluginId != ProvisioningInfo.TaskPluginId)];
        }

        foreach (var run in runs)
        {
            var (state, message) = Decide?.Invoke(run) ?? Outcomes.GetValueOrDefault(run.PluginId, (TaskEndState.Done, null));
            foreach (var id in run.TaskIds)
            {
                Ctx.EndTask(id, state, message);
            }
        }
    }

    /// <summary>Ends tasks and moves the clock until <paramref name="condition"/> holds.</summary>
    public Task RunUntilAsync(Func<bool> condition, string what) =>
        Eventually.TrueAsync(() =>
        {
            EndTasks();
            if (condition())
            {
                return true;
            }

            Time.Advance(TimeSpan.FromSeconds(1));
            return false;
        }, what);

    /// <summary>Makes <paramref name="profile"/> the active one and turns provisioning on.</summary>
    public async Task StartAsync(Profile profile)
    {
        Active = profile;
        await LoadAsync();
        await Engine.TurnOnAsync("admin", StarterTasks, CancellationToken.None);
    }

    /// <summary>A profile: Set password with the root entry, then <paramref name="nodes"/>.</summary>
    public Profile Profile(params FlowNode[] nodes) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Lobby",
        Nodes =
        [
            Node("set-password", NodeKind.SetPassword, new SetPasswordSettings(AutoAdd.Root), continueOnError: false),
            .. nodes,
        ],
    };

    public static FlowNode Node(string id, NodeKind kind, object? settings = null, bool continueOnError = true) =>
        new() { Id = id, Kind = kind, ContinueOnError = continueOnError, Settings = settings is null ? null : NodeSettings.Write(settings) };

    /// <summary>A Network node's pool.</summary>
    public static NetworkNodeSettings Pool(string first = "192.168.15.10", string last = "192.168.15.50", params string[] dns) =>
        new(IpMode.Pool, first, last, "255.255.255.0", "192.168.15.1", dns);

    public static NetworkNode.Pool CheckedPool(string first = "192.168.15.10", string last = "192.168.15.50") =>
        NetworkNode.Pool.Of(Pool(first, last), out _)!;

    public static DateTimeSettings Time1 { get; } = new("Europe/Vienna", ["10.0.0.17"]);

    /// <summary>A camera for a node run alone.</summary>
    public static CameraInfo Camera(int i = 1, Guid? deviceId = null, string? model = null) => new(Serial(i), Address(i), model, deviceId ?? Guid.NewGuid());

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        Vault.Dispose();
        if (Directory.Exists(DataFolder))
        {
            Directory.Delete(DataFolder, recursive: true);
        }
    }

    public static JsonElement Json(object value) => NodeSettings.Write(value);
}

/// <summary>How a provisioning task ended in <see cref="Harness.ExecuteAsync"/>: its steps and its failure.</summary>
internal sealed record TaskRunResult(FakeTaskContext Context, Exception? Failure)
{
    public string[] Steps => StepRun.Lines(Context.Steps);

    public string? Detail(string step) => StepRun.Detail(Context.Steps, step);
}

/// <summary>A fake task context with the task id the engine gave the task.</summary>
internal sealed class IdTaskContext(FakeTaskContext inner, Guid taskId) : ITaskExecutionContext
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
