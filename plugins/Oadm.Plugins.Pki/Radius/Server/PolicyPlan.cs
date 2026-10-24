using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// PKI's VLAN, Block and Revoke policies compiled for the certificate check (docs/spec/pki.md "Policies"): of the VLAN
/// policies that hold a device the oldest gives its VLAN, none gives no VLAN (the switch port keeps its own; there is no
/// default VLAN); any Block or Revoke policy that holds it refuses it (Block over VLAN), a Revoke policy also revokes its
/// certificate. A device OADM does not manage (a certificate of an Allow CA for any device) is held only by "All devices"
/// policies (<see cref="PolicyDevices.AllDevices"/>): their VLAN, their Block. Immutable; a change of the policies builds a new one before the next handshake. The tag parts of VLAN
/// policies apply only while the host protects the tags (<see cref="IProtectedTags"/>); without it they match no device
/// (fail closed: a group user could tag a device into a VLAN). Block policies keep their tag parts (fail closed the other
/// way). Revoke policies never match by tag: a tag could revoke the certificates of every device that gets it later.
/// </summary>
public sealed class PolicyPlan
{
    private readonly (PkiPolicy Policy, PolicyMatcher Matcher)[] _vlans;
    private readonly (PkiPolicy Policy, PolicyMatcher Matcher)[] _refusals;

    /// <param name="policies">PKI's policies in age order (oldest first); other kinds are skipped.</param>
    /// <param name="tagsProtected">The host protects the policies' tags.</param>
    public PolicyPlan(IEnumerable<PkiPolicy> policies, bool tagsProtected = true)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var list = policies.Where(p => p is not null).ToList();
        _vlans = [.. list.Where(p => p.Kind == PolicyKinds.Vlan && p.Vlan is >= 1 and <= 4094).Select(p => (p, PolicyMatcher.Of(p, tagsProtected)))];
        _refusals = [.. list.Where(p => p.Kind is PolicyKinds.Block or PolicyKinds.Revoke).Select(p => (p, PolicyMatcher.Of(p, tagsProtected)))];
        RevokePolicies = [.. list.Where(p => p.Kind == PolicyKinds.Revoke)];
    }

    private PolicyPlan()
        : this([])
    {
        IsUnavailable = true;
    }

    /// <summary>No policies: no VLAN, nobody blocked.</summary>
    public static PolicyPlan Off { get; } = new([]);

    /// <summary>PKI's policies cannot be read (PKI stopped): every device is refused (fail closed).</summary>
    public static PolicyPlan Unavailable { get; } = new();

    /// <summary>The plan of no store: the certificate check refuses every device.</summary>
    public bool IsUnavailable { get; }

    public IReadOnlyList<PkiPolicy> RevokePolicies { get; }

    /// <summary>
    /// The VLAN of <paramref name="device"/> and the policy that gives it; (null, null) for none. A device OADM does not manage
    /// (null) gets the VLAN of the oldest "All devices" VLAN policy.
    /// </summary>
    public (int? Vlan, string? Policy) VlanFor(IDeviceInfo? device)
    {
        foreach (var (policy, matcher) in _vlans)
        {
            if (device is null ? matcher.HoldsEveryDevice : matcher.Holds(device))
            {
                return (policy.Vlan, policy.Name);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The oldest Block or Revoke policy that holds <paramref name="device"/>, null when none does. A device OADM does not
    /// manage (null): the oldest "All devices" Block policy.
    /// </summary>
    public PkiPolicy? RefusalFor(IDeviceInfo? device)
    {
        foreach (var (policy, matcher) in _refusals)
        {
            if (device is null ? matcher.HoldsEveryDevice : matcher.Holds(device))
            {
                return policy;
            }
        }

        return null;
    }
}
