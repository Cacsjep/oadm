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
}

/// <summary>Thread-safe managed devices (the engine's flows add and read them at the same time).</summary>
internal sealed class SafeDevices : IDeviceRepository
{
    private readonly ConcurrentDictionary<Guid, IDeviceInfo> _devices = new();

    public void Add(IDeviceInfo device) => _devices[device.Id] = device;

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

/// <summary>The server's add page pipeline as the engine sees it, over <see cref="FakeCam"/>s.</summary>
internal sealed class FakeAutoAdd : IDeviceAutoAdd
{
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

    private int _waiting;

    public FakeCam Cam(string serial, bool hasPassword = false) => Cams[serial] = new FakeCam { Serial = serial, HasPassword = hasPassword };

    public Task<DeviceLook> LookAsync(string address, string expectedSerial, CancellationToken ct)
    {
        Calls.Enqueue($"look {expectedSerial}");
        if (Devices.BySerial(expectedSerial) is { } managed)
        {
            return Task.FromResult(new DeviceLook(managed.Status == DeviceStatus.PasswordNotSet ? DeviceLookResult.FactoryNew : DeviceLookResult.HasPassword, managed.Id, managed.Model, "managed"));
        }

        if (!Cams.TryGetValue(expectedSerial, out var cam) || !cam.Answers)
        {
            return Task.FromResult(new DeviceLook(DeviceLookResult.Unreachable, null, null, $"No Axis device answered at {address}."));
        }

        return Task.FromResult(new DeviceLook(cam.HasPassword ? DeviceLookResult.HasPassword : DeviceLookResult.FactoryNew, null, cam.Model, "looked"));
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
        return new DeviceFirstPasswordOutcome(true, "Password set.");
    }

    public Task<DeviceAutoAddOutcome> AddWithLoginAsync(string address, string expectedSerial, Guid credentialId, IReadOnlyList<string> tags, string source, CancellationToken ct)
    {
        Calls.Enqueue($"add {expectedSerial}");
        if (Devices.BySerial(expectedSerial) is FakeDevice managed)
        {
            Devices.Add(managed with { Tags = [.. managed.Tags.Concat(tags)] });
            return Task.FromResult(new DeviceAutoAddOutcome(DeviceAutoAddResult.AlreadyManaged, managed.Id, "OADM manages this camera already."));
        }

        var cam = Cams[expectedSerial];
        var device = new FakeDevice
        {
            Serial = expectedSerial,
            Address = address,
            Model = cam.Model,
            Status = cam.HasPassword ? DeviceStatus.Ok : DeviceStatus.PasswordNotSet,
            Apis = [new DeviceApi("network-settings", "1.37")],
            Tags = tags,
        };
        Devices.Add(device);
        return Task.FromResult(new DeviceAutoAddOutcome(cam.HasPassword ? DeviceAutoAddResult.Added : DeviceAutoAddResult.AddedPasswordNotSet, device.Id, "Added."));
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

    public Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct) =>
        throw new NotSupportedException("Provisioning adds with one credential.");

    public Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct) =>
        Task.FromResult(DeviceFollowResult.NotManaged);
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

/// <summary>
/// An engine on fakes with a fake clock: <see cref="Ack"/> hands a lease to it, <see cref="EndTasks"/> ends the tasks the
/// nodes started with the outcomes of <see cref="Outcomes"/>, <see cref="RunUntilAsync"/> moves the clock until a condition holds.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
    public const string Mac1 = "AC:CC:8E:00:00:01";
    public const string Serial1 = "ACCC8E000001";

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

    /// <summary>The tasks of the user who started provisioning (Upgrade AXIS OS online); null like after a restart.</summary>
    public ITaskRunner? StarterTasks { get; set; }

    /// <summary>A secret kept in the vault, as the page's storeSecret does.</summary>
    public Task<Guid> SecretAsync(string value) => Vault.AddSecretAsync(value, CancellationToken.None);

