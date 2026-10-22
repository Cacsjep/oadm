using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The OADM CA offered to other plugins (<see cref="IDeviceCertificateAuthority"/>, used by the EST server).</summary>
public sealed class CertificateAuthorityServiceTests
{
    [Fact]
    public async Task Signs_with_oadm_names_and_lists_the_device_certificate()
    {
        await using var pki = await PkiHarness.StartAsync();
        var authority = new PkiCertificateAuthority(pki.Service);
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=whatever the camera asks", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        var device = Guid.NewGuid();

        var issued = await authority.SignAsync(
            new DeviceCertificateRequest(csr, "axis-b8a44f631339", ["10.0.0.48"], ["axis-b8a44f631339.local"], DeviceCertificateUse.Server)
            {
                DeviceId = device,
                Source = "EST",
                Validity = TimeSpan.FromDays(30),
            },
            CancellationToken.None);

        Assert.True(authority.IsReady);
        using var certificate = X509CertificateLoader.LoadCertificate(issued.Certificate);
        Assert.Equal("CN=axis-b8a44f631339", certificate.Subject);
        Assert.Contains("axis-b8a44f631339.local", certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
        Assert.Equal(pki.Service.Ca!.Certificate.RawData, issued.Chain[0]);
        Assert.InRange((certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays, 29, 31);
        var listed = Assert.Single(await pki.Service.Issued.SnapshotAsync(CancellationToken.None));
        Assert.Equal((device, "EST", CertificatePurpose.Https, issued.SerialNumber), (listed.DeviceId, listed.Source, listed.Purpose, listed.SerialNumber));
    }

    [Fact]
    public async Task Refuses_sha1_requests_and_weak_keys()
    {
        await using var pki = await PkiHarness.StartAsync();
        var authority = new PkiCertificateAuthority(pki.Service);
        using var key = RSA.Create(2048);
        var sha1 = Sha1Request(key);
        using var weakKey = RSA.Create(1024);
        var weak = new CertificateRequest("CN=x", weakKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();

        var refused = await Assert.ThrowsAsync<CertificateRequestRefusedException>(() => authority.SignAsync(Request(sha1), CancellationToken.None));
        Assert.Contains("SHA-1", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CertificateRequestRefusedException>(() => authority.SignAsync(Request(weak), CancellationToken.None));
        await Assert.ThrowsAsync<CertificateRequestRefusedException>(() => authority.SignAsync(Request([0x30, 0x03, 0x02, 0x01, 0x00]), CancellationToken.None));
        Assert.Empty(await pki.Service.Issued.SnapshotAsync(CancellationToken.None));
    }

    /// <summary>A PKCS#10 request signed with sha1WithRSAEncryption (.NET no longer creates those).</summary>
    private static byte[] Sha1Request(RSA key)
    {
        var modern = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        var info = new AsnReader(modern, AsnEncodingRules.DER).ReadSequence().ReadEncodedValue().ToArray();
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(info);
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.2.840.113549.1.1.5");
                writer.WriteNull();
            }

            writer.WriteBitString(key.SignData(info, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        }

        return writer.Encode();
    }

    private static DeviceCertificateRequest Request(byte[] csr) =>
        new(csr, "axis-b8a44f631339", ["10.0.0.48"], [], DeviceCertificateUse.Client) { DeviceId = Guid.NewGuid() };
}
