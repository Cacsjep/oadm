namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// PKI's store of the cameras' current certificates for EST renewals (maintainer decision 2026-10-25, docs/spec/pki.md
/// "Current and pending"), in process: <see cref="PkiCertificateAuthority"/> implements it, EST finds it on the CA offer (a
/// cast), so a CA without a store offers none.
/// </summary>
public interface ICurrentCertificates
{
    /// <summary>
    /// The camera serial of <paramref name="certificateDer"/> when OADM issued exactly that certificate and it is its camera's
    /// current one now: in the store by its serial, the same DER, confirmed (not pending, not pushed out), not on the
    /// revocation list, never a certificate of the Certificates list made here. Null otherwise (also an entry without the
    /// camera's serial, from before 4.2). The camera comes from the store entry, never from a name in the certificate.
    /// O(1): lookups by serial.
    /// </summary>
    Task<string?> CurrentCameraAsync(byte[] certificateDer, CancellationToken ct);
}
