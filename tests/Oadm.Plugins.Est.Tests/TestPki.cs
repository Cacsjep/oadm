using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Est.Tests;

/// <summary>Test certificates: a device ID hierarchy like Axis' (root, intermediate, device) and a fake OADM CA.</summary>
internal static class TestPki
{
    public static X509Certificate2 Root(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(30));
    }

    public static X509Certificate2 Intermediate(X509Certificate2 issuer, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddYears(20), Serial());
        return signed.CopyWithPrivateKey(key);
    }

    /// <summary>A device ID certificate with the camera's serial in the subject (X.520 serialNumber), with its key.</summary>
    public static X509Certificate2 DeviceId(X509Certificate2 issuer, string serial, int days = 3650)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddOrganizationName("Axis Communications AB");
        name.Add("2.5.4.5", serial);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days), Serial());
        using var withKey = signed.CopyWithPrivateKey(key);
        return Usable(withKey);
    }

    /// <summary>Through PKCS#12, so TLS can use the key on every OS.</summary>
    public static X509Certificate2 Usable(X509Certificate2 withKey) =>
        X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);

    /// <summary>A PKCS#10 request (DER) and its key.</summary>
    public static (byte[] Csr, ECDsa Key) Request(string subject = "CN=camera")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (new CertificateRequest(subject, key, HashAlgorithmName.SHA256).CreateSigningRequest(), key);
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        return serial;
    }
}

/// <summary>The OADM CA as the PKI plugin offers it, for tests: signs with OADM's names, records every request.</summary>
internal sealed class FakeAuthority : IDeviceCertificateAuthority, IDisposable
{
    private readonly X509Certificate2 _ca = TestPki.Root("OADM Test CA " + Guid.NewGuid().ToString("N")[..8]); // unique: Windows caches chains by name

    public bool IsReady { get; set; } = true;

    public IReadOnlyList<byte[]> Chain => [_ca.RawData];

    public X509Certificate2 Certificate => _ca;

    public List<DeviceCertificateRequest> Requests { get; } = [];

    public event EventHandler? Changed;

    public Task<IssuedDeviceCertificate> SignAsync(DeviceCertificateRequest request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequest(request.Csr.ToArray(), HashAlgorithmName.SHA256);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateRequestRefusedException("The certificate request cannot be read.", ex);
        }

        var signing = new CertificateRequest(new X500DistinguishedName("CN=" + request.CommonName), csr.PublicKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var ip in request.IpAddresses)
        {
            san.AddIpAddress(System.Net.IPAddress.Parse(ip));
        }

        foreach (var dns in request.DnsNames)
        {
            san.AddDnsName(dns);
        }

        signing.CertificateExtensions.Add(san.Build());
        signing.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signing.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(request.Use == DeviceCertificateUse.Client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], false));
        signing.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca, true, false));
        var serial = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
        var notAfter = DateTimeOffset.UtcNow + (request.Validity ?? TimeSpan.FromDays(365));
        using var signed = signing.Create(_ca, DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, serial);
        return Task.FromResult(new IssuedDeviceCertificate(signed.RawData, Chain, signed.SerialNumber.ToUpperInvariant(), signed.NotAfter.ToUniversalTime()));
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _ca.Dispose();
}
