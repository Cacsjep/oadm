using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The decisions of 2026-10-25 on the server side (docs/spec/pki.md "Policies", "The PKI page"): system policies that cannot
/// be deleted, the policy lines of the server tabs' tooltip, the CA list apart from the certificates, the OADM server's own
/// certificate as a read-only row, and the EST policy names of earlier builds renamed once.
/// </summary>
public sealed class PolicyUiTests
{
    private static readonly IReadOnlyList<PolicyCipherSuite> Suites = PkiService.CipherSuites;

    private static Task<CertificatesReply> PageAsync(PkiHarness pki, CertificatesRequest request) =>
        pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, request);

    [Fact]
    public async Task The_first_policy_of_a_needed_kind_is_a_system_policy_that_cannot_be_deleted()
    {
        await using var pki = await PkiHarness.StartAsync();
        var store = pki.Policies;
        var https = store.FirstOf(PolicyKinds.HttpsCertificate)!;
        var expiry = store.FirstOf(PolicyKinds.ExpiryWarning)!;
        Assert.True(PolicyStore.IsSystem(https, store.All));
        Assert.True(PolicyStore.IsSystem(expiry, store.All));

        // A second HTTPS policy (other days for the task dialogs) is the user's own: it can be deleted.
        var extra = (await store.SaveAsync(https with { Id = Guid.Empty, Name = "HTTPS 90 days", ValidDays = 90 }, "admin", Suites, CancellationToken.None)).Saved!;
        Assert.False(PolicyStore.IsSystem(extra, store.All));

        var refused = await store.DeleteAsync(https.Id, "admin", CancellationToken.None);
        Assert.Equal(PolicyTexts.SystemPolicy, refused.Error?.Message);
        Assert.NotNull(store.Find(https.Id));
        Assert.Null((await store.DeleteAsync(extra.Id, "admin", CancellationToken.None)).Error);

        // The rows of the page carry the flag.
        var rows = store.Rows([]);
        Assert.True(rows.Single(r => r.Policy.Id == https.Id).IsSystem);
        Assert.All(rows.Where(r => r.Policy.Kind is PolicyKinds.Dot1xServer or PolicyKinds.EstServer), r => Assert.True(r.IsSystem));
    }

    [Fact]
    public async Task The_lines_of_a_server_tab_are_its_policies_with_their_summary_in_age_order()
    {
        await using var pki = await PkiHarness.StartAsync();
        var store = pki.Policies;
        var lines = store.LinesOf(PolicyKinds.IsDot1x);
        var expected = store.All.Where(p => PolicyKinds.IsDot1x(p.Kind)).Select(p => p.Name).ToList();

        Assert.Equal(expected, lines.Select(l => l.Name));
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Summary)));
        Assert.Empty(store.LinesOf(_ => false));
    }

    [Fact]
    public async Task The_cas_and_the_certificates_are_two_lists_and_the_oadm_server_certificate_is_a_read_only_row()
    {
        await using var pki = await PkiHarness.StartAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var own = new CertificateRequest("CN=SERVER01", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var der = own.RawData;
        pki.Anchors.ServerCertificate = () => der;

        var cas = await PageAsync(pki, new CertificatesRequest { Purpose = ListedCertificateUses.Ca });
        var others = await PageAsync(pki, new CertificatesRequest { NoAuthorities = true });

        Assert.NotEmpty(cas.Rows);
        Assert.All(cas.Rows, r => Assert.Equal(ListedCertificateKind.Authority, r.Kind));
        Assert.DoesNotContain(others.Rows, r => r.Kind == ListedCertificateKind.Authority);
        Assert.DoesNotContain(others.Groups, g => g.Key == IssuedCertificateGroups.Authorities);

        var server = Assert.Single(others.Rows, r => r.Id == "oadm-server");
        Assert.Equal((ListedCertificateKind.Server, "OADM server", IssuedCertificateMessages.SelfSigned, false), (server.Kind, server.Name, server.IssuedBy, server.HasKey));
        var view = await pki.InvokeAsync<CertificateDetailsReply>(PkiMethods.GetCertificate, new CertificateDetailsRequest("oadm-server"));
        Assert.NotNull(view.Certificate);

        // Without the host's certificate (plain HTTP): no row.
        pki.Anchors.ServerCertificate = null;
        Assert.DoesNotContain((await PageAsync(pki, new CertificatesRequest { NoAuthorities = true })).Rows, r => r.Id == "oadm-server");
    }

    [Theory]
    [InlineData("https", "EST HTTPS")]
    [InlineData("dot1x", "EST 802.1X")]
    [InlineData("mqtt_a", "EST mqtt_a")]
    public void The_policy_of_an_est_profile_is_named_like_the_rest_of_oadm(string profile, string expected) =>
        Assert.Equal(expected, Oadm.Plugins.Est.Serving.EstPolicies.NameFor(new Oadm.Plugins.Est.EstProfile(profile, Oadm.Plugins.Est.ProfileUse.Https)));
}
