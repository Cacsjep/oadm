using System.Security.Cryptography.X509Certificates;

using Oadm.Client.Api;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Client;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The client's fake backend (<c>--fake</c>) speaks the plugin's JSON; the page works against it.</summary>
public sealed class FakeModeTests : IDisposable
{
    private readonly FakeOadmApi _api = new(seedSampleData: false);

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Fake_backend_is_listed_and_its_state_reads_as_the_plugin_models()
    {
        Assert.Contains(await _api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == PkiPluginInfo.PluginId && p.IconKey == "shield");

        var state = PkiJson.Deserialize<PkiState>(await _api.InvokeCorePluginAsync(PkiPluginInfo.PluginId, PkiMethods.GetState, null, CancellationToken.None));
        Assert.True(state.Simulated);
        Assert.Equal("OADM Root CA FAKE-SERVER", state.Ca!.CommonName);
        Assert.Equal("ok", state.Status.Kind);
        Assert.Equal(["OADM Root CA FAKE-SERVER", "OADM Root CA OLD-SERVER", "OADM Issuing CA FAKE-SERVER"], state.Authorities.Select(a => a.Name));
        Assert.Equal([true, false, true], state.Authorities.Select(a => a.Issues));
        Assert.Equal(2, state.RootTrust.Count); // the root CAs
        Assert.Equal(12, state.IssuedDevices);
        Assert.Equal(365, state.Config.DeviceCertValidityDays);
        Assert.Equal(Dot1xIdentity.Mac, state.Config.Dot1x.Identity);

        var der = PkiJson.Deserialize<FileReply>(await Invoke(PkiMethods.ExportPublic, new ExportRequest("der")));
        using (var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(der.DataBase64!)))
        {
            Assert.Equal("CN=OADM Root CA FAKE-SERVER", cert.Subject);
        }

