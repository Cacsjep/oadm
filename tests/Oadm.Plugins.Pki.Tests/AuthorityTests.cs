using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>PKI's list of CAs (docs/spec/pki.md "Certificate authorities"): Authorities, SetUsedBy, Import CA, Remove.</summary>
public sealed class AuthorityTests
{
    private static ImportAuthorityRequest Request(byte[] bytes) => new(Convert.ToBase64String(bytes), "ca.crt");

#pragma warning disable CA1859 // The services see the SDK interface (its default members).
    private static IDeviceCertificateAuthority Offer(PkiHarness pki) => new PkiCertificateAuthority(pki.Service);
#pragma warning restore CA1859

    [Fact]
    public async Task The_list_holds_the_oadm_ca_previous_ones_the_axis_device_id_and_imported_cas_with_stable_ids()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ca = Offer(pki);
        var first = pki.Service.Ca!.Id;

        var initial = ca.Authorities;
        Assert.Equal([CertificateAuthorityInfo.OadmId, CertificateAuthorityInfo.AxisDeviceIdId], initial.Select(a => a.Id));
        var oadm = initial[0];
        Assert.Equal(CertificateAuthorityKind.Oadm, oadm.Kind);
        Assert.True(oadm.Issues);
        Assert.Equal("OADM Root CA SERVER01", oadm.Name);
        Assert.Equal(first, Convert.ToHexString(SHA256.HashData(oadm.Certificates[0])));
        var axis = initial[1];
        Assert.Equal(CertificateAuthorityKind.AxisDeviceId, axis.Kind);
        Assert.False(axis.Issues);
        Assert.True(axis.Certificates.Count >= 2); // the RSA and ECC roots (and their intermediates)
        Assert.True(axis.NotAfterUtc > DateTime.UtcNow);
        Assert.Same(initial, ca.Authorities); // built once per change