    /// <summary>A file kept in the vault, as the page's storeFile does.</summary>
    public Task<StoredFile> FileAsync(string name, byte[] content) => Vault.AddFileAsync(name, new MemoryStream(content), CancellationToken.None);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));

    public FakeCoreContext Ctx { get; } = new();

    public SafeDevices Devices { get; } = new();

    public FakeAutoAdd AutoAdd { get; }

    public FakeDhcp Dhcp { get; } = new();

    public FakeProbe Probe { get; } = new();

    public ProvisioningOptions Options { get; }

    public ProvisioningEngine Engine { get; private set; }

    /// <summary>Plugin id -> how its tasks end (default Done).</summary>
    public ConcurrentDictionary<string, (TaskEndState State, string? Message)> Outcomes { get; } = new(StringComparer.Ordinal);

    /// <summary>How one task run ends, before <see cref="Outcomes"/>; null: by its plugin id.</summary>
    public Func<FakeTaskRun, (TaskEndState State, string? Message)?>? Decide { get; set; }

    /// <summary>A new engine on the same stores (a server restart).</summary>
    public ProvisioningEngine NewEngine() => new(
        new EngineServices(AutoAdd, Ctx, Devices, Ctx.PluginSettings, Ctx.PluginRecords, Ctx, () => Dhcp, Probe, Vapix, Vault),
        NodeRegistry.Default,
        Options,
        Time);

    public async Task RestartAsync()
    {
        await Engine.DisposeAsync();
        Engine = NewEngine();
        await Engine.LoadAsync(CancellationToken.None);
    }

    public static string Mac(int i) => $"AC:CC:8E:00:{i / 256:X2}:{i % 256:X2}";

    public static string Serial(int i) => Mac(i).Replace(":", string.Empty, StringComparison.Ordinal);

    /// <summary>A factory-new camera (or one with a password) acknowledged by the DHCP server.</summary>
    public FakeCam Ack(int i, bool hasPassword = false, string? address = null, Action<FakeCam>? configure = null)
    {
        var cam = AutoAdd.Cam(Serial(i), hasPassword);
        configure?.Invoke(cam); // before the lease: the flow starts with it
        Engine.OnAck(Mac(i), address ?? $"192.168.15.{100 + i}");
        return cam;
    }

    public CameraRow Row(int i) => Engine.Find(Mac(i))!;

    /// <summary>Ends every active task of the nodes with its configured outcome.</summary>
    public void EndTasks()
    {
        List<FakeTaskRun> runs;
        lock (Ctx.TaskRuns)
        {
            runs = [.. Ctx.TaskRuns];
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

    /// <summary>Starts a session with <paramref name="profile"/>.</summary>
    public Task StartAsync(Profile profile) => Engine.StartAsync(profile, "admin", StarterTasks, CancellationToken.None);

    /// <summary>A profile: Set password with the root entry, Add to OADM, then <paramref name="nodes"/>.</summary>
    public Profile Profile(params FlowNode[] nodes) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Lobby",
        Nodes =
        [
            Node("set-password", NodeKind.SetPassword, new SetPasswordSettings(AutoAdd.Root), continueOnError: false),
            Node("add-to-oadm", NodeKind.AddToOadm, new AddToOadmSettings(["Lobby"])),
            .. nodes,
        ],
    };

    public static FlowNode Node(string id, NodeKind kind, object? settings = null, bool continueOnError = true) =>
        new() { Id = id, Kind = kind, ContinueOnError = continueOnError, Settings = settings is null ? null : NodeSettings.Write(settings) };

    public static SetIpSettings Pool(string first = "192.168.15.10", string last = "192.168.15.50") =>
        new(first, last, "255.255.255.0", "192.168.15.1", ["192.168.15.1"]);

    public static DateTimeSettings Time1 { get; } = new("Europe/Vienna", ["10.0.0.17"]);

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
