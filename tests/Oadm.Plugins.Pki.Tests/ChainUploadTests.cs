using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Decision 2026-10-26 (item 15): a camera certificate of an intermediate CA goes to the camera with its chain (Enable HTTPS,
/// Enable IEEE 802.1X; REST and the SOAP service of older AXIS OS); a camera that refuses the chain gets the certificate alone
/// and the task log says so. A certificate of a root CA is installed exactly as before.
/// </summary>
public sealed class ChainUploadTests
{
    private const string IssuingName = "Site Issuing CA";

    /// <summary>An intermediate CA under the first-start root CA; the HTTPS and IEEE 802.1X policies issue with it (RSA keys for older AXIS OS).</summary>
    private static async Task<CaMaterial> UseIntermediateAsync(TaskHarness h, bool rsa = false)
    {
        var created = await h.Pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority, new CreateCaRequest(IssuingName, true, CertificateAuthorityInfo.OadmId, CaKeys.Ecc, 5));
        Assert.True(created.Ok, created.Error);
        var id = h.Pki.Service.Authorities().Single(a => a.Name == IssuingName).Id;
        var key = rsa ? PolicyKeys.Rsa : PolicyKeys.Ecc;
        foreach (var kind in new[] { PolicyKinds.HttpsCertificate, PolicyKinds.Dot1xCertificate })
        {
            var reply = await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, h.Pki.Service.Policies.FirstOf(kind)! with { CaId = id, Key = key, EapolVersion = kind == PolicyKinds.Dot1xCertificate ? 2 : null });
            Assert.Null(reply.Error);
        }

        return h.Pki.Service.IssuingCa(id)!;
    }

    private static int Certificates(string pem) => pem.Split("BEGIN CERTIFICATE").Length - 1;

    private static List<CameraRequest> Installs(FakeCamera camera) =>
        [.. camera.Requests.Where(r => (r.Method == "PATCH" && r.Path.Contains("/certificates/", StringComparison.Ordinal)) || r.Body.Contains("<tds:LoadCertificates>", StringComparison.Ordinal))];

    [Fact]
    public async Task Enable_https_sends_a_certificate_of_an_intermediate_ca_with_its_chain()
    {
        await using var h = await TaskHarness.StartAsync();
        var ca = await UseIntermediateAsync(h);

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        var pem = h.Camera.CertificatePem(h.Camera.WebServerAlias);
        Assert.Equal(2, Certificates(pem)); // the certificate and the intermediate CA, never the root
        var stored = CaCertificates.ReadPemCertificates(pem);
        Assert.Equal(ca.Certificate.Subject, stored[0].Issuer);
        Assert.Equal(ca.Certificate.RawData, stored[1].RawData);
        Assert.Single(Installs(h.Camera));
        Assert.Contains(h.Context.LogEntries, e => e.Level == TaskLogLevel.Info && e.Message.EndsWith("installed with its chain (1 CA certificate).", StringComparison.Ordinal));
        Assert.Contains("Verify HTTPS: Done", h.Lines);
    }

    [Fact]
    public async Task A_camera_that_refuses_the_chain_gets_the_certificate_alone_and_the_log_says_so()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.Chains = ChainAnswer.Refuses;
        await UseIntermediateAsync(h);

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Equal(1, Certificates(h.Camera.CertificatePem(h.Camera.WebServerAlias)));
        Assert.Equal(2, Installs(h.Camera).Count); // with the chain, then alone
        var warning = Assert.Single(h.Context.LogEntries, e => e.Level == TaskLogLevel.Warning);
        Assert.StartsWith("The device refused the certificate with its chain (Install certificate: the device refused: ", warning.Message, StringComparison.Ordinal);
        Assert.EndsWith("; the certificate was installed alone.", warning.Message, StringComparison.Ordinal);
        Assert.Empty(h.Context.Warnings); // Done, not Done with warnings: the camera works with the certificate alone
        Assert.Contains("Verify HTTPS: Done", h.Lines);
    }

    [Fact]
    public async Task A_certificate_of_a_root_ca_is_installed_alone_as_before()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.Chains = ChainAnswer.Refuses; // never reached: nothing is sent with a chain

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Equal(1, Certificates(h.Camera.CertificatePem(h.Camera.WebServerAlias)));
        Assert.Single(Installs(h.Camera));
        Assert.DoesNotContain(h.Context.LogEntries, e => e.Message.Contains("chain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enable_8021x_sends_the_client_certificate_with_its_chain_and_falls_back_when_refused()
    {
        await using var h = await TaskHarness.StartAsync();
        await UseIntermediateAsync(h);

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        Assert.Equal(2, Certificates(h.Camera.CertificatePem(Newest(h.Camera))));

        h.Camera.Chains = ChainAnswer.Refuses;
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        Assert.Contains(h.Context.LogEntries, e => e.Level == TaskLogLevel.Warning && e.Message.StartsWith("The device refused the certificate with its chain", StringComparison.Ordinal));
        Assert.Equal(1, Certificates(h.Camera.CertificatePem(Newest(h.Camera))));

        static string Newest(FakeCamera camera) =>
            camera.CertificateAliases.Where(a => a.StartsWith(CertificateDeployment.Dot1xAliasPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).Last();
    }

    [Theory]
    [InlineData(ChainAnswer.Takes, false)]
    [InlineData(ChainAnswer.Refuses, true)]
    [InlineData(ChainAnswer.KeepsAsSent, true)]
    public async Task Older_axis_os_gets_the_chain_through_the_soap_service_and_the_certificate_alone_when_it_is_not_kept(ChainAnswer answer, bool alone)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy("9.80"));
        h.Camera.Chains = answer;
        var ca = await UseIntermediateAsync(h, rsa: true);

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        var alias = h.Camera.WebServerAlias;
        var pem = h.Camera.CertificatePem(alias);
        Assert.Equal(1, Certificates(pem));
        using (var served = X509Certificate2.CreateFromPem(pem))
        {
            Assert.Equal(ca.Certificate.Subject, served.Issuer);
            Assert.Equal(CaCertificates.Fingerprint(served), h.Context.PinnedFingerprint);
        }

        var installs = Installs(h.Camera);
        Assert.Equal(alone ? 2 : 1, installs.Count);
        Assert.DoesNotContain(h.Camera.CertificateAliases, a => a.EndsWith(DeviceCertificates.KeySuffix, StringComparison.Ordinal)); // the placeholder is gone
        if (alone)
        {
            Assert.Contains(h.Context.LogEntries, e => e.Level == TaskLogLevel.Warning && e.Message.StartsWith("The device refused the certificate with its chain", StringComparison.Ordinal)
                && e.Message.Contains(answer == ChainAnswer.KeepsAsSent ? "did not keep the certificate as sent" : "Invalid certificate", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(h.Context.LogEntries, e => e.Level == TaskLogLevel.Info && e.Message.EndsWith("installed with its chain (1 CA certificate).", StringComparison.Ordinal));
        }

        Assert.Contains("Verify HTTPS: Done", h.Lines);
    }

    [Fact]
    public void The_chain_of_a_ca_is_its_intermediates_from_the_issuer_up_and_none_for_a_root()
    {
        var now = DateTimeOffset.UtcNow;
        using var root = CaGenerator.Generate("Root", null, 10, now, CaKeys.Ecc, null);
        using var middle = CaGenerator.Generate("Middle", null, 5, now, CaKeys.Ecc, root);
        using var issuing = CaGenerator.Generate("Issuing", null, 2, now, CaKeys.Ecc, middle);

        Assert.Empty(CertificateDeployment.IssuerChainPems(root));
        Assert.Equal([CaCertificates.ToPem(middle.Certificate)], CertificateDeployment.IssuerChainPems(middle));
        Assert.Equal([CaCertificates.ToPem(issuing.Certificate), CaCertificates.ToPem(middle.Certificate)], CertificateDeployment.IssuerChainPems(issuing));
    }
}
