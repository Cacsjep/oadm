using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// Why the 802.1X and EST servers refuse a certificate on PKI's internal list of certificates no longer accepted (decision
/// 2026-10-26: no revoke in the UI; Delete and the automatic replacement put a certificate there until it ends). One phrase
/// per cause, never the word "revoked".
/// </summary>
public static class NotAcceptedTexts
{
    /// <summary>A newer certificate of the same camera and use was confirmed (or this one never reached the camera).</summary>
    public const string Replaced = "Certificate replaced by a newer certificate";

    /// <summary>Deleted on the PKI page (or by a revoke of OADM before 4.5).</summary>
    public const string Deleted = "Certificate deleted in OADM";

    /// <summary>The refusal text for <paramref name="entry"/> (null: deleted).</summary>
    public static string Of(CertificateRevocation? entry) => entry?.Reason == RevocationReason.Superseded ? Replaced : Deleted;
}
