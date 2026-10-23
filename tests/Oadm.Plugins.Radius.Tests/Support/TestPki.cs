using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oadm.Plugins.Radius.Protocol.Tls;

namespace Oadm.Plugins.Radius.Tests.Support;

/// <summary>A certificate with its key, as the tests hand it to BouncyCastle (DER chain leaf first, PKCS#8 key).</summary>
public sealed record TestCertificate(IReadOnlyList<byte[]> Chain, byte[] Pkcs8, ServerKeyKind Kind)
{
    public byte[] Leaf => Chain[0];
}

/// <summary>
/// A small PKI made in memory: a CA ("OADM CA"), the server certificate of the 802.1X server and camera certificates
/// (ECDSA P-256 like the OADM CA issues; RSA for the RSA suites), plus a foreign CA and an expired camera certificate.
/// </summary>
public static class TestPki
{
    private static readonly Lazy<Issuer> OadmCa = new(() => CreateCa("OADM test CA"));
    private static readonly Lazy<Issuer> ForeignCa = new(() => CreateCa("Foreign CA"));
    private static readonly Lazy<TestCertificate> EcServer = new(() => Leaf(OadmCa.Value, "oadm-server", rsa: false, clientAuth: false));
    private static readonly Lazy<TestCertificate> RsaServer = new(() => Leaf(OadmCa.Value, "oadm-server-rsa", rsa: true, clientAuth: false));
    private static readonly Lazy<TestCertificate> Camera = new(() => Leaf(OadmCa.Value, "axis-accc8e123456.local", rsa: false, clientAuth: true));
    private static readonly Lazy<TestCertificate> RsaCamera = new(() => Leaf(OadmCa.Value, "axis-accc8e654321.local", rsa: true, clientAuth: true));
    private static readonly Lazy<TestCertificate> ForeignCamera = new(() => Leaf(ForeignCa.Value, "axis-accc8e999999.local", rsa: false, clientAuth: true));

    /// <summary>The OADM CA certificate (DER).</summary>
    public static byte[] CaCertificate => OadmCa.Value.Certificate.RawData;

    public static byte[] ForeignCaCertificate => ForeignCa.Value.Certificate.RawData;

    /// <summary>The 802.1X server's certificate (ECDSA P-256), chain leaf + CA.</summary>
    public static TestCertificate Server => EcServer.Value;

    public static TestCertificate ServerRsa => RsaServer.Value;

    /// <summary>A camera certificate of the OADM CA (ECDSA P-256).</summary>
    public static TestCertificate CameraCertificate => Camera.Value;

    public static TestCertificate CameraRsa => RsaCamera.Value;

    /// <summary>A camera certificate of another CA.</summary>
    public static TestCertificate ForeignCameraCertificate => ForeignCamera.Value;

    public static TlsServerIdentity ServerIdentity => TlsServerIdentity.Create(Server.Chain, Server.Pkcs8);

    public static TlsServerIdentity ServerIdentityRsa => TlsServerIdentity.Create(ServerRsa.Chain, ServerRsa.Pkcs8);

    /// <summary>A camera certificate with a long chain (several intermediates) to make the device's flight large.</summary>
    public static TestCertificate CameraWithLongChain(int intermediates)
    {
        var issuer = OadmCa.Value;
        List<byte[]> chain = [];
        var issuers = new List<Issuer>();
        for (var i = 0; i < intermediates; i++)
        {
            issuer = CreateIntermediate(issuer, $"Intermediate {i}");
            issuers.Add(issuer);
        }

        var leaf = Leaf(issuer, "axis-accc8e777777.local", rsa: true, clientAuth: true);
        chain.Add(leaf.Leaf);
        chain.AddRange(issuers.AsEnumerable().Reverse().Select(i => i.Certificate.RawData));
        return leaf with { Chain = chain };
    }

    private sealed record Issuer(X509Certificate2 Certificate, ECDsa Key);

    private static Issuer CreateCa(string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return new Issuer(certificate, key);
    }

    private static Issuer CreateIntermediate(Issuer parent, string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var unsigned = request.Create(parent.Certificate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(4), RandomNumberGenerator.GetBytes(12));
        var certificate = unsigned.CopyWithPrivateKey(key);
        return new Issuer(certificate, key);
    }

    private static TestCertificate Leaf(Issuer issuer, string name, bool rsa, bool clientAuth)
    {
        using var ec = rsa ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsaKey = rsa ? RSA.Create(2048) : null;
        var request = rsa
            ? new CertificateRequest($"CN={name}", rsaKey!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest($"CN={name}", ec!, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | (rsa ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.KeyAgreement), true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(clientAuth ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.Create(issuer.Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(issuer.Key), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), RandomNumberGenerator.GetBytes(12));
        var pkcs8 = rsa ? rsaKey!.ExportPkcs8PrivateKey() : ec!.ExportPkcs8PrivateKey();
        return new TestCertificate([certificate.RawData, issuer.Certificate.RawData], pkcs8, rsa ? ServerKeyKind.Rsa : ServerKeyKind.Ecdsa);
    }
}
