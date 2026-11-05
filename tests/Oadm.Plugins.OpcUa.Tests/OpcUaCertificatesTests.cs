using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Opc.Ua;
using Opc.Ua.Security.Certificates;

using CertificateValidationResult = Opc.Ua.CertificateValidationResult;

namespace Oadm.Plugins.OpcUa.Tests;

/// <summary>Trust of the server's certificate, without a server and without a store.</summary>
public sealed class OpcUaCertificatesTests
{
    private static readonly Uri Endpoint = new("opc.tcp://plc.local:4840");

    private static X509Certificate2 Ca(string name = "CN=Plant CA")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(60));
    }

    private static Certificate Server(X509Certificate2 issuer, string host = "plc.local", int daysLeft = 30)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PLC", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        names.AddUri(new Uri("urn:plc"));
        request.CertificateExtensions.Add(names.Build());
        var serial = RandomNumberGenerator.GetBytes(8);
        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1 + Math.Min(daysLeft, 0)), DateTimeOffset.UtcNow.AddDays(daysLeft), serial);
        return Certificate.FromRawData(issued.RawData);
    }

    private static Task<CertificateValidationResult> CheckAsync(Certificate server, bool allowUntrusted, params X509Certificate2[] trusted)
    {
        using var manager = new OpcUaCertificates(Endpoint, allowUntrusted, trusted, null);
        return manager.ValidateAsync(server);
    }

    [Fact]
    public async Task AServerOfTheTrustedCaPasses()
    {
        using var ca = Ca();
        using var server = Server(ca);

        var result = await CheckAsync(server, allowUntrusted: false, ca);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task AServerOfAnotherCaIsRefused()
    {
        using var ca = Ca();
        using var other = Ca("CN=Other CA");
        using var server = Server(other);

        var result = await CheckAsync(server, allowUntrusted: false, ca);

        Assert.Equal(StatusCodes.BadCertificateUntrusted, result.StatusCode);
    }

    [Fact]
    public async Task WithoutATrustedCaTheRootsOfTheSystemDecide()
    {
        using var unknown = Ca("CN=Nobody knows this CA");
        using var server = Server(unknown);

        var result = await CheckAsync(server, allowUntrusted: false);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task AllowUntrustedAcceptsAnUnknownCa()
    {
        using var unknown = Ca("CN=Nobody knows this CA");
        using var server = Server(unknown);

        var result = await CheckAsync(server, allowUntrusted: true);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ACertificateForAnotherHostIsRefused()
    {
        using var ca = Ca();
        using var server = Server(ca, host: "other.local");

        var result = await CheckAsync(server, allowUntrusted: false, ca);

        Assert.Equal(StatusCodes.BadCertificateHostNameInvalid, result.StatusCode);
    }

    [Fact]
    public async Task AnImportedServerCertificateNeedsNoHostAndNoIssuer()
    {
        using var ca = Ca();
        using var server = Server(ca, host: "other.local");
        using var imported = X509CertificateLoader.LoadCertificate(server.RawData);

        var result = await CheckAsync(server, allowUntrusted: false, imported);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task AnExpiredCertificateIsRefused()
    {
        using var ca = Ca();
        using var server = Server(ca, daysLeft: -1);
        using var imported = X509CertificateLoader.LoadCertificate(server.RawData);

        Assert.Equal(StatusCodes.BadCertificateTimeInvalid, (await CheckAsync(server, allowUntrusted: false, ca)).StatusCode);
        Assert.Equal(StatusCodes.BadCertificateTimeInvalid, (await CheckAsync(server, allowUntrusted: false, imported)).StatusCode);
    }

    [Fact]
    public async Task TheIssuersTheServerSendsCompleteTheChain()
    {
        using var root = Ca("CN=Root");
        using var intermediateKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var intermediatePublic = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(40), RandomNumberGenerator.GetBytes(8));
        using var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);
        using var server = Server(intermediate);
        using var sent = Certificate.FromRawData(intermediate.RawData);
        using var chain = new CertificateCollection { server, sent };
        using var manager = new OpcUaCertificates(Endpoint, false, [root], null);

        var result = await manager.ValidateAsync(chain);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task TheCertificateOfALoginComesFromMemory()
    {
        using var own = Certificate.From(TestCertificates.Client());
        using var manager = new OpcUaCertificates(Endpoint, true, [], own);
        var identifier = new CertificateIdentifier { RawData = own.RawData };

        using var found = await manager.GetPrivateKeyCertificateAsync(identifier);
        using var byThumbprint = manager.TryGetPrivateKeyCertificate(own.Thumbprint);

        Assert.True(found!.HasPrivateKey);
        Assert.Equal(own.Thumbprint, byThumbprint!.Thumbprint);
        Assert.Null(manager.TryGetPrivateKeyCertificate("00"));
    }
}
