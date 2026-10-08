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
        Assert.Contains(await _api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == PkiPluginInfo.PluginId && p.IconKey == "key");

        var state = PkiJson.Deserialize<PkiState>(await _api.InvokeCorePluginAsync(PkiPluginInfo.PluginId, PkiMethods.GetState, null, CancellationToken.None));
        Assert.True(state.Simulated);
        Assert.Equal("OADM Root CA FAKE-SERVER", state.Ca!.CommonName);
        Assert.Equal("ok", state.Status.Kind);
        Assert.Single(state.PreviousCas);
        Assert.Equal(12, state.IssuedDevices);
        Assert.Equal(365, state.Config.DeviceCertValidityDays);
        Assert.Equal(Dot1xIdentity.Mac, state.Config.Dot1x.Identity);

        var ask = PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.Generate, new GenerateRequest("New fake CA", null, 5, false)));
        Assert.True(ask.NeedsConfirmation);
        Assert.Equal(12, ask.DevicesWithCurrentCa);
        var done = PkiJson.Deserialize<PkiReply>(await Invoke(PkiMethods.Generate, new GenerateRequest("New fake CA", null, 5, true)));
        Assert.True(done.Ok);
        Assert.Equal("New fake CA", done.State!.Ca!.CommonName);
        Assert.Equal("OADM Root CA FAKE-SERVER", Assert.Single(done.State.PreviousCas).CommonName);

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

        using var radius = TestCa.Root("Corp RADIUS CA");
        var imported = PkiJson.Deserialize<RadiusCaReply>(await Invoke(PkiMethods.ImportRadiusCa, new ImportRadiusCaRequest(Convert.ToBase64String(radius.RawData))));
        Assert.Equal("Corp RADIUS CA", imported.Summary!.CommonName);
        Assert.True(PkiJson.Deserialize<InstallReply>(await Invoke(PkiMethods.InstallServerTrust, null)).Installed);
    }

    [Fact]
    public async Task Page_against_the_fake_backend_never_touches_this_computer()
    {
        var clientTrust = new FakeClientTrust();
        using var vm = new PkiViewModel(new FakeContext(_api), clientTrust, new PkiClientSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));
        await vm.LoadAsync();

        Assert.True(vm.HasCa);
        Assert.Equal("OADM Root CA FAKE-SERVER", vm.CaName);
        Assert.Single(vm.PreviousCas);
        Assert.Contains("Fake mode", vm.ClientTrustTip, StringComparison.Ordinal);

        await vm.InstallTrustCommand.ExecuteAsync(null);
        Assert.Equal("Installed on the server", vm.ServerTrustText);
        Assert.Equal("Not installed on this computer", vm.ClientTrustText);
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
