using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// The issuing policy of a run of Enable HTTPS or Enable IEEE 802.1X (docs/spec/pki.md "Issuing policies have no devices"):
/// the policy the dialog chose (the first of the kind without one) with the values the dialog sent for this run. Checked
/// before the first write like a saved policy; the CA is always the policy's ("Issued by"; the dialog does not change it).
/// </summary>
public static class IssuingChoice
{
    /// <summary>The policy of the run: <paramref name="payloadJson"/>'s policy as stored (else the first of the kind).</summary>
    /// <exception cref="InvalidOperationException">No policy of the kind, or a value that is not allowed ("Nothing was changed").</exception>
    public static PkiPolicy Resolve(PkiService service, string kind, string? payloadJson)
    {
        ArgumentNullException.ThrowIfNull(service);
        var store = service.Policies;
        if (store.IsUnreadable)
        {
            throw new InvalidOperationException(PolicyTexts.Unreadable + " Nothing was changed.");
        }

        var payload = PkiJson.TryDeserialize<IssuePayload>(payloadJson) ?? new IssuePayload();
        var chosen = payload.PolicyId is { } id ? store.Find(id) : null;
        if (payload.PolicyId is not null && (chosen is null || chosen.Kind != kind))
        {
            throw new InvalidOperationException("The chosen policy no longer exists. Start the task again. Nothing was changed.");
        }

        var policy = chosen ?? store.FirstOf(kind)
            ?? throw new InvalidOperationException($"There is no {PolicyKinds.Name(kind)} policy. Add one on the PKI page. Nothing was changed.");
        var effective = PolicyRules.Normalize(policy);
        if (PolicyRules.ValidateIssuing(effective) is { } error)
        {
            throw new InvalidOperationException($"{effective.Name}: {error.Message} Nothing was changed.");
        }

        return effective;
    }

    /// <summary>"HTTPS: OADM CA, 365 days, ECC" (the Check compatibility step).</summary>
    public static string Describe(PkiPolicy policy, string? caName) =>
        policy.Name + ": " + PolicyStore.IssuingSummary(policy, caName is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [policy.CaId ?? CertificateAuthorityInfo.OadmId] = caName });

    /// <summary>
    /// The device can make the policy's key: its default keystore lists the key type. A device that does not say gets RSA 2048
    /// only (what OADM made before the policies); ECC then fails with "Nothing was changed".
    /// </summary>
    public static async Task RequireKeyAsync(Oadm.Sdk.Vapix.IVapixClient vapix, PkiPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var keyType = Device.CertApi.KeyTypeOf(policy.Key);
        var offered = await Device.CertApi.KeyTypesAsync(vapix, ct).ConfigureAwait(false);
        if (offered is null ? keyType != Device.CertApi.KeyType : !offered.Contains(keyType))
        {
            throw new DeviceNotCompatibleException(
                $"The device cannot create {(policy.Key == PolicyKeys.Ecc ? "an ECC" : "an RSA")} key. Choose a policy with {(policy.Key == PolicyKeys.Ecc ? "RSA" : "ECC")}. Nothing was changed.");
        }
    }

    /// <summary>The issuing policies of <paramref name="kind"/> for the dialog (read only).</summary>
    public static IssuingPolicyList List(PkiService? service, string kind)
    {
        if (service is null)
        {
            return new IssuingPolicyList { Error = "The PKI plugin is not running." };
        }

        if (service.Policies.IsUnreadable)
        {
            return new IssuingPolicyList { Error = PolicyTexts.Unreadable };
        }

        var policies = service.Policies.PoliciesOf(kind);
        // "Issued by" of each policy by name (decision 2026-10-25: any CA with its key issues).
        var names = service.Authorities().GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        return new IssuingPolicyList
        {
            Policies = policies,
            CaName = service.Ca is { } ca ? Ca.CaCertificates.CommonName(ca.Certificate) : null,
            CaNames = policies.ToDictionary(p => p.Id, p => names.GetValueOrDefault(p.CaId ?? CertificateAuthorityInfo.OadmId) ?? PolicyTexts.RemovedCa),
            RadiusCaNames = policies.Where(p => p.RadiusCaId is not null).ToDictionary(p => p.Id, p => names.GetValueOrDefault(p.RadiusCaId!) ?? PolicyTexts.RemovedCa),
            Summaries = policies.ToDictionary(p => p.Id, p => PolicyStore.Summary(p, names)),
        };
    }

    /// <summary>The query of the dialogs: <see cref="PkiQueries.ListIssuingPolicies"/>, else the clock check.</summary>
    internal static Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, PkiService? service, string kind, TimeProvider time, CancellationToken ct) =>
        method == PkiQueries.ListIssuingPolicies
            ? Task.FromResult<string?>(PkiJson.Serialize(List(service, kind)))
            : ClockQuery.QueryAsync(ctx, device, method, time, ct);
}
