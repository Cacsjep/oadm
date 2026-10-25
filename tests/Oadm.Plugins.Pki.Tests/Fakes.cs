using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.TrustStore;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>One recorded tool call of <see cref="FakeRunner"/>.</summary>
internal sealed record ToolCall(string FileName, IReadOnlyList<string> Arguments, bool Elevated);

/// <summary>Records tool calls and answers them; never starts a process. Tests never touch a real trust store.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    public List<ToolCall> Calls { get; } = [];

    /// <summary>Tools found on the search path (name -> full path).</summary>
    public Dictionary<string, string> Tools { get; } = new(StringComparer.Ordinal);

    /// <summary>The answer per call; default success with empty output.</summary>
    public Func<ToolCall, ProcessResult> Answer { get; set; } = _ => new ProcessResult(0, string.Empty);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var call = new ToolCall(fileName, [.. arguments], Elevated: false);
        Calls.Add(call);
        return Task.FromResult(Answer(call));
    }

    public Task<ProcessResult> RunElevatedAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var call = new ToolCall(fileName, [.. arguments], Elevated: true);
        Calls.Add(call);
        return Task.FromResult(Answer(call));
    }

    public string? FindExecutable(string name) => Tools.GetValueOrDefault(name);
}

/// <summary>An in-memory Windows LocalMachine Root store.</summary>
internal sealed class FakeRootStore : IMachineRootStore
{
    public HashSet<string> Thumbprints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Denied { get; set; }

    public bool Contains(string sha1Thumbprint) => Thumbprints.Contains(sha1Thumbprint);

    public void Add(X509Certificate2 certificate)
    {
        if (Denied)
        {
            throw new CryptographicException("Access is denied.");
        }

        Thumbprints.Add(certificate.Thumbprint);
    }

    public void Remove(string sha1Thumbprint) => Thumbprints.Remove(sha1Thumbprint);
}

/// <summary>A server trust store in memory (the PKI service tests).</summary>
internal sealed class FakeTrustStore : ITrustStoreInstaller
{
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? FailWith { get; set; }

    public int Installs { get; private set; }

    public Task<bool> IsInstalledAsync(X509Certificate2 ca, CancellationToken ct) => Task.FromResult(Installed.Contains(ca.Thumbprint));

    public Task<TrustStoreResult> InstallAsync(X509Certificate2 ca, CancellationToken ct)
    {
        Installs++;
        if (FailWith is { } error)
        {
            return Task.FromResult(new TrustStoreResult(false, error));
        }

        Installed.Add(ca.Thumbprint);
        return Task.FromResult(new TrustStoreResult(true));
    }

    public Task<TrustStoreResult> RemoveAsync(X509Certificate2 ca, CancellationToken ct)
    {
        Installed.Remove(ca.Thumbprint);
        return Task.FromResult(new TrustStoreResult(false));
    }
}

internal static class TestSecrets
{
    public static ISecretProtector Create() => new PluginSecretProtector(new CredentialProtector(RandomNumberGenerator.GetBytes(CredentialProtector.KeySize)));
}

/// <summary>Core plugin context: in-memory settings, real secret protector, event hub and trust anchor registry.</summary>
internal sealed class TestCoreContext(IPluginSettings settings, ISecretProtector? secrets, IPluginEvents? events, ITrustAnchors? anchors) : ICorePluginContext
{
    /// <summary>The managed devices (none by default: the context then has no device list).</summary>
    public IReadOnlyList<IDeviceInfo>? DeviceList { get; init; }

    /// <summary>The sites of the host (the policies' device choice).</summary>
    public FakeSites FakeSites { get; init; } = new();

    public Sdk.Sites.ISites? Sites => FakeSites;

    /// <summary>The protection of the policies' tags.</summary>
    public FakeProtectedTags FakeProtectedTags { get; init; } = new();

    public IProtectedTags? ProtectedTags => FakeProtectedTags;

    /// <summary>The event rules' publisher (the certificate expiry events); null: a host without event rules.</summary>
    public IEventPublisher? EventPublisher { get; init; }

