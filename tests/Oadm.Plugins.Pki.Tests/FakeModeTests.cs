using System.Security.Cryptography.X509Certificates;

using Oadm.Client.Api;
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
        Assert.Single(state.PreviousCas);
        Assert.Equal(12, state.IssuedDevices);
        Assert.Equal(365, state.Config.DeviceCertValidityDays);
        Assert.Equal(Dot1xIdentity.Mac, state.Config.Dot1x.Identity);

        var der = PkiJson.Deserialize<FileReply>(await Invoke(PkiMethods.ExportPublic, new ExportRequest("der")));
        using (var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(der.DataBase64!)))
        {
            Assert.Equal("CN=New fake CA", cert.Subject);
        }

        var backup = PkiJson.Deserialize<FileReply>(await Invoke(PkiMethods.Backup, new BackupRequest("long enough")));
        using (var pfx = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(backup.DataBase64!), "long enough"))
        {
            Assert.True(pfx.HasPrivateKey);
        }

        Assert.True(PkiJson.Deserialize<InstallReply>(await Invoke(PkiMethods.InstallServerTrust, null)).Installed);
    }

    [Fact]
    public async Task The_certificates_tab_of_the_fake_backend_lists_filters_shows_and_revokes()
    {
        var page = PkiJson.Deserialize<CertificatesReply>(await Invoke(PkiMethods.GetCertificates, new CertificatesRequest()));
        Assert.True(page.Total > 20);
        Assert.Contains(page.Rows, r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.Revoked && r.Revocation is not null);
        Assert.DoesNotContain(page.Rows, r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.Replaced); // only current certificates
        Assert.Contains(page.Rows, r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.Expired);
        Assert.Contains(page.Rows, r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.ExpiresSoon);
        var dot1x = PkiJson.Deserialize<CertificatesReply>(await Invoke(PkiMethods.GetCertificates, new CertificatesRequest { Purpose = "dot1x" }));
        Assert.All(dot1x.Rows, r => Assert.Equal("dot1x", r.Purpose));

        var valid = page.Rows.First(r => r.State == Oadm.Sdk.Certificates.IssuedCertificateState.Valid);
        var details = PkiJson.Deserialize<CertificateDetailsReply>(await Invoke(PkiMethods.GetCertificate, new CertificateDetailsRequest(valid.SerialNumber)));
        Assert.Contains("IP " + valid.Device, details.Certificate!.Names);
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
