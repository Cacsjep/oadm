using Oadm.Plugins.Est;
using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The shared "802.1X and EST server" certificate at the start (docs/spec/pki.md "Server certificates"): made only once both
/// server plugins told their names, never while a server is turned on (its own start makes it with its names).
/// </summary>
public sealed class ServerCertificateStartTests
{
    [Fact]
    public async Task The_start_makes_the_shared_certificate_only_after_both_server_plugins_with_their_names()
    {
        var pro = FakeProLicense.ActivePro();
        var core = new FakeCoreContext { Pro = pro, Secrets = TestSecrets.Create() };
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(core, CancellationToken.None);
        await plugin.Service!.Background;
        var service = plugin.Service;
        var store = service.Policies;
        Assert.False(PkiPlugin.MakesServerCertificates(service, pro)); // the server plugins have not told their names yet

        // The EST server plugin (its server off) hands the site nodes' addresses the cameras were given.
        var est = new FakeCoreContext();
        est.FakeServices.Offer(store);
        await est.Settings.SetAsync(EstStore.SitesKey, EstJson.Serialize(new Dictionary<Guid, List<string>> { [Guid.NewGuid()] = ["10.9.8.7"] }), CancellationToken.None);
        await EstPlugin.FollowPoliciesAsync(est, CancellationToken.None);
        Assert.Equal(["10.9.8.7"], store.EstServerNames!());
        store.ServerPluginStarted(PolicyKinds.EstServer, () => false);
        Assert.False(PkiPlugin.MakesServerCertificates(service, pro));

        // The 802.1X server is turned on: its start makes the certificate with its names (and takes over the one it kept).
        var radiusOn = true;
        store.ServerPluginStarted(PolicyKinds.Dot1xServer, () => radiusOn);
        Assert.False(PkiPlugin.MakesServerCertificates(service, pro));
        Assert.Null(store.EstServerPolicy!.CertificateId);

        // Both off: made now, with the EST server's names.
        radiusOn = false;
        store.ServerPluginStarted(PolicyKinds.Dot1xServer, () => radiusOn);
        Assert.True(PkiPlugin.MakesServerCertificates(service, pro));
        await Eventually.TrueAsync(() => store.EstServerPolicy!.CertificateId is not null, "the shared certificate");
        var material = service.ServerCertificateOf(PolicyKinds.EstServer).Material!;
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(material.Pkcs8);
        Assert.Contains("10.9.8.7", material.Names);

        Assert.False(PkiPlugin.MakesServerCertificates(service, new FakeProLicense())); // never without OADM PRO
    }
}
