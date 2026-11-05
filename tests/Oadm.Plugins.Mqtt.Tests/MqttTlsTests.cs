using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Mqtt;

namespace Oadm.Plugins.Mqtt.Tests;

/// <summary>Certificates made for one test, all in memory.</summary>
internal static class TestCertificates
{
    public static X509Certificate2 Ca(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(60));
    }

    /// <summary>A leaf of <paramref name="ca"/> (null: self-signed) for a DNS name or IP address.</summary>
    public static X509Certificate2 Leaf(X509Certificate2? ca, string name = "127.0.0.1", int daysLeft = 30)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(name, out var address))
        {
            names.AddIpAddress(address);
        }
        else
        {
            names.AddDnsName(name);
        }

        request.CertificateExtensions.Add(names.Build());
        var to = DateTimeOffset.UtcNow.AddDays(daysLeft);
        var from = to.AddDays(-2) < DateTimeOffset.UtcNow.AddDays(-1) ? to.AddDays(-2) : DateTimeOffset.UtcNow.AddDays(-1);
        if (ca is null)
        {
            return request.CreateSelfSigned(from, to);
        }

        using var signed = request.Create(ca, from, to, RandomNumberGenerator.GetBytes(8));
        return signed.CopyWithPrivateKey(key);
    }

    public static byte[] Pfx(X509Certificate2 certificate) => certificate.Export(X509ContentType.Pkcs12);

    /// <summary>The certificate as PKI hands it out: the key only in memory.</summary>
    public static X509Certificate2 Ephemeral(X509Certificate2 certificate) => X509CertificateLoader.LoadPkcs12(
        Pfx(certificate),
        null,
        X509KeyStorageFlags.Exportable | (OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet));

    public static IReadOnlyDictionary<string, IReadOnlyList<X509Certificate2>> Field(string key, params X509Certificate2[] chain) =>
        new Dictionary<string, IReadOnlyList<X509Certificate2>> { [key] = chain };
}

/// <summary>TLS against the in-process broker: every test ends in a publish or in a refused connect.</summary>
public sealed class MqttTlsTests
{
    private static async Task PublishesAsync(MqttSession session, TestBroker broker)
    {
        await session.ConnectAsync(Sessions.Patient);
        await session.PublishAsync(new MqttMessage("t", "p", 1, false), Sessions.Patient);
        Assert.Single(broker.Messages);
    }

