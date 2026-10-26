using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// docs/spec/pki.md "RADIUS server CA": the tasks always install the policy's RADIUS server CA, also when it did not issue
/// OADM's 802.1X server certificate (decision 2026-10-25: the user turns 802.1X off on the switch first); New OADM CA warns
/// that the servers keep their certificate; Renew HTTPS checks the clock first.
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
    public async Task A_radius_server_ca_that_did_not_issue_the_server_certificate_is_installed()
    {
        await using var h = await WithServerCertificateAsync();
        using var root = TestCa.Root("Contoso RADIUS");
        Assert.True((await h.Pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(root.RawData), null))).Ok);
        var policy = h.Pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)! with { RadiusCaId = PkiService.ImportedAuthorityId(Ca.CaCertificates.Fingerprint(root)) };
        Assert.Null((await h.Pki.Policies.SaveAsync(policy, "admin", PkiService.CipherSuites, CancellationToken.None)).Error);

        var cas = CertificateDeployment.RadiusCas(h.Pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!, h.Pki.Service);

        Assert.Equal([Ca.CaCertificates.Fingerprint(root)], cas.Select(c => c.Fingerprint));
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