    /// <summary>The plugin services (PKI offers its CA and its policies there).</summary>
    public FakePluginServices FakeServices { get; } = new();

    public IPluginServices? Services => FakeServices;

    public IDeviceRepository Devices => DeviceList is { } list ? new ListRepository(list) : throw new NotSupportedException();

    private sealed class ListRepository(IReadOnlyList<IDeviceInfo> list) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult(list);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(list.FirstOrDefault(d => d.Id == id));
    }

    public IVapixClientFactory Vapix => throw new NotSupportedException();

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings { get; } = settings;

    public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public ISecretProtector? Secrets { get; } = secrets;

    public IPluginEvents? Events { get; } = events;

    public ITrustAnchors? TrustAnchors { get; } = anchors;

    /// <summary>The device groups (none until a test adds one).</summary>
    public FakeDeviceScopes Scopes { get; } = new FakeCoreContext().FakeScopes;

    public IDeviceScopes? DeviceScopes => Scopes;

    /// <summary>The plugin records (issued certificates, revocation list); shared across restarts when a test passes its own.</summary>
    public MemoryPluginRecords PluginRecords { get; init; } = new();

    /// <summary>Other records than <see cref="PluginRecords"/> (the database of the backup test).</summary>
    public IPluginRecords? OtherRecords { get; init; }

    public IPluginRecords? Records => OtherRecords ?? PluginRecords;
}

/// <summary>A started PKI plugin over in-memory settings (fast 2048-bit keys), with its hub and anchors.</summary>
internal sealed class PkiHarness : IAsyncDisposable
{
    private PkiHarness(PkiPlugin plugin, InMemoryPluginSettingsProvider settings, PluginEventHub hub, TrustAnchorRegistry anchors, FakeTrustStore trust, ISecretProtector? secrets, FakeDeviceScopes scopes)
    {
        Scopes = scopes;
        Plugin = plugin;
        SettingsProvider = settings;
        Hub = hub;
        Anchors = anchors;
        Trust = trust;
        Secrets = secrets;
    }

    public PkiPlugin Plugin { get; }

    /// <summary>The device groups of the host.</summary>
    public FakeDeviceScopes Scopes { get; }

    public PkiService Service => Plugin.Service!;

    public InMemoryPluginSettingsProvider SettingsProvider { get; }

    public IPluginSettings Settings => SettingsProvider.GetSettings(PkiPluginInfo.PluginId);

    public PluginEventHub Hub { get; }

    public TrustAnchorRegistry Anchors { get; }

    public FakeTrustStore Trust { get; }

    public ISecretProtector? Secrets { get; }

    public static PkiOptions Options(FakeTrustStore trust, TimeProvider? time = null) => new()
    {
        CaKeySize = 2048,
        ServerName = "SERVER01",
        TrustStore = trust,
        Time = time ?? TimeProvider.System,
        CertificatesEventDelay = TimeSpan.Zero,
    };

    /// <summary>Starts the plugin; waits for the default CA unless <paramref name="waitForDefault"/> is false.</summary>
    public static async Task<PkiHarness> StartAsync(
        InMemoryPluginSettingsProvider? settings = null,
        ISecretProtector? secrets = null,
        bool noSecrets = false,
        TimeProvider? time = null,
        bool waitForDefault = true,
        MemoryPluginRecords? records = null,
        IReadOnlyList<IDeviceInfo>? devices = null,
        IEventPublisher? publisher = null,
        FakeSites? sites = null,
        bool pro = true)
    {
        settings ??= new InMemoryPluginSettingsProvider();
        var protector = noSecrets ? null : secrets ?? TestSecrets.Create();
        var hub = new PluginEventHub();
        var anchors = new TrustAnchorRegistry();
        var trust = new FakeTrustStore();
        var plugin = new PkiPlugin(Options(trust, time));
        var managed = new List<IDeviceInfo>(devices ?? []);
        var core = new TestCoreContext(settings.GetSettings(PkiPluginInfo.PluginId), protector, hub.For(PkiPluginInfo.PluginId), anchors.For(PkiPluginInfo.PluginId))
        {
            PluginRecords = records ?? new MemoryPluginRecords(),
            DeviceList = managed,
            EventPublisher = publisher,
            FakeSites = sites ?? new FakeSites(),
        };
        await plugin.StartAsync(core, CancellationToken.None);
        plugin.Service!.IsPro = () => pro; // the PRO servers' policies show (the license itself, and what it starts, is tested apart)
        if (waitForDefault)
        {
            await plugin.Service!.Background;
        }

        return new PkiHarness(plugin, settings, hub, anchors, trust, protector, core.Scopes) { Records = core.PluginRecords, Core = core, Managed = managed };
    }

