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

        // Generate (or import) replaces it; the unreadable one becomes a previous CA.
        var ask = await other.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("New CA", null, 5, Confirmed: false));
        Assert.True(ask.NeedsConfirmation); // replacing always needs the user's confirmation (count 0 here)
        Assert.Equal(0, ask.DevicesWithCurrentCa);
        var reply = await other.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("New CA", null, 5, Confirmed: true));
        Assert.True(reply.Ok);
        Assert.False(reply.State!.KeyUnreadable);
        Assert.Equal(id, Assert.Single(reply.State.PreviousCas).Id);
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
        Assert.NotNull((await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("CA", null, 10, true))).Error);
    }

    [Fact]
    public async Task Generate_validates_fields_and_asks_before_replacing_a_ca_with_issued_devices()
    {
        await using var pki = await PkiHarness.StartAsync();
        var oldId = pki.Service.Ca!.Id;

        var invalid = await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest(" ", new string('x', 65), 31, true));
        Assert.Equal("Enter a name.", invalid.Errors![PkiFields.CommonName]);
        Assert.Equal("At most 64 characters.", invalid.Errors[PkiFields.Organization]);
        Assert.Equal("Enter a whole number from 1 to 30.", invalid.Errors[PkiFields.ValidityYears]);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await pki.SetIssuedAsync(
        [
            new IssuedCertificate { SerialNumber = "01", DeviceId = a, Purpose = CertificatePurpose.Https, CaId = oldId, IssuedUtc = DateTime.UtcNow.AddDays(-3), NotAfterUtc = DateTime.UtcNow.AddDays(300) },
            new IssuedCertificate { SerialNumber = "02", DeviceId = a, Purpose = CertificatePurpose.Dot1x, CaId = oldId, IssuedUtc = DateTime.UtcNow.AddDays(-3), NotAfterUtc = DateTime.UtcNow.AddDays(10) },
            new IssuedCertificate { SerialNumber = "03", DeviceId = b, Purpose = CertificatePurpose.Https, CaId = oldId, IssuedUtc = DateTime.UtcNow.AddDays(-2), NotAfterUtc = DateTime.UtcNow.AddDays(300) },
        ]);
        Assert.Equal(2, (await pki.InvokeAsync<PreviewReplaceReply>(PkiMethods.PreviewReplace)).DevicesWithCurrentCa);
        var before = await pki.StateAsync();
        Assert.Equal(2, before.IssuedDevices);
        Assert.Equal(2, before.DevicesWithCurrentCa);
        Assert.Equal(1, before.ExpiringSoon);

        var ask = await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("Site CA", "Acme", 3, Confirmed: false));
        Assert.True(ask.NeedsConfirmation);
        Assert.Equal(2, ask.DevicesWithCurrentCa);
        Assert.Equal(oldId, pki.Service.Ca!.Id); // nothing changed

        using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events = pki.Hub.WatchAsync(PkiPluginInfo.PluginId, watch.Token).GetAsyncEnumerator(watch.Token);
        var next = events.MoveNextAsync();
        await Wait.UntilAsync(() => pki.Hub.WatcherCount(PkiPluginInfo.PluginId) == 1);
        var done = await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("Site CA", "Acme", 3, Confirmed: true));
        Assert.True(done.Ok);
        Assert.True(await next);
        Assert.Equal(PkiMethods.StateTopic, events.Current.Topic);
        await events.DisposeAsync();

        var state = done.State!;
        Assert.Equal("CN=Site CA, O=Acme", state.Ca!.Subject);
        Assert.Equal("Acme", state.Ca.Organization);
        Assert.Equal(DateTime.UtcNow.AddYears(3).Date, state.Ca.NotAfterUtc.Date);
        var previous = Assert.Single(state.PreviousCas);
        Assert.Equal(oldId, previous.Id);
        Assert.Equal("OADM Root CA SERVER01", previous.CommonName);
        Assert.Equal(2, previous.Devices);
        Assert.Equal(0, state.DevicesWithCurrentCa);
        Assert.Equal(2, state.DevicesWithPreviousCa);

        // Both CAs are trusted during the transition.
        Assert.Equal(2, pki.Anchors.Current.Certificates.Count);
    }

    [Fact]
    public async Task Import_replaces_the_ca_with_field_errors_first_and_keeps_the_intermediate_chain()
    {
        await using var pki = await PkiHarness.StartAsync();
        var bad = await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String([1, 2, 3]), "x.pfx", "pw", null, null, true));
        Assert.Equal(CaImporter.WrongPassword, bad.Errors![PkiFields.Password]);
        Assert.Equal("The file cannot be read.", (await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest("not base64!", null, null, null, null, true))).Errors![PkiFields.File]);

        using var root = TestCa.Root();
        using var intermediate = TestCa.Intermediate(root);
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);
        var pfx = TestCa.Pfx("secret", intermediate, rootPublic);
        var ask = await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(pfx), "acme.pfx", "secret", null, null, Confirmed: false));
        Assert.True(ask.NeedsConfirmation); // checked first, then the replace confirmation
        Assert.Equal(0, ask.DevicesWithCurrentCa);
        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(pfx), "acme.pfx", "secret", null, null, Confirmed: true));

        Assert.True(reply.Ok);
        var state = reply.State!;
        Assert.Equal(CaSource.Imported, state.Ca!.Source);
        Assert.True(state.Ca.IsIntermediate);
        Assert.Equal(["Acme Root CA"], state.Ca.ChainSubjects);
        Assert.Single(state.PreviousCas);

        // Anchors: the intermediate, its root and the previous CA.
        Assert.Equal(3, pki.Anchors.Current.Certificates.Count);
        using var leaf = TestCa.Leaf(intermediate);
        Assert.Equal(CertificateTrust.Trusted, CertificateTrustEvaluator.EvaluateChain(leaf, trustAnchors: pki.Anchors.Current.Certificates));
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

        await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("Second", null, 10, true));
        var previous = await pki.InvokeAsync<FileReply>(PkiMethods.ExportPrevious, new ExportPreviousRequest(oldId, "der"));
        Assert.Equal("OADM Root CA SERVER01.cer", previous.FileName);
        Assert.Equal("This previous CA no longer exists.", (await pki.InvokeAsync<FileReply>(PkiMethods.ExportPrevious, new ExportPreviousRequest("nope", "pem"))).Error);

        var removed = await pki.InvokeAsync<PkiReply>(PkiMethods.RemovePrevious, new RemovePreviousRequest(oldId));
        Assert.True(removed.Ok);
        Assert.Empty(removed.State!.PreviousCas);
        Assert.Single(pki.Anchors.Current.Certificates);
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

        await using var other = await PkiHarness.StartAsync();
        var restored = await other.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(backup.DataBase64, backup.FileName, "correct horse", null, null, true));
        Assert.True(restored.Ok);
        Assert.Equal(id, restored.State!.Ca!.Id);
        Assert.Equal(CaSource.Imported, restored.State.Ca.Source);
        Assert.True(other.Service.Ca!.HasKey);
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
    public async Task Save_settings_validates_and_persists_and_radius_ca_import_checks_the_file()
    {
        await using var pki = await PkiHarness.StartAsync();
        var invalid = new PkiConfig
        {
            DeviceCertValidityDays = 0,
            ExpiryWarningDays = 400,
            Dot1x = new Dot1xConfig { EapolVersion = 3, Identity = Dot1xIdentity.Custom, CustomIdentity = "cam-{mac}", RadiusCa = RadiusCaSource.Imported },
        };
        var errors = (await pki.InvokeAsync<PkiReply>(PkiMethods.SaveSettings, new SaveSettingsRequest(invalid))).Errors!;
        Assert.Equal("Enter a whole number from 1 to 3650.", errors[PkiFields.DeviceCertValidityDays]);
        Assert.Equal("Enter a whole number from 1 to 365.", errors[PkiFields.ExpiryWarningDays]);
        Assert.Equal("Choose EAPOL version 1 or 2.", errors[PkiFields.EapolVersion]);
        Assert.Equal("Unknown placeholder {mac}. Use {serial} or {hostName}.", errors[PkiFields.CustomIdentity]);
        Assert.Equal("Import the CA certificate of the RADIUS server.", errors[PkiFields.RadiusCa]);

        using var radius = TestCa.Root("Corp RADIUS CA");
        Assert.Equal("The file is not a certificate (.crt or .cer, PEM or DER).", (await pki.InvokeAsync<RadiusCaReply>(PkiMethods.ImportRadiusCa, new ImportRadiusCaRequest(Convert.ToBase64String([9, 9])))).Errors![PkiFields.RadiusCa]);
        Assert.StartsWith("This is not a CA certificate.", (await pki.InvokeAsync<RadiusCaReply>(PkiMethods.ImportRadiusCa, new ImportRadiusCaRequest(Convert.ToBase64String(LeafWithConstraints(radius))))).Errors![PkiFields.RadiusCa], StringComparison.Ordinal);
        var imported = await pki.InvokeAsync<RadiusCaReply>(PkiMethods.ImportRadiusCa, new ImportRadiusCaRequest(Convert.ToBase64String(radius.RawData)));
        Assert.Equal("Corp RADIUS CA", imported.Summary!.CommonName);
        Assert.DoesNotContain("PRIVATE", imported.Pem, StringComparison.Ordinal);

        var valid = invalid with
        {
            DeviceCertValidityDays = 730,
            ExpiryWarningDays = 45,
            Dot1x = invalid.Dot1x with { EapolVersion = 2, CustomIdentity = " cam-{serial}@{hostName} ", RadiusCaPem = imported.Pem },
        };
        var saved = await pki.InvokeAsync<PkiReply>(PkiMethods.SaveSettings, new SaveSettingsRequest(valid));
        Assert.True(saved.Ok);
        Assert.Equal("cam-{serial}@{hostName}", saved.State!.Config.Dot1x.CustomIdentity);
        Assert.Equal("Corp RADIUS CA", saved.State.RadiusCa!.CommonName);
        var stored = PkiJson.Deserialize<PkiConfig>(await pki.Settings.GetAsync(PkiStore.ConfigKey, CancellationToken.None));
        Assert.Equal(730, stored.DeviceCertValidityDays);
        Assert.Equal(2, stored.Dot1x.EapolVersion);

        // Back to this CA: the imported RADIUS CA is dropped.
        var back = await pki.InvokeAsync<PkiReply>(PkiMethods.SaveSettings, new SaveSettingsRequest(valid with { Dot1x = valid.Dot1x with { RadiusCa = RadiusCaSource.Oadm } }));
        Assert.Null(back.State!.Config.Dot1x.RadiusCaPem);
        Assert.Null(back.State.RadiusCa);
    }

    [Fact]
    public async Task Previous_cas_are_capped_and_expired_ones_are_dropped()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        await using (var pki = await PkiHarness.StartAsync(settings, secrets, time: time))
        {
            for (var i = 0; i < PkiPluginInfo.MaxPreviousCas + 1; i++)
            {
                Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("CA " + i, null, 1, true))).Ok);
            }

            var state = await pki.StateAsync();
            Assert.Equal(PkiPluginInfo.MaxPreviousCas, state.PreviousCas.Count);
            Assert.Equal("CA 9", state.PreviousCas[0].CommonName); // newest first
        }

        // Two years later the one-year CAs have expired: dropped on start (the 10-year default fell off the list of 10
        // before). The active one is expired too.
        time.Advance(TimeSpan.FromDays(800));
        await using var later = await PkiHarness.StartAsync(settings, secrets, time: time);
        var after = await later.StateAsync();
        Assert.Empty(after.PreviousCas);
        Assert.Equal("CA expired", after.Status.Text);
        Assert.Empty(PkiJson.Deserialize<List<StoredPreviousCa>>(await later.Settings.GetAsync(PkiStore.PreviousCasKey, CancellationToken.None)));
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
        Assert.Equal(new ServiceStatus(ServiceStatus.Error, "CA expired", $"Expired on {end:yyyy-MM-dd}. Generate a new CA or import one."), PkiService.StatusOf(false, false, null, ca, end.AddDays(1), 30));
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
        await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("Short", null, 1, true));
        await pki.InvokeAsync<PkiReply>(PkiMethods.SaveSettings, new SaveSettingsRequest(new PkiConfig { DeviceCertValidityDays = 3650 }));
        Assert.InRange(pki.Service.DeviceCertificateValidity().TotalDays, 360, 367);
    }

    private static byte[] LeafWithConstraints(X509Certificate2 issuer)
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest("CN=radius.example", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var leaf = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), [1, 2, 3]);
        return leaf.RawData;
    }
}
