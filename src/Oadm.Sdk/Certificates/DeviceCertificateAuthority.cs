namespace Oadm.Sdk.Certificates;

/// <summary>What a device certificate is for: its extended key usage.</summary>
public enum DeviceCertificateUse
{
    /// <summary>A server certificate (HTTPS, RTSPS, MQTT over TLS): serverAuth.</summary>
    Server = 0,

    /// <summary>A client certificate (IEEE 802.1X, MQTT client): clientAuth.</summary>
    Client = 1,
}

/// <summary>
/// A certificate request for <see cref="IDeviceCertificateAuthority.SignAsync"/>. The subject and alternative names come
/// from OADM, never from the request; the request only gives the public key and proves it holds the private key.
/// </summary>
/// <param name="Csr">The PKCS#10 request, DER.</param>
/// <param name="CommonName">The subject's common name.</param>
/// <param name="IpAddresses">IP addresses for the alternative names.</param>
/// <param name="DnsNames">Host names for the alternative names, e.g. <c>axis-&lt;serial&gt;.local</c>.</param>
/// <param name="Use">Server or client certificate.</param>
public sealed record DeviceCertificateRequest(
    ReadOnlyMemory<byte> Csr,
    string CommonName,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> DnsNames,
    DeviceCertificateUse Use)
{
    /// <summary>The managed device the certificate is for; it is listed with the device's certificates. Null for OADM's own.</summary>
    public Guid? DeviceId { get; init; }

    /// <summary>How long it is valid; null = the CA's setting for device certificates. Always ends with the CA.</summary>
    public TimeSpan? Validity { get; init; }

    /// <summary>Who asked for it, for the CA's list ("EST"); null = the CA's own tasks.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// The use in the list of issued certificates (SDK 0.4.0, one of <see cref="DeviceCertificatePurposes"/>: HTTPS, IEEE
    /// 802.1X, MQTT, RTSPS); null = from <see cref="Use"/> (HTTPS for a server, IEEE 802.1X for a client certificate).
    /// </summary>
    public string? Purpose { get; init; }

    /// <summary>
    /// The CA of PKI's list that signs (SDK 0.4.0, a <see cref="CertificateAuthorityInfo.Id"/>: the OADM CA or an imported CA
    /// with its key, the CA of the caller's policy); null = the OADM CA. A CA that cannot issue is refused.
    /// </summary>
    public string? IssuerId { get; init; }
}

/// <summary>A signed certificate.</summary>
/// <param name="Certificate">The certificate, DER.</param>
/// <param name="Chain">The CA certificate and the certificates above it, DER, issuing CA first.</param>
/// <param name="SerialNumber">The serial number, upper-case hex.</param>
/// <param name="NotAfterUtc">The end of validity.</param>
public sealed record IssuedDeviceCertificate(byte[] Certificate, IReadOnlyList<byte[]> Chain, string SerialNumber, DateTime NotAfterUtc);

/// <summary>The request was refused: unreadable, wrong signature, a weak key or digest, or the CA cannot sign.</summary>
public sealed class CertificateRequestRefusedException : Exception
{
    /// <summary>Without a reason.</summary>
    public CertificateRequestRefusedException()
    {
    }

    /// <summary>With the plain reason.</summary>
    public CertificateRequestRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>With the plain reason and the error behind it.</summary>
    public CertificateRequestRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The OADM CA, offered by the PKI plugin through <see cref="Plugins.IPluginServices"/>: one CA signs every device
/// certificate. Keys RSA 2048 or more, or EC P-256 or more; requests signed with SHA-1 or MD5 are refused.
/// </summary>
public interface IDeviceCertificateAuthority
{
    /// <summary>A CA with its private key exists, so <see cref="SignAsync"/> can sign.</summary>
    bool IsReady { get; }

    /// <summary>The CA certificate and the certificates above it, DER, issuing CA first; empty without a CA.</summary>
    IReadOnlyList<byte[]> Chain { get; }

    /// <summary>Raised after the CA was generated, imported or replaced. Runs on a server thread; keep it short.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Signs a request. Throws <see cref="CertificateRequestRefusedException"/> with a plain reason. A certificate with a
    /// <see cref="DeviceCertificateRequest.DeviceId"/> goes into the list of issued certificates.
    /// </summary>
    Task<IssuedDeviceCertificate> SignAsync(DeviceCertificateRequest request, CancellationToken ct);

    // ------------------------------------------------------------------ SDK 0.4.0: issued certificates and revocation

    /// <summary>
    /// True when the certificate with <paramref name="serialNumber"/> is on the revocation list (any notation, see
    /// <see cref="CertificateSerials.Normalize"/>). A hash lookup in memory: ask it for every authentication or renewal.
    /// Default (a CA without a list): false.
    /// </summary>
    bool IsRevoked(string serialNumber) => false;

    /// <summary>The revocation of <paramref name="serialNumber"/>, null when it is not revoked (for the refusal's text).</summary>
    CertificateRevocation? FindRevocation(string serialNumber) => null;