    public async Task<T> InvokeAsync<T>(string method, object? payload = null) =>
        PkiJson.Deserialize<T>(await Plugin.InvokeAsync(method, payload is null ? null : PkiJson.Serialize(payload), CancellationToken.None));

    public Task<PkiState> StateAsync() => InvokeAsync<PkiState>(PkiMethods.GetState);

    /// <summary>The plugin records of the started plugin (issued certificates, revocation list).</summary>
    public MemoryPluginRecords Records { get; init; } = new();

    /// <summary>The plugin's context (sites, protected tags, services).</summary>
    public TestCoreContext Core { get; init; } = null!;

    /// <summary>PKI's policies.</summary>
    public Policies.PolicyStore Policies => Service.Policies;

    /// <summary>The managed devices (the host's device list); tests add and remove cameras.</summary>
    public List<IDeviceInfo> Managed { get; init; } = [];

    /// <summary>
    /// Adds certificates to the store as current ones (as a confirmed install does). <paramref name="manage"/>: OADM manages
    /// their cameras (the list shows only managed cameras); a camera not in the device list yet is added.
    /// </summary>
    public async Task SetIssuedAsync(IEnumerable<IssuedCertificate> issued, bool manage = true)
    {
        foreach (var entry in issued)
        {
            if (manage)
            {
                Manage(entry.DeviceId, entry.DeviceSerial);
            }

            await Service.Issued.AddAsync(entry, CancellationToken.None);
        }
    }

    /// <summary>OADM manages the camera (its id and serial; a serial made from the id when none is given).</summary>
    public IDeviceInfo Manage(Guid id, string? serial = null)
    {
        if (Managed.FirstOrDefault(d => d.Id == id) is { } known)
        {
            return known;
        }

        var n = Managed.Count;
        var device = new PkiFakeDevice(id, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"10.1.{n / 250}.{n % 250 + 1}"),
            serial ?? Convert.ToHexString(id.ToByteArray())[..12]);
        Managed.Add(device);
        return device;
    }

    public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
}

/// <summary>Test certificates and keys built in code (CertificateRequest); nothing touches an OS store.</summary>
internal static class TestCa
{
    public static X509Certificate2 Root(string cn = "Acme Root CA", int years = 20, RSA? rsa = null, DateTimeOffset? notBefore = null, bool ca = true, bool keyCertSign = true)
    {
        var key = rsa ?? RSA.Create(2048);
        var request = new CertificateRequest("CN=" + cn + ", O=Acme", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyCertSign ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var start = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        return request.CreateSelfSigned(start, start.AddYears(years));
    }

    public static X509Certificate2 Intermediate(X509Certificate2 root, string cn = "Acme Issuing CA", int years = 10)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + cn + ", O=Acme", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(years), RandomNumberGenerator.GetBytes(16).Select((b, i) => i == 0 ? (byte)(b & 0x7F | 1) : b).ToArray());
        return issued.CopyWithPrivateKey(key);
    }

    public static X509Certificate2 Leaf(X509Certificate2 issuer, string ip = "10.0.0.48")
    {
        using var key = RSA.Create(2048); // same algorithm as the RSA test CAs (CertificateRequest.Create needs it)
        var request = new CertificateRequest("CN=" + ip, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(san.Build());
        var notBefore = new DateTimeOffset(issuer.NotBefore.ToUniversalTime()).AddSeconds(1);
        var notAfter = new DateTimeOffset(issuer.NotAfter.ToUniversalTime()) < DateTimeOffset.UtcNow.AddDays(300) ? new DateTimeOffset(issuer.NotAfter.ToUniversalTime()) : DateTimeOffset.UtcNow.AddDays(300);
        using var issued = request.Create(issuer, notBefore, notAfter, [0x12, 0x34, 0x56]);
        return X509CertificateLoader.LoadCertificate(issued.RawData);
    }

    public static byte[] Pfx(string password, params X509Certificate2[] certificates)
    {
        var collection = new X509Certificate2Collection();
        collection.AddRange(certificates);
        return collection.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2000), password);
    }

    public static byte[] Pem(params X509Certificate2[] certificates) =>
        System.Text.Encoding.ASCII.GetBytes(string.Concat(certificates.Select(CaCertificates.ToPem)));

    public static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);
}

