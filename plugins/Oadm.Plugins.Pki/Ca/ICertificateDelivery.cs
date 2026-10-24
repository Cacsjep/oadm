namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// What the EST and 802.1X servers tell the store about a device certificate (docs/spec/pki.md "Current and pending"), in
/// process: <see cref="PkiCertificateAuthority"/> implements it. The servers find it on the CA offer (a cast), so a CA
/// without a store ignores them.
/// </summary>
public interface ICertificateDelivery
{
    /// <summary>
    /// EST wrote its answer with the certificate: an HTTPS, RTSPS or MQTT certificate becomes current (the previous one is
    /// revoked); an IEEE 802.1X one stays pending until the camera signs in to the 802.1X server with it.
    /// </summary>
    Task DeliveredAsync(string serialNumber, CancellationToken ct);

    /// <summary>EST could not write its answer: the camera never got the certificate (revoked as unused).</summary>
    Task NotDeliveredAsync(string serialNumber, CancellationToken ct);

    /// <summary>The 802.1X server let a camera in with this certificate: a pending IEEE 802.1X one becomes current.</summary>
    Task SignedInAsync(string serialNumber, CancellationToken ct);
}
