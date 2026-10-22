using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// The OADM CA for other plugins (<see cref="IDeviceCertificateAuthority"/>, offered through the plugin services while PKI
/// runs): signs with <see cref="DeviceCertificateIssuer"/> like PKI's own tasks and lists device certificates in the issued
/// registry. Refuses requests signed with SHA-1 or MD5.
/// </summary>
public sealed class PkiCertificateAuthority(PkiService service) : IDeviceCertificateAuthority
{
    /// <summary>Signature algorithms of a request that are refused (MD2, MD5, SHA-1 with RSA, DSA, ECDSA).</summary>
    private static readonly HashSet<string> WeakSignatures =
    [
        "1.2.840.113549.1.1.2", // md2WithRSAEncryption
        "1.2.840.113549.1.1.4", // md5WithRSAEncryption
        "1.2.840.113549.1.1.5", // sha1WithRSAEncryption
        "1.3.14.3.2.29", // sha1WithRSASignature (OIW)
        "1.2.840.10040.4.3", // dsa-with-sha1
        "1.2.840.10045.4.1", // ecdsa-with-SHA1
    ];

    public bool IsReady => service.Ca is { HasKey: true };

    public IReadOnlyList<byte[]> Chain => service.Ca is { } ca ? [ca.Certificate.RawData, .. ca.Chain.Select(c => c.RawData)] : [];

    public event EventHandler? Changed
    {
        add => service.CaChanged += value;
        remove => service.CaChanged -= value;
    }

    public async Task<IssuedDeviceCertificate> SignAsync(DeviceCertificateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (service.Ca is not { HasKey: true } ca)
        {
            throw new CertificateRequestRefusedException("The PKI has no certificate authority. Generate or import one first.");
        }

        CheckSignatureAlgorithm(request.Csr);
        foreach (var ip in request.IpAddresses)
        {
            if (!IPAddress.TryParse(ip, out _))
            {
                throw new CertificateRequestRefusedException($"\"{ip}\" is not an IP address.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.CommonName))
        {
            throw new CertificateRequestRefusedException("The certificate needs a name.");
        }

        var names = new CertificateNames(request.CommonName.Trim(), request.IpAddresses, request.DnsNames);
        var purpose = request.Use == DeviceCertificateUse.Client ? CertificatePurpose.Dot1x : CertificatePurpose.Https;
        var csrPem = new string(PemEncoding.Write("CERTIFICATE REQUEST", request.Csr.Span));
        var validity = request.Validity is { } wanted && wanted > TimeSpan.Zero ? wanted : service.DeviceCertificateValidity();
        X509Certificate2 signed;
        try
        {
            signed = DeviceCertificateIssuer.Sign(ca, csrPem, names, purpose, service.Time.GetUtcNow(), validity);
        }
        catch (CertificateRequestRejectedException ex)
        {
            throw new CertificateRequestRefusedException(ex.Message, ex);
        }

        using (signed)
        {
            var (_, serial, notAfter) = DeviceCertificateIssuer.Describe(signed);
            if (request.DeviceId is { } deviceId)
            {
                await service.Issued.AddAsync(new IssuedCertificate
                {
                    SerialNumber = serial,
                    DeviceId = deviceId,
                    Purpose = purpose,
                    CaId = ca.Id,
                    NotAfterUtc = notAfter,
                    IssuedUtc = service.Time.GetUtcNow().UtcDateTime,
                    Source = request.Source,
                }, ct).ConfigureAwait(false);
            }

            return new IssuedDeviceCertificate(signed.RawData, [ca.Certificate.RawData, .. ca.Chain.Select(c => c.RawData)], serial, notAfter);
        }
    }

    /// <summary>Reads the request's signature algorithm (PKCS#10: info, algorithm, signature) and refuses weak ones.</summary>
    internal static void CheckSignatureAlgorithm(ReadOnlyMemory<byte> csr)
    {
        string algorithm;
        try
        {
            var outer = new AsnReader(csr, AsnEncodingRules.DER).ReadSequence();
            outer.ReadEncodedValue(); // certificationRequestInfo
            algorithm = outer.ReadSequence().ReadObjectIdentifier();
        }
        catch (AsnContentException ex)
        {
            throw new CertificateRequestRefusedException("The certificate request cannot be read.", ex);
        }

        if (WeakSignatures.Contains(algorithm))
        {
            throw new CertificateRequestRefusedException("The certificate request is signed with SHA-1 or MD5; SHA-256 or stronger is needed.");
        }
    }
}
