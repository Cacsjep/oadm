using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The Summary of a policy row (and the editor's preview): one plain sentence per kind with its values.</summary>
public sealed class PolicySentenceTests
{
    private static readonly Dictionary<string, string> Cas = new(StringComparer.OrdinalIgnoreCase)
    {
        [CertificateAuthorityInfo.OadmId] = "OADM Root CA acs",
        ["imported:contoso"] = "Contoso Root",
    };

    private static string Of(PkiPolicy policy) => PolicyStore.Summary(policy, Cas);

    [Fact]
    public void Every_kind_says_what_it_does_in_one_sentence()
    {
        Assert.Equal("Certificates with 30 days or less left show a warning and raise Certificate expires soon.",
            Of(new PkiPolicy { Kind = PolicyKinds.ExpiryWarning, Days = 30 }));
        Assert.Equal("Enable HTTPS issues certificates from OADM Root CA acs, valid 365 days, with an ECC key.",
            Of(new PkiPolicy { Kind = PolicyKinds.HttpsCertificate, CaId = "oadm", ValidDays = 365, Key = PolicyKeys.Ecc }));
        Assert.Equal("Enable IEEE 802.1X issues certificates from OADM Root CA acs, valid 365 days, with an ECC key. Cameras sign in with their MAC address.",
            Of(new PkiPolicy { Kind = PolicyKinds.Dot1xCertificate, CaId = "oadm", ValidDays = 365, Key = PolicyKeys.Ecc, Identity = Dot1xIdentity.Mac }));
        Assert.EndsWith("Cameras sign in as camera-lobby.",
            Of(new PkiPolicy { Kind = PolicyKinds.Dot1xCertificate, CaId = "oadm", ValidDays = 365, Key = PolicyKeys.Rsa, Identity = Dot1xIdentity.Custom, CustomIdentity = "camera-lobby" }), StringComparison.Ordinal);
        // The server policies show a certificate of the list (decision 2026-10-25); the suites follow the minimum version.
        var certificates = new Dictionary<string, string> { ["shared"] = "802.1X and EST server (OADM Root CA acs), until 2036-10-22" };
        Assert.Equal("The EST server shows the certificate 802.1X and EST server (OADM Root CA acs), until 2036-10-22.",
            PolicyStore.Summary(new PkiPolicy { Kind = PolicyKinds.EstServer, CertificateId = "shared" }, Cas, certificates));
        Assert.Equal("The EST server has no certificate yet.", Of(new PkiPolicy { Kind = PolicyKinds.EstServer }));
        Assert.Equal("The EST server shows a certificate that is no longer in the list.", Of(new PkiPolicy { Kind = PolicyKinds.EstServer, CertificateId = "gone" }));
        Assert.Equal(
            "The 802.1X server shows the certificate 802.1X and EST server (OADM Root CA acs), until 2036-10-22. It accepts TLS 1.2 or later with secure cipher suites, sends EAP packets of up to 1,020 bytes and asks cameras to sign in again every 60 minutes.",
            PolicyStore.Summary(new PkiPolicy { Kind = PolicyKinds.Dot1xServer, CertificateId = "shared", MinimumTls = "1.2", LargestEapPacket = 1020, ReauthMinutes = 60 }, Cas, certificates));
        Assert.Equal("Enable IEEE 802.1X issues certificates from OADM Root CA acs, valid 365 days, with an ECC key. Cameras sign in with the last octet of their IP address. Cameras check the RADIUS server with Contoso Root.",
            Of(new PkiPolicy { Kind = PolicyKinds.Dot1xCertificate, CaId = "oadm", ValidDays = 365, Key = PolicyKeys.Ecc, Identity = Dot1xIdentity.IpLastOctet, RadiusCaId = "imported:contoso" }));
        Assert.Equal("The 802.1X server lets in devices OADM manages that have a certificate from OADM Root CA acs.",
            Of(new PkiPolicy { Kind = PolicyKinds.AllowCa, CaId = "oadm", OnlyManaged = true }));
        Assert.Equal("The 802.1X server lets in any device with a valid certificate from Contoso Root.",
            Of(new PkiPolicy { Kind = PolicyKinds.AllowCa, CaId = "imported:contoso", OnlyManaged = false }));
        Assert.Equal("The 802.1X server puts these devices in VLAN 30.", Of(new PkiPolicy { Kind = PolicyKinds.Vlan, Vlan = 30 }));
        Assert.Equal("The 802.1X server refuses these devices.", Of(new PkiPolicy { Kind = PolicyKinds.Block }));
        Assert.Equal("The 802.1X server refuses these devices. Their IEEE 802.1X certificates are revoked, new ones too.", Of(new PkiPolicy { Kind = PolicyKinds.Block, AlsoRevoke = true }));
        Assert.Equal("These cameras may get certificates from the EST server for every profile.",
            Of(new PkiPolicy { Kind = PolicyKinds.Enrollment, ProfileScope = PolicyProfiles.All }));
        Assert.Equal("These cameras may get certificates from the EST server for the profiles https and dot1x.",
            Of(new PkiPolicy { Kind = PolicyKinds.Enrollment, ProfileScope = PolicyProfiles.Chosen, Profiles = ["https", "dot1x"] }));
        Assert.Equal("These cameras may not get certificates from the EST server.",
            Of(new PkiPolicy { Kind = PolicyKinds.Enrollment, ProfileScope = PolicyProfiles.None }));
        Assert.Equal("Cameras sign in to the EST server with their Axis device ID for new certificates and renewals.",
            Of(new PkiPolicy { Kind = PolicyKinds.SignInCa, CaId = CertificateAuthorityInfo.AxisDeviceIdId, SignInFor = PolicySignIn.New }));
        Assert.Equal("Cameras with a certificate from OADM Root CA acs may sign in to renew it.",
            Of(new PkiPolicy { Kind = PolicyKinds.SignInCa, CaId = "oadm", SignInFor = PolicySignIn.Renewal }));
    }
}
