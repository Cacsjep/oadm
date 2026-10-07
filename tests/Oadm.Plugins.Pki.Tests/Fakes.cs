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
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
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
    public IDeviceRepository Devices => throw new NotSupportedException();

    public IVapixClientFactory Vapix => throw new NotSupportedException();

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings { get; } = settings;

    public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public ISecretProtector? Secrets { get; } = secrets;

    public IPluginEvents? Events { get; } = events;

    public ITrustAnchors? TrustAnchors { get; } = anchors;
}

/// <summary>A started PKI plugin over in-memory settings (fast 2048-bit keys), with its hub and anchors.</summary>
internal sealed class PkiHarness : IAsyncDisposable
{
    private PkiHarness(PkiPlugin plugin, InMemoryPluginSettingsProvider settings, PluginEventHub hub, TrustAnchorRegistry anchors, FakeTrustStore trust, ISecretProtector? secrets)
    {
        Plugin = plugin;
        SettingsProvider = settings;
        Hub = hub;
        Anchors = anchors;
        Trust = trust;
        Secrets = secrets;
    }

    public PkiPlugin Plugin { get; }

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
    };

    /// <summary>Starts the plugin; waits for the default CA unless <paramref name="waitForDefault"/> is false.</summary>
    public static async Task<PkiHarness> StartAsync(
        InMemoryPluginSettingsProvider? settings = null,
        ISecretProtector? secrets = null,
        bool noSecrets = false,
        TimeProvider? time = null,
        bool waitForDefault = true)
    {
        settings ??= new InMemoryPluginSettingsProvider();
        var protector = noSecrets ? null : secrets ?? TestSecrets.Create();
        var hub = new PluginEventHub();
        var anchors = new TrustAnchorRegistry();
        var trust = new FakeTrustStore();
        var plugin = new PkiPlugin(Options(trust, time));
        await plugin.StartAsync(new TestCoreContext(settings.GetSettings(PkiPluginInfo.PluginId), protector, hub.For(PkiPluginInfo.PluginId), anchors.For(PkiPluginInfo.PluginId)), CancellationToken.None);
        if (waitForDefault)
        {
            await plugin.Service!.Background;
        }

        return new PkiHarness(plugin, settings, hub, anchors, trust, protector);
    }

    public async Task<T> InvokeAsync<T>(string method, object? payload = null) =>
        PkiJson.Deserialize<T>(await Plugin.InvokeAsync(method, payload is null ? null : PkiJson.Serialize(payload), CancellationToken.None));

    public Task<PkiState> StateAsync() => InvokeAsync<PkiState>(PkiMethods.GetState);

    /// <summary>Writes the issued registry the task plugins (part 2) will write.</summary>
    public Task SetIssuedAsync(IEnumerable<IssuedCertificate> issued) =>
        Settings.SetAsync(PkiStore.IssuedKey, PkiJson.Serialize(issued.ToList()), CancellationToken.None);

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

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        if (Intercept is { } intercept)
        {
            return await intercept(method, payloadJson);
        }

        return await plugin.InvokeAsync(method, payloadJson, ct);
    }

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
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