        await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("Second", null, 10, true));
        using var root = TestCa.Root("Contoso Root");
        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, Request(root.RawData));
        Assert.True(reply.Ok);

        var list = ca.Authorities;
        Assert.Equal(
            [CertificateAuthorityInfo.OadmId, "oadm-previous:" + first, CertificateAuthorityInfo.AxisDeviceIdId, "imported:" + CaCertificates.Fingerprint(root)],
            list.Select(a => a.Id));
        Assert.Equal(CertificateAuthorityKind.PreviousOadm, list[1].Kind);
        Assert.Equal("OADM Root CA SERVER01", list[1].Name);
        Assert.Equal("Contoso Root", list[3].Name);
        Assert.Equal(CertificateAuthorityKind.Imported, list[3].Kind);
        Assert.Equal(root.RawData, list[3].Certificates[0]);

        // The page shows the same rows.
        var state = reply.State!;
        Assert.Equal(list.Select(a => a.Id), state.Authorities.Select(a => a.Id));
        Assert.All(state.Authorities, row => Assert.StartsWith("-----BEGIN CERTIFICATE-----", row.CertificatesPem[0], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changed_is_raised_when_the_list_changes_and_imported_cas_survive_a_restart()
    {
        var settings = new InMemoryPluginSettingsProvider();
        var secrets = TestSecrets.Create();
        using var root = TestCa.Root("Contoso Root");
        await using (var pki = await PkiHarness.StartAsync(settings, secrets))
        {
            var ca = Offer(pki);
            var changes = 0;
            ca.Changed += (_, _) => Interlocked.Increment(ref changes);

            Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, Request(Encoding.ASCII.GetBytes(root.ExportCertificatePem())))).Ok);
            Assert.Equal(1, changes);
            Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest("imported:" + CaCertificates.Fingerprint(root)))).Ok);
            Assert.Equal(2, changes);
            Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, Request(root.RawData))).Ok); // DER
            Assert.Equal(3, changes);
        }

        await using var again = await PkiHarness.StartAsync(settings, secrets);
        Assert.Contains(Offer(again).Authorities, a => a.Id == "imported:" + CaCertificates.Fingerprint(root));
    }

    [Fact]
    public async Task Import_checks_the_file_and_answers_below_the_field()
    {
        await using var pki = await PkiHarness.StartAsync();
        async Task<string?> ErrorOf(byte[] bytes) =>
            (await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, Request(bytes))).Errors?.GetValueOrDefault(PkiFields.AuthorityFile);

        Assert.Equal("The file is not a certificate (.crt or .cer, PEM or DER).", await ErrorOf("garbage"u8.ToArray()));
        Assert.Equal("The file cannot be read.", (await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(null, null))).Errors![PkiFields.AuthorityFile]);
        Assert.Equal("The file is larger than 1 MB.", await ErrorOf(new byte[PkiPluginInfo.MaxImportBytes + 1]));

        using var leaf = TestCa.Root("Not a CA", ca: false);
        Assert.Equal("This certificate is not a CA certificate (it cannot issue certificates).", await ErrorOf(leaf.RawData));
        using var expired = TestCa.Root("Old", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-2));
        Assert.Equal("This certificate has expired.", await ErrorOf(expired.RawData));

        using var withKey = TestCa.Root("With key");
        var pem = withKey.ExportCertificatePem() + "\n" + withKey.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();
        Assert.Equal("The file contains a private key. Choose the certificate only.", await ErrorOf(Encoding.ASCII.GetBytes(pem)));

        // The OADM CA and a CA already in the list.
        Assert.Equal("This CA is already in the list.", await ErrorOf(pki.Service.Ca!.Certificate.RawData));
        using var root = TestCa.Root("Contoso Root");
        Assert.Null(await ErrorOf(root.RawData));
        Assert.Equal("This CA is already in the list.", await ErrorOf(root.RawData));
    }

    [Fact]
    public async Task SetUsedBy_shows_used_by_and_refuses_to_remove_a_used_ca()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ca = Offer(pki);
        using var root = TestCa.Root("Contoso Root");
        await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, Request(root.RawData));
        var id = "imported:" + CaCertificates.Fingerprint(root);

        ca.SetUsedBy("oadm.radius", "802.1X server", [id, CertificateAuthorityInfo.OadmId]);
        ca.SetUsedBy("oadm.est", "EST server", [CertificateAuthorityInfo.OadmId]);
        var state = await pki.StateAsync();
        Assert.Equal(["Issuing: HTTPS", "Issuing: IEEE 802.1X", "Issuing: 802.1X server", "Issuing: EST server", "802.1X server", "EST server"],
            state.Authorities.Single(a => a.Id == CertificateAuthorityInfo.OadmId).UsedBy);
        Assert.Equal(["802.1X server"], state.Authorities.Single(a => a.Id == id).UsedBy);
        Assert.Empty(state.Authorities.Single(a => a.Id == CertificateAuthorityInfo.AxisDeviceIdId).UsedBy);

        var refused = await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest(id));
        Assert.False(refused.Ok);
        Assert.Equal("Used by the 802.1X server. Remove it there first.", refused.Error);
        ca.SetUsedBy("oadm.est", "EST server", [id]);
        Assert.Equal("Used by the 802.1X server and the EST server. Remove it there first.",
            (await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest(id))).Error);

        // An empty list clears the service; then Remove works.
        ca.SetUsedBy("oadm.radius", "802.1X server", []);
        ca.SetUsedBy("oadm.est", "EST server", []);
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest(id))).Ok);
        Assert.DoesNotContain(ca.Authorities, a => a.Id == id);

        // Only imported CAs can be removed; a gone one says so.
        Assert.Equal("Only imported CAs can be removed here.", (await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest(CertificateAuthorityInfo.OadmId))).Error);
        Assert.Equal("This CA is no longer in the list.", (await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest(id))).Error);
    }

    [Fact]
    public async Task SetUsedBy_publishes_the_state_only_when_it_changed()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ca = Offer(pki);
        var events = 0;
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            await foreach (var item in pki.Hub.WatchAsync(PkiPluginInfo.PluginId, stop.Token))
            {
                if (item.Topic == PkiMethods.StateTopic)
                {
                    Interlocked.Increment(ref events);
                }
            }
        });
        await Eventually.TrueAsync(() => pki.Hub.WatcherCount(PkiPluginInfo.PluginId) == 1, "the watcher");

        ca.SetUsedBy("oadm.est", "EST server", [CertificateAuthorityInfo.OadmId]);
        await Eventually.TrueAsync(() => Volatile.Read(ref events) == 1, "the state event");
        ca.SetUsedBy("oadm.est", "EST server", [CertificateAuthorityInfo.OadmId]); // the same: nothing
        ca.SetUsedBy("oadm.est", "EST server", [CertificateAuthorityInfo.AxisDeviceIdId]);
        await Eventually.TrueAsync(() => Volatile.Read(ref events) == 2, "the second state event");
        Assert.Equal(2, Volatile.Read(ref events));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
    }

    [Fact]
    public async Task Import_and_remove_need_manage_and_all_devices()
    {
        await using var pki = await PkiHarness.StartAsync();
        Assert.Equal(UserRole.Admin, pki.Plugin.RequiredRole(PkiMethods.ImportAuthority));
        Assert.Equal(UserRole.Admin, pki.Plugin.RequiredRole(PkiMethods.RemoveAuthority));
        Assert.True(((ICorePlugin)pki.Plugin).IsAudited(PkiMethods.ImportAuthority));
        Assert.True(((ICorePlugin)pki.Plugin).IsAudited(PkiMethods.RemoveAuthority));
        Assert.Null(pki.Plugin.RequiredPermission(PkiMethods.ImportAuthority)); // the plugin's "manage"
        Assert.True(pki.Plugin.RequiresAllDevices(PkiMethods.ImportAuthority));
        Assert.True(pki.Plugin.RequiresAllDevices(PkiMethods.RemoveAuthority));
    }

    [Fact]
    public async Task The_page_lists_the_cas_views_imports_and_removes()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var dialogs = new FakeDialogs();
        var settingsFile = Path.Combine(Path.GetTempPath(), "oadm-pki-authorities-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var vm = new PkiViewModel(ctx, new FakeClientTrust(), new PkiClientSettingsStore(settingsFile)) { Dialogs = dialogs };
            await vm.LoadAsync();
            Assert.Equal(["OADM CA", "Axis device ID"], vm.Authorities.Select(a => a.KindText));
            // Used by: the issuing policies name the OADM CA (they issue with it); the Axis device ID is used by nothing yet.
            Assert.Equal(["Issuing: HTTPS, Issuing: IEEE 802.1X, Issuing: 802.1X server, Issuing: EST server", "-"], vm.Authorities.Select(a => a.UsedBy));
            Assert.All(vm.Authorities, row => Assert.False(row.IsRemovable));

            // View: the shared certificate window with the CA.
            await vm.ViewAuthorityCommand.ExecuteAsync(vm.Authorities[0]);
            Assert.Equal("OADM Root CA SERVER01", Assert.Single(dialogs.Shown).Title);

            // Import CA: a wrong file shows its error below the field, the dialog stays open.
            using var root = TestCa.Root("Contoso Root");
            using var leaf = TestCa.Root("Not a CA", ca: false);
            ImportAuthorityViewModel? dialog = null;
            dialogs.Drive = async d =>
            {
                dialog = (ImportAuthorityViewModel)d;
                dialog.SetFile(new PickedFile("leaf.crt", leaf.RawData));
                await dialog.ImportCommand.ExecuteAsync(null);
            };
            await vm.ImportAuthorityCommand.ExecuteAsync(null);
            Assert.Equal("This certificate is not a CA certificate (it cannot issue certificates).", dialog!.FileError);
            Assert.False(dialog.Completed);

            dialogs.Drive = async d =>
            {
                dialog = (ImportAuthorityViewModel)d;
                dialog.SetFile(new PickedFile("root.crt", root.RawData));
                await dialog.ImportCommand.ExecuteAsync(null);
            };
            await vm.ImportAuthorityCommand.ExecuteAsync(null);
            Assert.True(dialog!.Completed);
            var imported = vm.Authorities.Single(a => a.IsImported);
            Assert.Equal("Contoso Root", imported.Name);
            Assert.Equal("Imported CA", imported.KindText);
            Assert.True(imported.IsRemovable);

            // Remove: refused while a service uses it, with the reason.
            Offer(pki).SetUsedBy("oadm.radius", "802.1X server", [imported.Id]);
            await vm.LoadAsync();
            imported = vm.Authorities.Single(a => a.IsImported);
            Assert.Equal("802.1X server", imported.UsedBy);
            await vm.RemoveAuthorityCommand.ExecuteAsync(imported);
            Assert.Equal(("Remove CA", "Used by the 802.1X server. Remove it there first."), ctx.Messages[^1]);
            Assert.Contains(vm.Authorities, a => a.IsImported);

            Offer(pki).SetUsedBy("oadm.radius", "802.1X server", []);
            await vm.RemoveAuthorityCommand.ExecuteAsync(vm.Authorities.Single(a => a.IsImported));
            Assert.DoesNotContain(vm.Authorities, a => a.IsImported);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }
}
