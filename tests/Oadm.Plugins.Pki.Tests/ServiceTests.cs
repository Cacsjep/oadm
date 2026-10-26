using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Core.Vapix;
using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The core plugin against in-memory settings, a real secret protector, the event hub and the trust anchor registry.</summary>
public sealed class ServiceTests
{
    [Fact]
    public async Task First_start_generates_the_default_ca_stores_it_encrypted_and_trusts_it()
    {
        await using var pki = await PkiHarness.StartAsync();
        var state = await pki.StateAsync();

        Assert.Equal("OADM Root CA SERVER01", state.Ca!.CommonName);
        Assert.Equal(CaSource.Generated, state.Ca.Source);
        Assert.Equal(ServiceStatus.Ok, state.Status.Kind);
        Assert.Equal($"CA valid until {state.Ca.NotAfterUtc:yyyy-MM-dd}", state.Status.Text);
        Assert.Equal(DateTime.UtcNow.AddYears(10).Date, state.Ca.NotAfterUtc.Date);
        Assert.False(state.KeyUnreadable);
        Assert.False(state.IsGenerating);
        Assert.False(state.ServerTrustInstalled);
        Assert.Empty(state.PreviousCas);

        // Stored: the key only encrypted (purpose bound to the CA id).
        var stored = PkiJson.Deserialize<StoredCa>(await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
        Assert.Equal(state.Ca.Id, stored.Id);
        Assert.DoesNotContain("PRIVATE KEY", stored.KeyProtected, StringComparison.Ordinal);
        Assert.Contains("PRIVATE KEY", pki.Secrets!.Unprotect(stored.KeyProtected, "pki:ca:" + stored.Id), StringComparison.Ordinal);

        // The server rates certificates of this CA as trusted.
        Assert.Equal(state.Ca.Id, CaCertificates.Fingerprint(Assert.Single(pki.Anchors.Current.Certificates)));
        using var signer = pki.Service.Ca!.WithPrivateKey();
        using var leaf = TestCa.Leaf(signer);
        Assert.Equal(CertificateTrust.Trusted, CertificateTrustEvaluator.EvaluateChain(leaf, trustAnchors: pki.Anchors.Current.Certificates));
    }

    [Fact]
    public async Task Restart_loads_the_stored_ca_and_another_master_key_is_an_error_without_overwriting()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        string id;
        await using (var first = await PkiHarness.StartAsync(settings, secrets))
        {
            id = (await first.StateAsync()).Ca!.Id;
        }

        await using (var again = await PkiHarness.StartAsync(settings, secrets))
        {
            Assert.Equal(id, (await again.StateAsync()).Ca!.Id);
        }

        var before = await settings.GetSettings(PkiPluginInfo.PluginId).GetAsync(PkiStore.CaKey, CancellationToken.None);
        await using var other = await PkiHarness.StartAsync(settings, TestSecrets.Create());
        var state = await other.StateAsync();
        Assert.True(state.KeyUnreadable);
        Assert.Equal(ServiceStatus.Error, state.Status.Kind);
        Assert.Equal("CA key cannot be read", state.Status.Text);
        Assert.Equal(id, state.Ca!.Id); // the public part is still shown
        Assert.Equal(before, await other.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));

