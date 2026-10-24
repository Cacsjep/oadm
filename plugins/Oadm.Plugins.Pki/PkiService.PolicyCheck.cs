using System.Globalization;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Server;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki;

/// <summary>
/// "Check a device" of the Policies tab (docs/spec/pki.md "Policies", decision 2026-10-25): which policies apply to one
/// device, decided by the same rules the servers use (<see cref="RadiusRuleSet"/> and its <see cref="PolicyPlan"/> for the
/// 802.1X server, <see cref="EstRules"/> for the EST server, the Expiry warning days as the host gets them). Read only.
/// </summary>
public sealed partial class PkiService
{
    /// <summary>The lines of <paramref name="deviceId"/>; an error when the device is gone or the policies cannot be read.</summary>
    internal async Task<PolicyCheck> CheckDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        var store = Policies;
        if (store.IsUnreadable)
        {
            return new PolicyCheck { Error = PolicyTexts.Unreadable };
        }

        var devices = await SystemDevicesAsync(ct).ConfigureAwait(false);
        if (devices.FirstOrDefault(d => d.Id == deviceId) is not { } device)
        {
            return new PolicyCheck { Error = DeviceMessages.Removed };
        }

        var lines = new List<PolicyCheckLine>();
        Issuing(PolicyCheckAreas.Https, PolicyKinds.HttpsCertificate);
        Issuing(PolicyCheckAreas.Dot1x, PolicyKinds.Dot1xCertificate);
        lines.Add(Expiry(store, device));
        lines.AddRange(Network(store, device));
        lines.Add(await EnrollmentAsync(store, device, ct).ConfigureAwait(false));
        return new PolicyCheck { Lines = lines };

