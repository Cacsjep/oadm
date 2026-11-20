using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki;

/// <summary>PKI's <see cref="IIssuingPolicies"/>, offered through the plugin services while PKI runs (e.g. for Provisioning's nodes).</summary>
internal sealed class PkiIssuingPolicies(PolicyStore policies) : IIssuingPolicies
{
    /// <inheritdoc />
    public Task<IReadOnlyList<IssuingPolicyInfo>> ListAsync(IssuingPolicyUse use, CancellationToken ct)
    {
        var kind = use == IssuingPolicyUse.Dot1x ? PolicyKinds.Dot1xCertificate : PolicyKinds.HttpsCertificate;
        IReadOnlyList<IssuingPolicyInfo> list = [.. policies.PoliciesOf(kind).Select(p => new IssuingPolicyInfo(p.Id, p.Name, use))];
        return Task.FromResult(list);
    }
}