        var backup = await other.InvokeAsync<FileReply>(PkiMethods.Backup, new BackupRequest("long enough"));
        Assert.Equal("The CA key cannot be read, so it cannot be backed up.", backup.Error);
    }

    [Fact]
    public async Task Without_secret_protection_no_ca_is_kept_and_the_status_says_so()
    {
        await using var pki = await PkiHarness.StartAsync(noSecrets: true);
        var state = await pki.StateAsync();

        Assert.True(state.Unavailable);
        Assert.Null(state.Ca);
        Assert.Equal("CA cannot be stored on this server", state.Status.Text);
        Assert.Null(await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
        Assert.NotNull((await pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority, new CreateCaRequest("CA", false, null, CaKeys.Default, 10))).Error);
    }

    [Fact]
    public async Task Import_with_its_key_adds_a_ca_with_field_errors_first_and_keeps_the_intermediate_chain()
    {
        await using var pki = await PkiHarness.StartAsync();
        var oadm = pki.Service.Ca!.Id;
        var bad = await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String([1, 2, 3]), "x.pfx", "pw", null, null));
        Assert.Equal(CaImporter.WrongPassword, bad.Errors![PkiFields.Password]);
        Assert.Equal("The file cannot be read.", (await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest("not base64!", null, null, null, null))).Errors![PkiFields.File]);

        using var root = TestCa.Root();
        using var intermediate = TestCa.Intermediate(root);
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);
        var pfx = TestCa.Pfx("secret", intermediate, rootPublic);
        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(pfx), "acme.pfx", "secret", null, null));

        // Decision 2026-10-25: the CA is added to the list; the OADM CA stays as it was (nothing asked, nothing replaced).
        Assert.True(reply.Ok);
        var state = reply.State!;
        Assert.Equal((oadm, CaSource.Generated), (state.Ca!.Id, state.Ca.Source));
        Assert.Empty(state.PreviousCas);
        var imported = Assert.Single(pki.Service.Authorities(), a => a.Kind == Oadm.Sdk.Certificates.CertificateAuthorityKind.Imported);
        Assert.True(imported.Issues);
        Assert.Equal([intermediate.RawData, root.RawData], imported.Certificates); // the chain kept at import

        // Anchors: the OADM CA, the intermediate and its root; renewals pin the CAs that issue (not their chains).
        Assert.Equal(3, pki.Anchors.Current.Certificates.Count);
        Assert.Equal(2, pki.Anchors.Current.RenewalIssuers.Count);
        using var leaf = TestCa.Leaf(intermediate);
        Assert.Equal(CertificateTrust.Trusted, CertificateTrustEvaluator.EvaluateChain(leaf, trustAnchors: pki.Anchors.Current.Certificates));
        Assert.Equal("This CA is already in the list.", (await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(pfx), "acme.pfx", "secret", null, null))).Errors![PkiFields.File]);
    }

    [Fact]
    public async Task Export_public_pem_and_der_previous_cas_and_removing_them()
    {
        await using var pki = await PkiHarness.StartAsync();
        var oldId = pki.Service.Ca!.Id;

        var pem = await pki.InvokeAsync<FileReply>(PkiMethods.ExportPublic, new ExportRequest("pem"));
        Assert.Equal("OADM Root CA SERVER01.crt", pem.FileName);
        var pemText = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(pem.DataBase64!));
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pemText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", pemText, StringComparison.Ordinal);

        var der = await pki.InvokeAsync<FileReply>(PkiMethods.ExportPublic, new ExportRequest("der"));
        Assert.Equal("OADM Root CA SERVER01.cer", der.FileName);
        using (var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(der.DataBase64!)))
        {
            Assert.Equal(oldId, CaCertificates.Fingerprint(cert));
        }

        Assert.NotNull((await pki.InvokeAsync<FileReply>(PkiMethods.ExportPublic, new ExportRequest("zip"))).Error);

        Assert.Equal("This previous CA no longer exists.", (await pki.InvokeAsync<FileReply>(PkiMethods.ExportPrevious, new ExportPreviousRequest("nope", "pem"))).Error);
    }

    [Fact]
    public async Task Backup_is_password_protected_and_restores_on_another_server()
    {
        await using var pki = await PkiHarness.StartAsync();
        var id = pki.Service.Ca!.Id;

        Assert.Equal("Use at least 8 characters.", (await pki.InvokeAsync<FileReply>(PkiMethods.Backup, new BackupRequest("short"))).Errors![PkiFields.Password]);
        var backup = await pki.InvokeAsync<FileReply>(PkiMethods.Backup, new BackupRequest("correct horse"));
        Assert.Equal("OADM Root CA SERVER01.pfx", backup.FileName);
        var bytes = Convert.FromBase64String(backup.DataBase64!);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => X509CertificateLoader.LoadPkcs12(bytes, "wrong"));

        // On another server the backup is a CA with its key: it is added to the list and can issue (decision 2026-10-25:
        // an import never replaces that server's own OADM CA).
        await using var other = await PkiHarness.StartAsync();
        var restored = await other.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(backup.DataBase64, backup.FileName, "correct horse", null, null));
        Assert.True(restored.Ok);
        Assert.NotEqual(id, restored.State!.Ca!.Id);
        Assert.NotNull(other.Service.IssuingCa(PkiService.ImportedAuthorityId(id)));
    }

    [Fact]
    public async Task Install_server_trust_reports_success_and_failures()
    {
        await using var pki = await PkiHarness.StartAsync();

        pki.Trust.FailWith = "Permission denied: the server must run as administrator / root";
        var failed = await pki.InvokeAsync<InstallReply>(PkiMethods.InstallServerTrust);
        Assert.False(failed.Installed);
        Assert.Equal(pki.Trust.FailWith, failed.Error);
        Assert.Equal(pki.Trust.FailWith, failed.State!.ServerTrustError);

        pki.Trust.FailWith = null;
        var ok = await pki.InvokeAsync<InstallReply>(PkiMethods.InstallServerTrust);
        Assert.True(ok.Installed);
        Assert.True(ok.State!.ServerTrustInstalled);
        Assert.Null(ok.State.ServerTrustError);
        Assert.True((await pki.StateAsync()).ServerTrustInstalled);
    }

    [Fact]
    public async Task The_radius_server_ca_is_a_ca_of_the_list_and_stays_while_a_policy_names_it()
    {
        // Decision 2026-10-25: no import of its own; the CA is imported on the Certificates page and picked by its id.
        await using var pki = await PkiHarness.StartAsync();
        using var radius = TestCa.Root("Corp RADIUS CA");
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(radius.RawData), "radius.crt"))).Ok);
        var id = "imported:" + Ca.CaCertificates.Fingerprint(radius);

        var policy = pki.Policies.FirstOf(Policies.PolicyKinds.Dot1xCertificate)!;
        Assert.Equal(Oadm.Sdk.Certificates.CertificateAuthorityInfo.OadmId, policy.RadiusCaId); // a new server: the OADM CA
        var unknown = await pki.InvokeAsync<Policies.PolicySaveReply>(Policies.PolicyMethods.SavePolicy, policy with { RadiusCaId = "imported:00" });
        Assert.Equal((Policies.PolicyFields.RadiusCa, Policies.PolicyTexts.ChooseRadiusCa), (unknown.Error!.Field, unknown.Error.Message));

        var saved = await pki.InvokeAsync<Policies.PolicySaveReply>(Policies.PolicyMethods.SavePolicy, policy with { RadiusCaId = id });
        Assert.Null(saved.Error);
        Assert.Equal(id, pki.Policies.Find(policy.Id)!.RadiusCaId);

        // Used: it cannot be removed.
        var refused = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest("ca:" + Ca.CaCertificates.Fingerprint(radius)));
        Assert.Equal(Policies.PolicyTexts.UsedByPolicy(policy.Name), refused.Error);
    }

    [Fact]
    public void Status_texts_follow_the_ca_validity()
    {
        using var ca = CaGenerator.Generate("CA", null, 1, DateTimeOffset.UtcNow, keySize: 2048);
        var end = ca.Certificate.NotAfter.ToUniversalTime();

        Assert.Equal(new ServiceStatus(ServiceStatus.Ok, $"CA valid until {end:yyyy-MM-dd}"), PkiService.StatusOf(false, false, null, ca, end.AddDays(-100), 30));
        Assert.Equal("CA expires in 20 days", PkiService.StatusOf(false, false, null, ca, end.AddDays(-20.5), 30).Text);
        Assert.Equal(ServiceStatus.Warning, PkiService.StatusOf(false, false, null, ca, end.AddDays(-20.5), 30).Kind);
        Assert.Equal("CA expires in 1 day", PkiService.StatusOf(false, false, null, ca, end.AddDays(-1.5), 30).Text);
        Assert.Equal("CA expires today", PkiService.StatusOf(false, false, null, ca, end.AddHours(-2), 30).Text);
        Assert.Equal(new ServiceStatus(ServiceStatus.Error, "CA expired", $"Expired on {end:yyyy-MM-dd}. Use New OADM CA."), PkiService.StatusOf(false, false, null, ca, end.AddDays(1), 30));
        Assert.Equal("Creating the certificate authority", PkiService.StatusOf(false, true, null, null, end, 30).Text);
        using var noKey = new CaMaterial(X509CertificateLoader.LoadCertificate(ca.Certificate.RawData), [], null);
        Assert.Equal("CA key cannot be read", PkiService.StatusOf(false, false, null, noKey, end.AddDays(-100), 30).Text);
    }

    [Fact]
    public async Task Default_ca_is_created_in_the_background_with_a_progress_state()
    {
        await using var pki = await PkiHarness.StartAsync(waitForDefault: false);
        var early = await pki.StateAsync();
        Assert.True(early.IsGenerating || early.Ca is not null);
        await pki.Service.Background;
        var state = await pki.StateAsync();
        Assert.False(state.IsGenerating);
        Assert.NotNull(state.Ca);
    }

    [Fact]
    public async Task Device_certificate_validity_is_shortened_to_the_ca_end()
    {
        await using var pki = await PkiHarness.StartAsync();
        Assert.Equal(TimeSpan.FromDays(365), pki.Service.DeviceCertificateValidity());
        await pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority, new CreateCaRequest("Short", false, null, CaKeys.Default, 1));
        Assert.InRange(pki.Service.DeviceCertificateValidity(3650).TotalDays, 360, 367);
    }

    [Fact]
    public void Issued_counts_for_30000_devices_are_one_pass() => CountTheMostDevices();

    [Fact]
    [Trait("Category", "Perf")]
    public void Issued_counts_for_30000_devices_are_fast()
    {
        var elapsed = CountTheMostDevices();
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"took {elapsed.TotalMilliseconds:0} ms");
    }

    private static TimeSpan CountTheMostDevices()
    {
        var now = DateTime.UtcNow;
        var issued = Enumerable.Range(0, 30_000).SelectMany(i => new[]
        {
            new IssuedCertificate { SerialNumber = "a" + i, DeviceId = new Guid(i, 0, 0, new byte[8]), Purpose = CertificatePurpose.Https, CaId = i % 10 == 0 ? "OLD" : "NEW", IssuedUtc = now.AddDays(-1), NotAfterUtc = now.AddDays(i % 100 == 0 ? 5 : 300) },
            new IssuedCertificate { SerialNumber = "b" + i, DeviceId = new Guid(i, 0, 0, new byte[8]), Purpose = CertificatePurpose.Dot1x, CaId = "NEW", IssuedUtc = now.AddDays(-1), NotAfterUtc = now.AddDays(300) },
        }).ToList();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var counts = IssuedCounts.From(issued, "NEW", now, 30);
        watch.Stop();

        Assert.Equal(30_000, counts.Devices);
        Assert.Equal(30_000, counts.WithCurrentCa);
        Assert.Equal(3000, counts.WithPreviousCa);
        Assert.Equal(300, counts.ExpiringSoon);
        Assert.Equal(3000, counts.DevicesPerCa["OLD"]);
        return watch.Elapsed;
    }
}
