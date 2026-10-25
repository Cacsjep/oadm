using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// The chain of the CA a policy names as "Issued by" (decision 2026-10-25: any CA with its key issues), read through PKI's
/// CA offer: EST's answers and its <c>cacerts</c>.
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
}
