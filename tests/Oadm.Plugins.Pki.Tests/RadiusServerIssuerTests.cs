using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Device safety (docs/spec/pki.md "RADIUS server CA"): the cameras get the CA that issued OADM's 802.1X server certificate,
/// else Enable, Renew and Install certificates (802.1X) refuse before the first write; Renew HTTPS checks the clock first.
/// </summary>
public sealed class RadiusServerIssuerTests
{
    private static async Task<TaskHarness> WithServerCertificateAsync()
    {
        var h = await TaskHarness.StartAsync();
        await h.Pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None); // the shared one, from the OADM CA
        var shared = h.Pki.Policies.EstServerPolicy!.CertificateId;
        Assert.NotNull(shared);
        var server = Radius.Server.RadiusPolicies.DefaultServerPolicy() with { CertificateId = shared };
        Assert.Null((await h.Pki.Policies.SaveAsync(server, "admin", PkiService.CipherSuites, CancellationToken.None)).Error);
        return h;
    }

    [Fact]
    public async Task After_a_new_oadm_ca_enable_refuses_while_the_8021x_server_keeps_its_certificate_of_the_previous_ca()
    {
        await using var h = await WithServerCertificateAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable)); // the server's certificate is from the OADM CA the cameras get
        Assert.True((await h.Pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true))).Ok);
        var writes = h.Camera.Writes.Count;

        var ex = await h.RunAsync(PkiTaskIds.Dot1xEnable);

        Assert.NotNull(ex);
        Assert.StartsWith("The 802.1X server's certificate is issued by OADM Root CA SERVER01, but the policy IEEE 802.1X gives the cameras another RADIUS server CA.", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("Choose a certificate of the policy's RADIUS server CA in the 802.1X server policy. Nothing was changed.", ex.Message, StringComparison.Ordinal); // a previous CA cannot be chosen
        Assert.Contains("Check compatibility: Failed", h.Lines);
        Assert.Equal(writes, h.Camera.Writes.Count);

        // Renew skips 802.1X with the reason; Install certificates (802.1X) refuses in Check compatibility.
        Assert.Null(await h.RunAsync(PkiTaskIds.Renew));
        Assert.StartsWith("The 802.1X server's certificate is issued by", h.Detail(RenewTask.RenewDot1xStep), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_radius_server_ca_that_is_not_the_server_certificates_issuer_names_both_ways_out()
    {
        await using var h = await WithServerCertificateAsync();
        using var root = TestCa.Root("Contoso RADIUS");
        Assert.True((await h.Pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(root.RawData), null))).Ok);
        var policy = h.Pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)! with { RadiusCaId = PkiService.ImportedAuthorityId(Ca.CaCertificates.Fingerprint(root)) };
        Assert.Null((await h.Pki.Policies.SaveAsync(policy, "admin", PkiService.CipherSuites, CancellationToken.None)).Error);

        var ex = Assert.Throws<InvalidOperationException>(() => CertificateDeployment.RadiusCas(h.Pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!, h.Pki.Service));

        Assert.Contains("Choose that CA in the policy, or choose a certificate of the policy's RADIUS server CA in the 802.1X server policy.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_certificate_of_the_8021x_server_the_radius_server_ca_is_not_checked_against_it()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(h.Pki.Policies.FirstOf(PolicyKinds.Dot1xServer)?.CertificateId);
        Assert.True((await h.Pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true))).Ok);

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
    }

    [Fact]
    public async Task New_oadm_ca_warns_that_the_server_certificates_stay_from_the_current_ca()
    {
        await using var h = await WithServerCertificateAsync();

        var preview = await h.Pki.InvokeAsync<PreviewReplaceReply>(PkiMethods.PreviewReplace);

        Assert.Equal(["802.1X server", "EST server"], preview.Servers);
        Assert.Contains("The 802.1X server and the EST server keep their certificate from the current CA.",
            Client.PkiViewModel.ReplaceMessage(0, preview.Servers), StringComparison.Ordinal);
        Assert.Equal("Replace the CA?", Client.PkiViewModel.ReplaceMessage(0, []));
    }

    [Fact]
    public async Task Renew_checks_the_clock_before_it_renews_the_https_certificate()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        h.Camera.ClockOffset = -TimeSpan.FromMinutes(12);
        var writes = h.Camera.Writes.Count;

        var ex = await h.RunAsync(PkiTaskIds.Renew);

        Assert.EndsWith(" Set the device to use NTP. Nothing was changed.", ex!.Message, StringComparison.Ordinal);
        Assert.Contains("Check device clock: Failed", h.Lines);
        Assert.Equal(writes, h.Camera.Writes.Count);
    }
}
