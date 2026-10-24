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
public sealed class PkiCertificateAuthority(PkiService service) : IDeviceCertificateAuthority, ICertificateDelivery
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

    public event EventHandler? RevocationChanged
    {
        add => service.RevocationChanged += value;
        remove => service.RevocationChanged -= value;
    }

    public IReadOnlyList<IReadOnlyList<byte[]>> PreviousChains => service.PreviousChains();

    public bool IsRevoked(string serialNumber) => service.Issued.IsRevoked(serialNumber);

    /// <summary>
    /// EST wrote its answer with the certificate (docs/spec/pki.md "Current and pending"): HTTPS, RTSPS and MQTT certificates
    /// become current; an IEEE 802.1X one stays pending until the camera signs in to the 802.1X server with it.
    /// </summary>
    public async Task DeliveredAsync(string serialNumber, CancellationToken ct)
    {
        if (service.Issued.Find(serialNumber) is not { Pending: true } entry)
        {
            return;
        }

        if (string.Equals(entry.Purpose, DeviceCertificatePurposes.Dot1x, StringComparison.Ordinal))
        {
            service.Issued.MarkDelivered(serialNumber);
        }
        else
        {
            await service.Issued.ConfirmAsync(serialNumber, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The camera has the pending certificate (any use): it becomes current, the previous one is revoked.</summary>
    public Task<bool> ConfirmAsync(string serialNumber, CancellationToken ct) => service.Issued.ConfirmAsync(serialNumber, ct);

    /// <summary>EST could not write its answer: the camera never got the certificate (revoked as unused).</summary>
    public Task NotDeliveredAsync(string serialNumber, CancellationToken ct) => service.Issued.DiscardAsync(serialNumber, evenDelivered: false, ct);

    /// <summary>
    /// The 802.1X server let a camera in with this certificate: a pending IEEE 802.1X certificate is confirmed (the previous
    /// one is revoked). One lookup when nothing is pending.
    /// </summary>
    public Task SignedInAsync(string serialNumber, CancellationToken ct) =>
        service.Issued.Find(serialNumber) is { Pending: true, Purpose: DeviceCertificatePurposes.Dot1x }
            ? service.Issued.ConfirmAsync(serialNumber, ct)
            : Task.CompletedTask;

    /// <summary>The 802.1X server read its stored certificate at its start: the Certificates list shows it as used by that server.</summary>
    public void NoteServerCertificate(byte[] leaf, byte[] caCertificate) =>
        service.NoteServerCertificate(Certificates.ServerCertificateSources.Dot1x, leaf, Convert.ToHexString(SHA256.HashData(caCertificate)));

    /// <summary>
    /// What <see cref="PurposeOf"/> answers for a certificate created, signed or imported on the PKI page: no device
    /// certificate, whatever it names. EST and the 802.1X server refuse it (fail closed).
    /// </summary>
    public const string NotDevicePurpose = "not-device";

    /// <summary>The use of a listed device certificate; <see cref="NotDevicePurpose"/> for one of the Certificates list made here (first).</summary>
    public string? PurposeOf(string serialNumber) =>
        service.Local.HasSerial(serialNumber) ? NotDevicePurpose : service.Issued.Find(serialNumber)?.Purpose;

    public CertificateRevocation? FindRevocation(string serialNumber) => service.Issued.FindRevocation(serialNumber);

    public IReadOnlyList<CertificateRevocation> Revocations => service.Issued.Revocations;

    public IReadOnlyList<CertificateAuthorityInfo> Authorities => service.Authorities();

    public void SetUsedBy(string serviceId, string serviceName, IReadOnlyCollection<string> authorityIds) => service.SetUsedBy(serviceId, serviceName, authorityIds);

    public Task<IssuedCertificatePage> ListIssuedAsync(IssuedCertificateQuery query, CancellationToken ct) => service.QueryIssuedAsync(query, ct);

    public Task<byte[]?> GetIssuedCertificateAsync(string serialNumber, CancellationToken ct) => service.Issued.GetDerAsync(serialNumber, ct);

    public Task<RevokeResult> RevokeAsync(IReadOnlyCollection<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct) =>
        service.RevokeSerialsAsync(serialNumbers, reason, userName, ct);

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
                // The list of issued certificates: every device certificate, with its use (MQTT and RTSPS too) and DER.
                // EST certificates wait for the answer (DeliveredAsync); a caller that never confirms adds a current one.
                var serials = IssuedRegistry.AxisSerials(signed);
                var est = string.Equals(request.Source, IssuedCertificateSources.Est, StringComparison.OrdinalIgnoreCase);
                await service.Issued.AddPendingAsync(new IssuedCertificate
                {
                    SerialNumber = serial,
                    DeviceId = deviceId,
                    DeviceSerial = serials.Count == 1 ? serials[0] : null,
                    Purpose = DeviceCertificatePurposes.Of(request),
                    CaId = ca.Id,
                    NotBeforeUtc = signed.NotBefore.ToUniversalTime(),
                    NotAfterUtc = notAfter,
                    IssuedUtc = service.Time.GetUtcNow().UtcDateTime,
                    Source = request.Source,
                }, signed.RawData, ct).ConfigureAwait(false);
                if (!est)
                {
                    await service.Issued.ConfirmAsync(serial, ct).ConfigureAwait(false);
                }
            }
            else if (request.Use == DeviceCertificateUse.Server)
            {
                // A certificate of an OADM server (802.1X, EST): listed as used by it.
                service.NoteServerCertificate(request.Source, signed.RawData, ca.Id);
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