    /// <summary>
    /// The use (<see cref="DeviceCertificatePurposes"/>) of the certificate with <paramref name="serialNumber"/> when the list
    /// of issued certificates holds it (any notation), null when it does not. A lookup in memory: ask it for every
    /// authentication (the 802.1X server refuses a listed certificate of another use). Default (a CA without a list): null.
    /// </summary>
    string? PurposeOf(string serialNumber) => null;

    /// <summary>
    /// The previous OADM CAs (replaced, kept while their device certificates may still be valid), newest first: each one as
    /// DER, the CA certificate first, then the certificates above it. A server that checks device certificates (the 802.1X
    /// server) trusts them next to <see cref="Chain"/>. They change when a CA is replaced or a previous one removed: read
    /// them again after <see cref="Changed"/> and now and then. Default: none.
    /// </summary>
    IReadOnlyList<IReadOnlyList<byte[]>> PreviousChains => [];

    /// <summary>The revocation list: revoked certificates that could still be valid, newest first.</summary>
    IReadOnlyList<CertificateRevocation> Revocations => [];

    /// <summary>
    /// Raised after certificates were revoked or left the revocation list. Runs on a server thread; keep it short. The event
    /// belongs to this offer: subscribe again when <see cref="Plugins.IPluginServices.Find{T}"/> returns another instance
    /// (PKI restarted), and ask <see cref="IsRevoked"/> of the current offer.
    /// </summary>
    event EventHandler? RevocationChanged
    {
        add { }
        remove { }
    }

    /// <summary>One page of the list of issued certificates (filters, search, newest first). Default: an empty page.</summary>
    Task<IssuedCertificatePage> ListIssuedAsync(IssuedCertificateQuery query, CancellationToken ct) => Task.FromResult(IssuedCertificatePage.Empty);

    /// <summary>The certificate (DER) with <paramref name="serialNumber"/> when the list holds it; null otherwise.</summary>
    Task<byte[]?> GetIssuedCertificateAsync(string serialNumber, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    /// <summary>
    /// Puts the certificates on the revocation list (no undo; only for users with all devices: the caller checks). Expired
    /// ones are counted, never added; a serial the list does not hold (a certificate of the CA from before the list) is added
    /// without a device. Raises <see cref="RevocationChanged"/> once. Default: not supported.
    /// </summary>
    Task<RevokeResult> RevokeAsync(IReadOnlyCollection<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct) =>
        throw new NotSupportedException("This certificate authority keeps no revocation list.");

    /// <summary>
    /// Every CA PKI holds (docs/spec/pki.md, SDK 0.4.0): the current OADM CA, previous ones, the Axis device ID and imported
    /// CAs. Services select from it by <see cref="CertificateAuthorityInfo.Id"/> (stable across restarts). Changes raise
    /// <see cref="Changed"/>. Default: none.
    /// </summary>
    IReadOnlyList<CertificateAuthorityInfo> Authorities => [];

    /// <summary>
    /// Tells PKI which of its CAs <paramref name="serviceName"/> (shown as "Used by") uses now; an empty list clears it. PKI
    /// refuses to remove a CA a service uses. Default: nothing.
    /// </summary>
    void SetUsedBy(string serviceId, string serviceName, IReadOnlyCollection<string> authorityIds)
    {
    }
}

/// <summary>The kind of a CA PKI holds.</summary>
public enum CertificateAuthorityKind
{
    /// <summary>The OADM CA that issues now (id "oadm").</summary>
    Oadm,

    /// <summary>A replaced OADM CA, kept while its certificates live (id "oadm-previous:&lt;sha256&gt;").</summary>
    PreviousOadm,

    /// <summary>The Axis device ID roots of the factory certificates (id "axis-device-id"; several certificates).</summary>
    AxisDeviceId,

    /// <summary>A CA an administrator imported, its certificate only or with its key (id "imported:&lt;sha256&gt;").</summary>
    Imported,
}

/// <summary>
/// One CA of PKI's list: a stable id, the name shown, its kind, its certificates (DER; the Axis device ID has its roots and
/// intermediates; an imported intermediate its chain), the end of the latest of them, and whether it can issue certificates.
/// </summary>
/// <param name="Id">Stable id ("oadm", "oadm-previous:&lt;sha256&gt;", "axis-device-id", "imported:&lt;sha256&gt;").</param>
/// <param name="Name">The name shown ("OADM CA", "Axis device ID").</param>
/// <param name="Kind">The kind of CA.</param>
/// <param name="Certificates">The CA's certificates as DER, the CA certificate first.</param>
/// <param name="NotAfterUtc">The end of the latest of them (UTC).</param>
/// <param name="Issues">True for a CA that can issue certificates now: the OADM CA, an imported CA with its key.</param>
public sealed record CertificateAuthorityInfo(
    string Id,
    string Name,
    CertificateAuthorityKind Kind,
    IReadOnlyList<byte[]> Certificates,
    DateTime NotAfterUtc,
    bool Issues)
{
    /// <summary>Id of the OADM CA that issues now.</summary>
    public const string OadmId = "oadm";

    /// <summary>Id of the Axis device ID roots.</summary>
    public const string AxisDeviceIdId = "axis-device-id";
}