        var backup = PkiJson.Deserialize<FileReply>(await Invoke(PkiMethods.Backup, new BackupRequest("long enough")));
        using (var pfx = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(backup.DataBase64!), "long enough"))
        {
            Assert.True(pfx.HasPrivateKey);
        }

        Assert.True(PkiJson.Deserialize<InstallReply>(await Invoke(PkiMethods.InstallServerTrust, null)).Installed);
    }

    [Fact]
    public async Task Create_ca_makes_root_and_intermediate_cas_and_delete_ca_follows_the_plugins_refusals()
    {
        var invalid = PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.CreateAuthority, new CreateCaRequest("", true, "nope", "dsa", 0)));
        Assert.Equal([PkiFields.CommonName, PkiFields.Issuer, PkiFields.Key, PkiFields.ValidityYears], invalid.Errors!.Keys.Order(StringComparer.Ordinal));

        var root = PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.CreateAuthority, new CreateCaRequest("Site Root", false, null, CaKeys.Ecc, 10)));
        Assert.True(root.Ok);
        var rootId = Assert.Single(root.State!.Authorities, a => a.Name == "Site Root").Id;
        var intermediate = PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.CreateAuthority, new CreateCaRequest("Site Issuing", true, rootId, CaKeys.Rsa2048, 5)));
        Assert.True(intermediate.Ok);

        var cas = PkiJson.Deserialize<CertificatesReply>(await Invoke(PkiMethods.GetCertificates, new CertificatesRequest { Purpose = Oadm.Sdk.Certificates.ListedCertificateUses.Ca }));
        Assert.All(cas.Rows, r => Assert.Equal(Oadm.Sdk.Certificates.ListedCertificateKind.Authority, r.Kind));
        var siteRoot = Assert.Single(cas.Rows, r => r.Name == "Site Root");
        var siteIssuing = Assert.Single(cas.Rows, r => r.Name == "Site Issuing");
        Assert.True(siteRoot.IsRootCa);
        Assert.Equal("ECC P-256", siteRoot.KeyText);
        Assert.False(siteIssuing.IsRootCa);
        Assert.Equal("Site Root", siteIssuing.IssuedBy);
        Assert.Equal("RSA 2048", siteIssuing.KeyText);
        Assert.Contains(PkiJson.Deserialize<CertificateDetailsReply>(await Invoke(PkiMethods.GetCertificate, new CertificateDetailsRequest(siteIssuing.Id!))).Certificate!.Subject, p => p.Value == "Site Issuing");

        // A CA that signed a listed CA, and the first-start CA the built-in policies name, stay.
        Assert.Equal("It signed Site Issuing. Delete Site Issuing first.", PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(siteRoot.Id))).Error);
        var first = Assert.Single(cas.Rows, r => r.Name == "OADM Root CA FAKE-SERVER");
        Assert.Equal("Used by the policy HTTPS. Change or delete the policy first.", PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(first.Id))).Error);

        Assert.True(PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(siteIssuing.Id))).Ok);
        var after = PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(siteRoot.Id)));
        Assert.True(after.Ok);
        Assert.DoesNotContain(after.State!.Authorities, a => a.Name.StartsWith("Site ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_certificates_of_the_fake_backend_list_filter_show_and_delete_without_revoke()
    {
        var page = PkiJson.Deserialize<CertificatesReply>(await Invoke(PkiMethods.GetCertificates, new CertificatesRequest { NoAuthorities = true }));
        Assert.True(page.Total > 20);
        Assert.DoesNotContain(page.Rows, r => r.Kind == Oadm.Sdk.Certificates.ListedCertificateKind.Authority);
        Assert.DoesNotContain(page.Rows, r => r.State is Oadm.Sdk.Certificates.IssuedCertificateState.Revoked or Oadm.Sdk.Certificates.IssuedCertificateState.Replaced);
        Assert.Contains(page.Rows, r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.Expired);
        Assert.Contains(page.Rows, r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.ExpiresSoon);
        var dot1x = PkiJson.Deserialize<CertificatesReply>(await Invoke(PkiMethods.GetCertificates, new CertificatesRequest { Purpose = "dot1x" }));
        Assert.All(dot1x.Rows, r => Assert.Equal("dot1x", r.Purpose));

        var valid = page.Rows.First(r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.Valid);
        var details = PkiJson.Deserialize<CertificateDetailsReply>(await Invoke(PkiMethods.GetCertificate, new CertificateDetailsRequest(valid.SerialNumber)));
        Assert.Contains("IP " + valid.Device, details.Certificate!.Names);

        var deleted = PkiJson.Deserialize<DeleteCertificatesReply>(await Invoke(CertificateMethods.Delete, new DeleteCertificatesRequest([valid.SerialNumber])));
        Assert.Equal(1, deleted.Deleted);
        var again = PkiJson.Deserialize<CertificatesReply>(await Invoke(PkiMethods.GetCertificates, new CertificatesRequest { NoAuthorities = true }));
        Assert.Equal(page.Total - 1, again.Total);
        Assert.DoesNotContain(again.Rows, r => r.SerialNumber == valid.SerialNumber);
    }

    [Fact]
    public async Task Page_against_the_fake_backend_never_touches_this_computer()
    {
        var clientTrust = new FakeClientTrust();
        using var vm = new PkiViewModel(new FakeContext(_api), clientTrust, new PkiClientSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));
        await vm.LoadAsync();

        Assert.True(vm.HasCa);

        await vm.Authorities.LoadAsync();
        await vm.InstallTrustCommand.ExecuteAsync(vm.Authorities.Rows.FirstOrDefault(r => r.IsRootCa));
        Assert.Empty(clientTrust.Installed);
    }

    private Task<string?> Invoke(string method, object? payload) =>
        _api.InvokeCorePluginAsync(PkiPluginInfo.PluginId, method, payload is null ? null : PkiJson.Serialize(payload), CancellationToken.None);

    private sealed class FakeContext(FakeOadmApi api) : ICorePluginClientContext
    {
        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.InvokeCorePluginAsync(PkiPluginInfo.PluginId, method, payloadJson, ct);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
    }
}
