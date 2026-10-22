using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Core.Tests.Vapix;

/// <summary>Certificates built in code with <see cref="CertificateRequest"/>; nothing touches the OS store.</summary>
internal static class TestCertificates
{
    /// <summary>Self-signed leaf like the Axis default certificate, with an IP SAN.</summary>
    public static X509Certificate2 SelfSigned(string cn = "axis-b8a44f631339", string ip = "10.0.0.48", DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(San(ip));
        return request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(300));
    }

    /// <summary>A CA certificate (with private key) that is in no trust store.</summary>
    public static X509Certificate2 CreateCa(string cn = "OADM Test Root CA")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(10));
    }

    /// <summary>Leaf issued by <paramref name="ca"/>.</summary>
    public static X509Certificate2 IssuedBy(X509Certificate2 ca, string cn = "camera.example", string ip = "10.0.0.48", int days = 200)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(San(ip, cn));
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days), serial);
        return X509CertificateLoader.LoadCertificate(issued.RawData);
    }

    /// <summary>Server certificate issued by <paramref name="ca"/> with these DNS names.</summary>
    public static X509Certificate2 IssuedByWithNames(X509Certificate2 ca, params string[] names)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=camera", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(200), serial);
        return X509CertificateLoader.LoadCertificate(issued.RawData);
    }

    /// <summary>An intermediate CA issued by <paramref name="root"/>, with its key.</summary>
    public static X509Certificate2 CreateIntermediate(X509Certificate2 root, string cn)
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

    private static X509Extension San(string ip, string? dns = null)
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(ip));
        if (dns is not null)
        {
            san.AddDnsName(dns);
        }

        return san.Build();
    }
}