        // The certificates the tasks issue by default: the system policy of the kind (the task dialogs preselect it).
        void Issuing(string area, string kind)
        {
            if (store.FirstOf(kind) is { } policy)
            {
                lines.Add(new PolicyCheckLine(area, store.SummaryOf(policy), [policy.Name]));
            }
        }
    }

    /// <summary>The days of the Expiry warning: the largest of the policies that hold the device, as the host gets them.</summary>
    private static PolicyCheckLine Expiry(PolicyStore store, IDeviceInfo device)
    {
        PkiPolicy? deciding = null;
        foreach (var policy in store.PoliciesOf(PolicyKinds.ExpiryWarning))
        {
            if ((policy.Days ?? 0) > (deciding?.Days ?? 0) && PolicyMatcher.Of(policy, store.TagsProtected).Holds(device))
            {
                deciding = policy;
            }
        }

        var days = deciding?.Days ?? PolicyLimits.DefaultExpiryDays;
        return new PolicyCheckLine(PolicyCheckAreas.Expiry, PolicyCheckTexts.Expiry(days), deciding is null ? [] : [deciding.Name]);
    }

    /// <summary>The 802.1X server: refused by a Block policy, else the trusted CAs and the VLAN.</summary>
    private static IEnumerable<PolicyCheckLine> Network(PolicyStore store, IDeviceInfo device)
    {
        var rules = RadiusRuleSet.Of(store);
        if (rules.Plan.RefusalFor(device) is { } block)
        {
            yield return new PolicyCheckLine(PolicyCheckAreas.Network, PolicyCheckTexts.Blocked(PolicyRules.Revokes(block)), [block.Name], IsWarning: true);
            yield break;
        }

        var trusted = store.PoliciesOf(PolicyKinds.AllowCa).Where(p => !string.IsNullOrWhiteSpace(p.CaId)).ToList();
        yield return trusted.Count == 0
            ? new PolicyCheckLine(PolicyCheckAreas.Network, PolicyCheckTexts.NoTrustedCa, [], IsWarning: true)
            : new PolicyCheckLine(PolicyCheckAreas.Network, PolicyCheckTexts.Passes(trusted.Select(p => store.CaNameOf(p.CaId)).Distinct(StringComparer.CurrentCulture).ToList()),
                [.. trusted.Select(p => p.Name)]);

        // The certificate Enable IEEE 802.1X gives it must come from a trusted CA, else the switch port stays closed.
        if (trusted.Count > 0 && store.FirstOf(PolicyKinds.Dot1xCertificate) is { } dot1x
            && !trusted.Any(p => string.Equals(p.CaId, dot1x.CaId ?? Sdk.Certificates.CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new PolicyCheckLine(PolicyCheckAreas.Network, PolicyCheckTexts.NotTrusted(store.CaNameOf(dot1x.CaId)), [dot1x.Name], IsWarning: true);
        }

        var (vlan, by) = rules.Plan.VlanFor(device);
        yield return vlan is { } id
            ? new PolicyCheckLine(PolicyCheckAreas.Network, PolicyCheckTexts.Vlan(id), by is null ? [] : [by])
            : new PolicyCheckLine(PolicyCheckAreas.Network, PolicyCheckTexts.NoVlan, []);
    }

    /// <summary>The EST server: the profiles the device may enroll for (each with the issuing policy it names), or why none.</summary>
    private static async Task<PolicyCheckLine> EnrollmentAsync(PolicyStore store, IDeviceInfo device, CancellationToken ct)
    {
        var holding = store.PoliciesOf(PolicyKinds.Enrollment).Where(p => PolicyMatcher.Of(p, store.TagsProtected).Holds(device)).Select(p => p.Name).ToList();
        if (holding.Count == 0)
        {
            return new PolicyCheckLine(PolicyCheckAreas.Est, PolicyCheckTexts.NoEnrollment, [], IsWarning: true);
        }

        var profiles = await store.EstProfilesAsync(ct).ConfigureAwait(false);
        if (profiles.Count == 0)
        {
            return new PolicyCheckLine(PolicyCheckAreas.Est, PolicyCheckTexts.NoProfiles, holding);
        }

        var rules = EstRules.Of(store);
        IReadOnlyList<(Guid PolicyId, string Profile)> uses = store.ProfileUses is { } read ? await read(ct).ConfigureAwait(false) : [];
        var allowed = profiles.Where(p => rules.Check(device, p).Allowed).Select(p =>
            uses.FirstOrDefault(u => string.Equals(u.Profile, p, StringComparison.OrdinalIgnoreCase)) is { PolicyId: var id } && store.Find(id) is { } issuing
                ? $"{p} ({issuing.Name})" : p).ToList();
        return new PolicyCheckLine(PolicyCheckAreas.Est, allowed.Count == 0 ? PolicyCheckTexts.NotEnrolled : PolicyCheckTexts.Enrolls(allowed), holding);
    }
}

/// <summary>The sentences of "Check a device" (one phrase per concept; the policy names follow each line on the page).</summary>
public static class PolicyCheckTexts
{
    public const string NoTrustedCa = "The 802.1X server lets no device in: there is no 802.1X trusted CA.";
    public const string NoVlan = "It gets no VLAN from the 802.1X server. The switch port keeps its own.";
    public const string NoEnrollment = "The EST server refuses it: no Enrollment policy includes it.";
    public const string NoProfiles = "The EST server has no profiles.";
    public const string NotEnrolled = "It may not get certificates from the EST server.";

    /// <summary>"Its certificates show a warning with 30 days or less left."</summary>
    public static string Expiry(int days) => string.Create(CultureInfo.CurrentCulture, $"Its certificates show a warning with {PolicyTexts.DaysText(days)} or less left.");

    /// <summary>"The 802.1X server refuses it." (with Also revoke: its IEEE 802.1X certificates are revoked too).</summary>
    public static string Blocked(bool revokes) => revokes
        ? "The 802.1X server refuses it. Its IEEE 802.1X certificates are revoked, new ones too."
        : "The 802.1X server refuses it.";

    /// <summary>"It passes the 802.1X server with a certificate from OADM CA or Axis device ID."</summary>
    public static string Passes(IReadOnlyList<string> cas)
    {
        ArgumentNullException.ThrowIfNull(cas);
        return "It passes the 802.1X server with a certificate from " + Or(cas) + ".";
    }

    /// <summary>"Enable IEEE 802.1X gives it a certificate from Corp CA, which is no 802.1X trusted CA."</summary>
    public static string NotTrusted(string ca) => $"Enable IEEE 802.1X gives it a certificate from {ca}, which is no 802.1X trusted CA.";

    /// <summary>"It gets VLAN 30."</summary>
    public static string Vlan(int vlan) => string.Create(CultureInfo.InvariantCulture, $"It gets VLAN {vlan}.");

    /// <summary>"It may get certificates for the profile https (HTTPS)." / "... the profiles https (HTTPS) and dot1x (IEEE 802.1X)."</summary>
    public static string Enrolls(IReadOnlyList<string> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Count == 1
            ? $"It may get certificates for the profile {profiles[0]}."
            : $"It may get certificates for the profiles {And(profiles)}.";
    }

    private static string Or(IReadOnlyList<string> items) => items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1];

    private static string And(IReadOnlyList<string> items) => items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