    [Fact]
    public async Task ABrokerTheSystemDoesNotTrustIsRefused()
    {
        using var certificate = TestCertificates.Leaf(null);
        await using var broker = await TestBroker.StartTlsAsync(certificate);
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true }));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Sessions.Patient));
        Assert.Equal(MqttCertificateCheck.NotTrusted, refused.Message);
    }

    [Fact]
    public async Task AllowUntrustedAccepts()
    {
        using var certificate = TestCertificates.Leaf(null, "wrong.example");
        await using var broker = await TestBroker.StartTlsAsync(certificate);
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true, allowUntrusted = true }));
        await PublishesAsync(session, broker);
    }

    [Fact]
    public async Task TheTrustedCaAccepts()
    {
        using var ca = TestCertificates.Ca("Trusted");
        using var leaf = TestCertificates.Leaf(ca);
        await using var broker = await TestBroker.StartTlsAsync(leaf);
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true }, TestCertificates.Field("trustedCa", ca)));
        await PublishesAsync(session, broker);
    }

    [Fact]
    public async Task ASelfSignedBrokerCanBeItsOwnTrustedCertificate()
    {
        using var certificate = TestCertificates.Leaf(null);
        await using var broker = await TestBroker.StartTlsAsync(certificate);
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true }, TestCertificates.Field("trustedCa", certificate)));
        await PublishesAsync(session, broker);
    }

    [Fact]
    public async Task AnotherCaIsRefused()
    {
        using var ca = TestCertificates.Ca("Trusted");
        using var other = TestCertificates.Ca("Other");
        using var leaf = TestCertificates.Leaf(other);
        await using var broker = await TestBroker.StartTlsAsync(leaf);
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true }, TestCertificates.Field("trustedCa", ca)));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Sessions.Patient));
        Assert.Equal(MqttCertificateCheck.NotFromTrustedCa, refused.Message);
    }

    [Fact]
    public async Task TheTrustedCaStillNeedsTheHostName()
    {
        using var ca = TestCertificates.Ca("Trusted");
        using var leaf = TestCertificates.Leaf(ca, "other.example");
        await using var broker = await TestBroker.StartTlsAsync(leaf);
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true }, TestCertificates.Field("trustedCa", ca)));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Sessions.Patient));
        Assert.Equal(MqttCertificateCheck.WrongName, refused.Message);
    }

    [Fact]
    public async Task AClientCertificateReachesTheBroker()
    {
        using var ca = TestCertificates.Ca("Trusted");
        using var leaf = TestCertificates.Leaf(ca);
        using var client = TestCertificates.Leaf(ca, "client.example");
        using var inMemory = TestCertificates.Ephemeral(client);
        string? seen = null;
        await using var broker = await TestBroker.StartTlsAsync(leaf, askClientCertificate: true, server => server.ValidatingConnectionAsync += e =>
        {
            seen = e.ClientCertificate?.Thumbprint;
            return Task.CompletedTask;
        });
        var certificates = new Dictionary<string, IReadOnlyList<X509Certificate2>> { ["trustedCa"] = [ca], ["clientCertificate"] = [inMemory, ca] };
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true }, certificates));
        await PublishesAsync(session, broker);
        Assert.Equal(client.Thumbprint, seen);
    }

    [Fact]
    public async Task ABrokerThatWantsAClientCertificateSaysSoAtTheConnect()
    {
        using var ca = TestCertificates.Ca("Trusted");
        using var leaf = TestCertificates.Leaf(ca);
        await using var broker = await TestBroker.StartTlsAsync(leaf, askClientCertificate: true);
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true }, TestCertificates.Field("trustedCa", ca)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Sessions.Patient));
    }

    [Fact]
    public void AnExpiredCertificateSaysSo()
    {
        using var ca = TestCertificates.Ca("Trusted");
        using var leaf = TestCertificates.Leaf(ca, daysLeft: -1);
        Assert.Equal(MqttCertificateCheck.Expired, MqttCertificateCheck.Problem(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, false, [ca]));
    }

    [Fact]
    public void WithoutAnyCheckNothingPassesThatTheSystemRefused()
    {
        using var leaf = TestCertificates.Leaf(null);
        Assert.Null(MqttCertificateCheck.Problem(leaf, null, SslPolicyErrors.None, false, []));
        Assert.Equal(MqttCertificateCheck.NotTrusted, MqttCertificateCheck.Problem(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, false, []));
        Assert.Equal(MqttCertificateCheck.WrongName, MqttCertificateCheck.Problem(leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch, false, []));
        Assert.Equal(MqttCertificateCheck.NoCertificate, MqttCertificateCheck.Problem(null, null, SslPolicyErrors.RemoteCertificateNotAvailable, false, []));
        Assert.Null(MqttCertificateCheck.Problem(null, null, SslPolicyErrors.RemoteCertificateNotAvailable, true, []));
    }

    [Fact]
    public void AnIntermediateTheBrokerSendsCompletesTheChain()
    {
        using var root = TestCertificates.Ca("Root");
        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Intermediate", intermediateKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var intermediatePublic = request.Create(root, DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(40), RandomNumberGenerator.GetBytes(8));
        using var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);
        using var leaf = TestCertificates.Leaf(intermediate);
        using var presented = new X509Chain();
        presented.ChainPolicy.ExtraStore.Add(intermediate);

        Assert.Null(MqttCertificateCheck.Problem(leaf, presented, SslPolicyErrors.RemoteCertificateChainErrors, false, [root]));
        Assert.Equal(MqttCertificateCheck.NotFromTrustedCa, MqttCertificateCheck.Problem(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, false, [root]));
    }
}
