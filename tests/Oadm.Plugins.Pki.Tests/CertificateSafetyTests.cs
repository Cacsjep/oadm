using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Fixes of the review of 2026-10-25 (docs/spec/pki.md): a server's certificate cannot be revoked while its policy shows it,
/// the server certificate choices follow the revocation list, an imported CA whose key cannot be read takes it again, and a
/// user limited to a device group sees its camera's certificate by the camera's serial and no policy names.
/// </summary>
public sealed class CertificateSafetyTests
{
    private static async Task<(string Id, string Serial)> SharedServerCertificateAsync(PkiHarness pki)
    {
        await pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None);
        var id = pki.Policies.EstServerPolicy!.CertificateId!;
        return (id, pki.Service.Local.Find(id)!.Serial);
    }

    [Fact]
    public async Task The_certificate_a_server_policy_shows_cannot_be_revoked()
    {
        await using var pki = await PkiHarness.StartAsync();
        var (_, serial) = await SharedServerCertificateAsync(pki);

        var reply = await pki.InvokeAsync<RevokeReply>(PkiMethods.Revoke, new RevokeRequest([serial], RevocationReason.Other));

        Assert.Equal("Used by the policy EST server. Pick another certificate first.", reply.Error);
        Assert.False(pki.Service.Issued.IsRevoked(serial));
    }

    [Fact]
    public async Task The_server_certificate_choices_follow_a_revocation_at_once()
    {
        await using var pki = await PkiHarness.StartAsync();
        var (id, serial) = await SharedServerCertificateAsync(pki);
        Assert.Null(pki.Service.ServerCertificateChoices().Single(c => c.Id == id).Problem); // read and cached

        await pki.Service.RevokeSerialsAsync([serial], RevocationReason.KeyCompromised, "admin", CancellationToken.None);

        Assert.Equal("This certificate is revoked. Choose another one.", pki.Service.ServerCertificateChoices().Single(c => c.Id == id).Problem);
    }

    [Fact]
    public async Task An_imported_ca_whose_key_cannot_be_read_takes_its_key_again()
    {
        var settings = new InMemoryPluginSettingsProvider();
        using var root = TestCa.Root();
        using var intermediate = TestCa.Intermediate(root);
        using var rootPublic = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(root.RawData);
        var pfx = Convert.ToBase64String(TestCa.Pfx("secret", intermediate, rootPublic));
        var id = PkiService.ImportedAuthorityId(CaCertificates.Fingerprint(intermediate));
        await using (var first = await PkiHarness.StartAsync(settings))
        {
            Assert.True((await first.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(pfx, "acme.pfx", "secret", null, null))).Ok);
        }

        // Another master key (a backup restored elsewhere): the CA stays listed but cannot issue.
        await using var pki = await PkiHarness.StartAsync(settings, TestSecrets.Create());
        Assert.Null(pki.Service.IssuingCa(id));

        var again = await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(pfx, "acme.pfx", "secret", null, null));

        Assert.True(again.Ok);
        Assert.NotNull(pki.Service.IssuingCa(id));
        Assert.Single(pki.Service.Authorities(), a => a.Kind == CertificateAuthorityKind.Imported);
        Assert.Equal("This CA is already in the list.", (await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(pfx, "acme.pfx", "secret", null, null))).Errors![PkiFields.File]);
    }

    [Fact]
    public async Task A_group_user_opens_its_cameras_certificate_by_the_cameras_serial_and_gets_no_policy_names()
    {
        var ctx = new FakeCoreContext { Secrets = TestSecrets.Create() };
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F631339" });
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        await plugin.Service!.Background;
        var authority = ctx.FakeServices.Find<IDeviceCertificateAuthority>()!;
        var before = await RevocationTests.SignAsync(authority, Guid.NewGuid(), "B8A44F631339"); // issued while the camera had another id
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        var group = FakeCallContext.WithGroup(ctx, [inside.Id], "tech2", key);

        var listed = PkiJson.Deserialize<CertificatesReply>(await ctx.InvokeAsync(plugin, group, PkiMethods.GetCertificates, "{}"));
        Assert.Equal(CertificateSerials.Normalize(before.SerialNumber), Assert.Single(listed.Rows).SerialNumber);
        var details = PkiJson.Deserialize<CertificateDetailsReply>(await ctx.InvokeAsync(plugin, group, PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(before.SerialNumber))));
        Assert.Null(details.Error);

        // Used by of the CAs: the policies are server-wide, a group's users get no names of them.
        var seen = PkiJson.Deserialize<PkiState>(await ctx.InvokeAsync(plugin, group, PkiMethods.GetState, null));
        Assert.DoesNotContain(seen.Authorities.Single(a => a.Id == CertificateAuthorityInfo.OadmId).UsedBy, u => u.StartsWith("Issuing", StringComparison.Ordinal));
        var full = PkiJson.Deserialize<PkiState>(await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), PkiMethods.GetState, null));
        Assert.Contains(full.Authorities.Single(a => a.Id == CertificateAuthorityInfo.OadmId).UsedBy, u => u.StartsWith("Issuing", StringComparison.Ordinal));
    }
}