/// <summary>The page context against the in-process plugin (InvokeAsync) and the real event hub (WatchEventsAsync).</summary>
internal sealed class PluginPageContext(PkiPlugin plugin, PluginEventHub hub) : ICorePluginClientContext
{
    public List<(string Title, string Message)> Messages { get; } = [];

    public List<(string Title, string Message, string ConfirmText)> Confirmations { get; } = [];

    /// <summary>Answer of every confirmation.</summary>
    public bool ConfirmAnswer { get; set; } = true;

    public Func<string, string?, Task<string?>>? Intercept { get; set; }

    /// <summary>False: the user is limited to a device group.</summary>
    public bool HasAllDevices { get; set; } = true;

    /// <summary>False: the role lacks the plugin's "manage" permissions.</summary>
    public bool Manage { get; set; } = true;

    public bool Has(string permission) => Manage || !permission.EndsWith(":" + PermissionKeys.Manage, StringComparison.Ordinal);

    /// <summary>The devices this client knows (the policy editor's device picker).</summary>
    public IReadOnlyList<IDeviceInfo> DeviceList { get; set; } = [];

    private IReadOnlyList<CorePluginTab> _tabs = [];

    /// <summary>The server pages the host hands the page as tabs; a set raises TabsChanged.</summary>
    public IReadOnlyList<CorePluginTab> Tabs
    {
        get => _tabs;
        set
        {
            _tabs = value;
            TabsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? TabsChanged;

    public event EventHandler<string>? OpenTabRequested;

    /// <summary>The host asks for a tab (OpenAsync of a tab page or of PKI).</summary>
    public void RequestTab(string pluginId) => OpenTabRequested?.Invoke(this, pluginId);

    public List<string> Opened { get; } = [];

    private readonly Dictionary<string, string> _local = [];

    /// <summary>What the page keeps on this computer (shared by every page made with this context).</summary>
    public string? GetLocalSetting(string key) => _local.GetValueOrDefault(key);

    public void SetLocalSetting(string key, string? value)
    {
        if (value is null)
        {
            _local.Remove(key);
        }
        else
        {
            _local[key] = value;
        }
    }

    public Task OpenAsync(string hostPage)
    {
        Opened.Add(hostPage);
        return Task.CompletedTask;
    }

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        if (Intercept is { } intercept)
        {
            return await intercept(method, payloadJson);
        }

        return await plugin.InvokeAsync(method, payloadJson, ct);
    }

    /// <summary>The downloads of the page (Export of the Certificates card) from the in-process plugin.</summary>
    public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) => plugin.OpenDownloadAsync(method, payloadJson, ct);

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in hub.WatchAsync(PkiPluginInfo.PluginId, ct))
        {
            yield return item;
        }
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmAnswer);
    }
}

internal static class Wait
{
    public static Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        Oadm.Sdk.Testing.Eventually.TrueAsync(condition, "the condition", timeout);
}
