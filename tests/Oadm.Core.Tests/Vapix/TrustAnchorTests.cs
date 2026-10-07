using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Plugins;
using Oadm.Core.Vapix;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Vapix;

/// <summary>Extra trust anchors (PKI plugin CA): registry, evaluator, pinning and the core plugin context.</summary>
public sealed class TrustAnchorTests
{
    [Fact]
    public void LeafOfAnAnchoredCaIsTrusted()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.IssuedBy(ca);
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.RawData);

        Assert.Equal(CertificateTrust.Untrusted, CertificateTrustEvaluator.EvaluateChain(leaf));
        Assert.Equal(CertificateTrust.Trusted, CertificateTrustEvaluator.EvaluateChain(leaf, trustAnchors: [caPublic]));

        // The device may also send the CA itself in the handshake.
        Assert.Equal(CertificateTrust.Trusted, CertificateTrustEvaluator.EvaluateChain(leaf, [leaf, caPublic], trustAnchors: [caPublic]));
    }

    [Fact]
    public void LeafOfAnotherCaStaysUntrusted()
    {
        using var ca = TestCertificates.CreateCa("Device CA");
        using var other = TestCertificates.CreateCa("Other CA");
        using var leaf = TestCertificates.IssuedBy(ca);
        using var otherPublic = X509CertificateLoader.LoadCertificate(other.RawData);

        Assert.Equal(CertificateTrust.Untrusted, CertificateTrustEvaluator.EvaluateChain(leaf, trustAnchors: [otherPublic]));
    }

    [Fact]
    public void SelfSignedCertificatesStaySelfSignedEvenWhenAnchored()
    {
        using var cert = TestCertificates.SelfSigned();
        using var copy = X509CertificateLoader.LoadCertificate(cert.RawData);

        Assert.Equal(CertificateTrust.SelfSigned, CertificateTrustEvaluator.EvaluateChain(cert, trustAnchors: [copy]));
    }

    [Fact]
    public void WithoutAnchorsTheResultIsUnchanged()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.IssuedBy(ca);

        Assert.Equal(CertificateTrust.Untrusted, CertificateTrustEvaluator.EvaluateChain(leaf, trustAnchors: []));
        Assert.Equal(CertificateTrust.Untrusted, CertificateTrustEvaluator.Describe(leaf, host: "10.0.0.48").ChainTrust);
    }

    [Fact]
    public void LeafOfAnAnchoredIntermediateWithItsRootIsTrusted()
    {
        using var root = TestCertificates.CreateCa("Customer Root CA");
        using var intermediate = CreateIntermediate(root, "Customer Issuing CA");
        using var leaf = TestCertificates.IssuedBy(intermediate);
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);
        using var intermediatePublic = X509CertificateLoader.LoadCertificate(intermediate.RawData);

        Assert.Equal(CertificateTrust.Untrusted, CertificateTrustEvaluator.EvaluateChain(leaf));
        Assert.Equal(CertificateTrust.Trusted, CertificateTrustEvaluator.EvaluateChain(leaf, trustAnchors: [intermediatePublic, rootPublic]));
        Assert.Equal(CertificateTrust.Trusted, CertificateTrustEvaluator.EvaluateChain(leaf, [intermediatePublic], trustAnchors: [rootPublic]));
    }

    [Fact]
    public void RegistryKeepsOneSetPerOwnerAndTrustsTheUnion()
    {
        using var a = TestCertificates.CreateCa("A");
        using var b = TestCertificates.CreateCa("B");
        var registry = new TrustAnchorRegistry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;
        Assert.True(registry.Current.IsEmpty);
        Assert.Equal(0, registry.Current.Version);

        registry.For("oadm.pki").Set([a.RawData]);
        registry.For("other").Set([b.RawData, a.RawData]);
        Assert.Equal(2, registry.Current.Certificates.Count); // distinct by thumbprint
        Assert.Equal(2, registry.Current.Version);

        registry.For("oadm.pki").Set([b.RawData]); // replaces the plugin's own set only
        Assert.Equal(2, registry.Current.Certificates.Count);
        registry.Remove("other");
        Assert.Equal(b.Thumbprint, Assert.Single(registry.Current.Certificates).Thumbprint);
        registry.Remove("unknown"); // nothing to remove: no new version
        Assert.Equal(4, registry.Current.Version);
        Assert.Equal(4, changes);

        Assert.Throws<ArgumentException>(() => registry.For("x").Set([[1, 2, 3]]));
    }

    [Fact]
    public void PinningRatesAgainstTheRegistryAndFollowsChangesLazily()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.IssuedBy(ca);
        var registry = new TrustAnchorRegistry();
        var pinning = new CertificatePinning { TrustAnchors = registry };

        Assert.True(pinning.Validate(leaf, null, "10.0.0.48"));
        Assert.Equal(CertificateTrust.Untrusted, pinning.ObservedCertificate!.ChainTrust);

        // The CA is added later: the next read rates the same handshake again, no new connection needed.
        registry.For("oadm.pki").Set([ca.RawData]);
        Assert.Equal(CertificateTrust.Trusted, pinning.ObservedCertificate!.ChainTrust);

        registry.Remove("oadm.pki");
        Assert.Equal(CertificateTrust.Untrusted, pinning.ObservedCertificate!.ChainTrust);
    }

    [Fact]
    public async Task CorePluginContextExposesThePluginsSetAndStopRemovesIt()
    {
        using var ca = TestCertificates.CreateCa();
        var registry = new TrustAnchorRegistry();
        var plugin = new AnchorPlugin(ca.RawData);
        var pluginRegistry = new PluginRegistry();
        Assert.True(pluginRegistry.RegisterCorePlugin(plugin, new PluginOrigin("test", "1.0.0", null)));
        await using var host = new CorePluginHost(
            pluginRegistry,
            new Tasks.FakeDeviceRepository(),
            new Tasks.FakeVapixClientFactory(),
            new NoTasks(),
            new InMemoryPluginSettingsProvider(),
            trustAnchors: registry);

        await host.StartAllAsync(CancellationToken.None);
        Assert.NotNull(plugin.Anchors);
        Assert.Equal(ca.Thumbprint, Assert.Single(registry.Current.Certificates).Thumbprint);

        await host.StopAllAsync(CancellationToken.None);
        Assert.True(registry.Current.IsEmpty);
    }

    [Fact]
    public void ContextsWithoutSupportReturnNull()
    {
        ICorePluginContext ctx = new MinimalContext();
        Assert.Null(ctx.TrustAnchors);
    }

    private static X509Certificate2 CreateIntermediate(X509Certificate2 root, string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(5), serial);
        return issued.CopyWithPrivateKey(key);
    }

    private sealed class AnchorPlugin(byte[] der) : ICorePlugin
    {
        public ITrustAnchors? Anchors { get; private set; }

        public string Id => "oadm.pki";

        public string DisplayName => "PKI";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Anchors = ctx.TrustAnchors;
            Anchors?.Set([der]);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class NoTasks : Sdk.Tasks.ITaskRunner
    {
        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class MinimalContext : ICorePluginContext
    {
        public Sdk.Devices.IDeviceRepository Devices => throw new NotSupportedException();

        public Sdk.Vapix.IVapixClientFactory Vapix => throw new NotSupportedException();

        public Sdk.Tasks.ITaskRunner Tasks => throw new NotSupportedException();

        public IPluginSettings Settings => throw new NotSupportedException();

        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }
}
