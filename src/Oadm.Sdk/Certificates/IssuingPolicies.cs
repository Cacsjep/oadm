namespace Oadm.Sdk.Certificates;

/// <summary>What the certificates of an issuing policy are for (SDK 0.15.0).</summary>
public enum IssuingPolicyUse
{
    /// <summary>The camera's HTTPS certificate (PKI's task Enable HTTPS).</summary>
    Https,

    /// <summary>The camera's IEEE 802.1X certificate (PKI's task Enable IEEE 802.1X).</summary>
    Dot1x,
}

/// <summary>An issuing policy of PKI: its id (the <c>policyId</c> of PKI's tasks) and its name.</summary>
/// <param name="Id">The policy's id, the <c>policyId</c> of PKI's tasks.</param>
/// <param name="Name">The policy's name as the PKI page shows it.</param>
/// <param name="Use">What its certificates are for.</param>
public sealed record IssuingPolicyInfo(Guid Id, string Name, IssuingPolicyUse Use);

/// <summary>
/// PKI's issuing policies, read only (SDK 0.15.0), offered by PKI through <see cref="Plugins.IPluginServices"/> while it runs:
/// a plugin that starts PKI's tasks lets the user choose the policy by name and sends its id. Ask with <c>Find</c> each time
/// and never keep it. Thread-safe.
/// </summary>
public interface IIssuingPolicies
{
    /// <summary>The policies for <paramref name="use"/> in PKI's order (the first is the one PKI's dialogs choose first).</summary>
    Task<IReadOnlyList<IssuingPolicyInfo>> ListAsync(IssuingPolicyUse use, CancellationToken ct);
}
