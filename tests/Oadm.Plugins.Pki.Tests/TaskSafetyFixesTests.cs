using System.Net;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Fixes of the second review of 2026-10-25 (docs/spec/pki.md): Delete certificates never deletes a certificate whose use
/// cannot be read, an HTTPS certificate the web server may present is never revoked as unused, a refused removal of the
/// previous certificate is a warning, CA alias collisions and CA only files. REST (AXIS OS 12.11) and SOAP (older AXIS OS)
/// paths against the fake camera.
/// </summary>
public sealed class TaskSafetyFixesTests
{
    private static string DeletePayload(TaskHarness h, params CertificateRef[] refs) =>
        PkiJson.Serialize(new DeletePayload { Devices = new Dictionary<Guid, IReadOnlyList<CertificateRef>> { [h.Device.Id] = refs } });

    private static async Task<CertificateListReply> QueryAsync(TaskHarness h) =>
        PkiJson.Deserialize<CertificateListReply>(await h.Task<DeleteCertificatesTask>(PkiTaskIds.Delete).QueryAsync(new PkiTaskContext(h.Camera), h.Device, PkiQueries.ListCertificates, null, CancellationToken.None));

    private static async Task AssertDeleteRefusedAsync(TaskHarness h, CertificateRef choice)
    {
        var writes = h.Camera.Writes.Count;

        var ex = await h.RunAsync(PkiTaskIds.Delete, DeletePayload(h, choice));

        Assert.StartsWith("What uses the certificates cannot be read (", ex!.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Read certificates: Failed", h.Lines);
        Assert.Contains("Delete certificate " + choice.Alias + ": Skipped", h.Lines);
        Assert.Equal(writes, h.Camera.Writes.Count);
        Assert.Contains(choice.Alias, choice.Ca ? h.Camera.CaAliases : h.Camera.CertificateAliases);

        // The dialog shows the use as unknown: nothing can be checked for delete.
        var reply = await QueryAsync(h);
        Assert.Null(reply.Error);
        Assert.All(reply.Certificates, c => Assert.True(c.UseUnknown));
        var row = new CertificateRow(h.Device, reply.Certificates.First(c => c.Alias == choice.Alias));
        Assert.Equal(InstalledCertificate.UnknownUseText, row.InUse);
        Assert.Equal("What uses it cannot be read: it cannot be deleted.", row.BlockedReason);
        Assert.False(row.IsSelectable);
    }

    [Fact]
    public async Task Delete_refuses_when_the_web_server_settings_cannot_be_read_rest()
    {
        await using var h = await TaskHarness.StartAsync();
        using var unused = TestCa.Root("old one", ca: false);
        h.Camera.AddCertificate("old one", CaCertificates.ToPem(unused));
        h.Camera.FailSoap = "GetWebServerTlsConfiguration";

        await AssertDeleteRefusedAsync(h, new CertificateRef("old one", false));
    }

    [Fact]
    public async Task Delete_refuses_when_the_8021x_settings_cannot_be_read_rest()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.HasNetworkSettings = false; // listed, but getNetworkInfo answers with an error

        await AssertDeleteRefusedAsync(h, new CertificateRef("COMODO ECC Certification Authority", true));
    }

    [Theory]
    [InlineData("GetWebServerTlsConfiguration")]
    [InlineData("GetCertSet")]
    public async Task Delete_refuses_when_a_use_cannot_be_read_soap(string failing)
    {
        await using var h = await TaskHarness.StartAsync(FakeCamera.Legacy("8.40"));
        using var root = TestCa.Root("Acme Root CA");
        const string ca = "Acme Root CA";
        h.Camera.AddCaCertificate(ca, CaCertificates.ToPem(root));
        h.Camera.FailSoap = failing;

        await AssertDeleteRefusedAsync(h, new CertificateRef(ca, true));

        // Read again: the delete goes ahead.
        h.Camera.FailSoap = null;
        Assert.Null(await h.RunAsync(PkiTaskIds.Delete, DeletePayload(h, new CertificateRef(ca, true))));
        Assert.DoesNotContain(ca, h.Camera.CaAliases);
    }

    [Fact]
    public async Task An_https_certificate_whose_switch_answer_got_lost_is_never_revoked()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        var previous = CurrentOf(h);
        h.Camera.FailWebServerSwitchAnswer = true; // applied, but the answer is a fault

