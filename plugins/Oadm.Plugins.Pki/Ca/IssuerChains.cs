using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// The chain of the CA a policy names as "Issued by" (decision 2026-10-25: any CA with its key issues), read through PKI's
/// CA offer: the 802.1X and EST servers' certificates, EST's answers and its <c>cacerts</c>.
/// </summary>
public static class IssuerChains
{
    /// <summary>
    /// The CA certificate of <paramref name="caId"/> and the certificates above it (DER, the CA first): the OADM CA's chain for
    /// null or "oadm", else the listed CA that can issue; empty without a CA or when that CA cannot issue.
    /// </summary>
    public static IReadOnlyList<byte[]> Of(IDeviceCertificateAuthority? authority, string? caId)
    {
        if (authority is null)
        {
            return [];
        }

        if (caId is null || string.Equals(caId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase))
        {
            return authority.IsReady ? authority.Chain : [];
        }

        return authority.Authorities.FirstOrDefault(a => a.Issues && string.Equals(a.Id, caId, StringComparison.OrdinalIgnoreCase))?.Certificates ?? [];
    }

    /// <summary>
    /// Why <paramref name="server"/> ("The 802.1X server") has no certificate although PKI has its OADM CA: the CA its policy
    /// names cannot issue ("The 802.1X server has no certificate: Corp CA cannot issue.") or left the PKI; null when that CA
    /// can issue, or when PKI has no CA at all (the servers say that on their own).
    /// </summary>
    public static string? NoCertificateReason(IDeviceCertificateAuthority? authority, string? caId, string server)
    {
        if (authority is null || caId is null || string.Equals(caId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase) || Of(authority, caId).Count > 0)
        {
            return null;
        }

        var listed = authority.Authorities.FirstOrDefault(a => string.Equals(a.Id, caId, StringComparison.OrdinalIgnoreCase));
        return Policies.PolicyTexts.ServerWithoutCertificate(server, listed?.Name);
    }
}