        Assert.NotNull(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Contains("Switch web server to the new certificate: Failed", h.Lines);
        Assert.StartsWith(CertificateDeployment.HttpsAliasPrefix, h.Camera.WebServerAlias, StringComparison.Ordinal);
        Assert.Empty(h.Pki.Service.Issued.Revocations); // neither the new one (the web server presents it) nor the previous one
        var pending = h.Pki.Service.Issued.ForDevice(h.Device).Single(e => e.Pending);
        Assert.True(pending.Delivered);
        Assert.Equal(previous.SerialNumber, CurrentOf(h).SerialNumber);
        Assert.Contains(h.Context.LogEntries, e => e.Message.Contains("stays pending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_https_certificate_is_kept_when_the_web_server_does_not_come_back()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.AnswersBeforeRestart = 0;
        h.Camera.RestartRefusals = 1000; // the read after the failed Verify HTTPS is refused too

        Assert.NotNull(await h.RunWaitingAsync(PkiTaskIds.HttpsEnable));

        Assert.Contains("Verify HTTPS: Failed", h.Lines);
        Assert.Empty(h.Pki.Service.Issued.Revocations);
        Assert.True(h.Pki.Service.Issued.ForDevice(h.Device).Single(e => e.Pending).Delivered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("10.12")]
    public async Task An_https_certificate_the_web_server_does_not_use_is_revoked_after_a_failed_verify(string? legacy)
    {
        await using var h = await TaskHarness.StartAsync(legacy is null ? null : FakeCamera.Legacy(legacy));
        if (legacy is not null)
        {
            await UseRsaAsync(h);
        }

        h.Camera.FailWebServerSwitch = true; // refused: the web server keeps its certificate

        Assert.NotNull(await h.RunAsync(PkiTaskIds.HttpsEnable));

        var unused = Assert.Single(h.Pki.Service.Issued.Revocations);
        Assert.Equal(RevocationReason.Other, unused.Reason);
        Assert.Empty(h.Pki.Service.Issued.ForDevice(h.Device));
    }

    [Fact]
    public async Task A_refused_removal_of_the_previous_certificate_is_a_warning()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        var first = h.Camera.WebServerAlias;
        h.Camera.FailPath = "/certificates/" + first; // the delete of the previous one is refused

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        Assert.Equal("Remove previous OADM certificate: Warning", h.Lines.Single(l => l.StartsWith(PkiSteps.RemovePrevious, StringComparison.Ordinal)));
        Assert.Contains(h.Context.Warnings, w => w.Contains(first, StringComparison.Ordinal));
        Assert.Contains(h.Context.LogEntries, e => e.Level == TaskLogLevel.Warning && e.Message.Contains(first, StringComparison.Ordinal));
        Assert.NotEqual(first, h.Camera.WebServerAlias); // switched and confirmed
        Assert.Contains(first, h.Camera.CertificateAliases);
        Assert.False(CurrentOf(h).Pending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("10.12")]
    public async Task A_ca_alias_another_ca_holds_gets_a_free_alias(string? legacy)
    {
        await using var h = await TaskHarness.StartAsync(legacy is null ? null : FakeCamera.Legacy(legacy));
        using var root = TestCa.Root("Acme Root CA");
        var fingerprint = CaCertificates.Fingerprint(root);
        var alias = CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, fingerprint);
        using var other = TestCa.Root("Other CA");
        h.Camera.AddCaCertificate(alias, CaCertificates.ToPem(other)); // same alias, another fingerprint
        var ctx = new PkiTaskContext(h.Camera);
        ctx.PlanSteps(PkiSteps.InstallCas);
        var api = legacy is null ? CertificateApiKind.Rest : CertificateApiKind.Soap;

        var aliases = await CertificateDeployment.InstallCaCertificatesAsync(ctx, PkiSteps.InstallCas, [(alias, CaCertificates.ToPem(root), fingerprint)], api, CancellationToken.None);

        Assert.Equal([alias + "-2"], aliases);
        Assert.Equal(fingerprint, CaCertificates.Fingerprint(X509Certificate2.CreateFromPem(h.Camera.CaCertificatePem(alias + "-2"))));
        Assert.Equal(CaCertificates.Fingerprint(other), CaCertificates.Fingerprint(X509Certificate2.CreateFromPem(h.Camera.CaCertificatePem(alias))));
    }

    [Fact]
    public async Task Ca_only_refuses_a_file_with_a_certificate_that_is_not_a_ca()
    {
        await using var h = await TaskHarness.StartAsync();
        using var ca = TestCa.Root("Customer CA");
        using var leaf = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.48");
        using var publicCa = X509CertificateLoader.LoadCertificate(ca.RawData);
        var pfx = TestCa.Pfx("pw", leaf, publicCa);
        var payload = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.CaOnly, Password = "pw", Files = [new InstallFile(h.Device.Id, "f1", "cam48.pfx")] });
        var writes = h.Camera.Writes.Count;

        var ex = await RenewDeleteInstallTests.RunWithFileAsync(h, payload, pfx);

        Assert.Equal("cam48.pfx: 10.0.0.48 is not a CA certificate. Choose a file with CA certificates only. Nothing was changed.", ex!.Message);
        Assert.Contains("Read certificate file: Failed", h.Lines);
        Assert.Equal(writes, h.Camera.Writes.Count);

        // The dialog says so in the file's row.
        var device = new PkiFakeDevice(Guid.NewGuid());
        var vm = new InstallCertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(null)), [device], _ => pfx) { Password = "pw" };
        vm.SelectedPurpose = vm.Purposes.Single(p => p.Value == InstallPurpose.CaOnly);
        vm.SetFiles(["c:/certs/cam48.pfx"]);
        Assert.Equal("Cannot be used", vm.Files[0].Status);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    private static IssuedCertificate CurrentOf(TaskHarness h) =>
        h.Pki.Service.Issued.ForDevice(h.Device).Single(e => e.Purpose == CertificatePurpose.Https && h.Pki.Service.Issued.IsCurrent(e.SerialNumber));

    /// <summary>The HTTPS policy with an RSA key (what older AXIS OS takes).</summary>
    private static async Task UseRsaAsync(TaskHarness h)
    {
        var reply = await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, h.Pki.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)! with { Key = PolicyKeys.Rsa });
        Assert.Null(reply.Error);
    }
}
