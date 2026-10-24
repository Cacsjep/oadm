using Oadm.Plugins.Est;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Radius;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// PKI as one package with the EST and 802.1X servers (docs/spec/pki.md "One plugin"): three core plugins and three rail
/// pages with the names of 4.1, and PKI's own parts never need OADM PRO.
/// </summary>
public sealed class MergedPackageTests
{
    [Fact]
    public void TheClientPartHasThePagesOfPkiAndOfBothServers()
    {
        var pages = typeof(PkiPage).Assembly.GetExportedTypes()
            .Where(t => typeof(ICorePluginPage).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => (ICorePluginPage)Activator.CreateInstance(t)!)
            .OrderBy(p => p.PluginId, StringComparer.Ordinal)
            .Select(p => (p.PluginId, p.Title))
            .ToList();

        Assert.Equal([("oadm.est", "EST server"), ("oadm.pki", "PKI"), ("oadm.radius", "802.1X server")], pages);
    }

    [Fact]
    public void TheServerPartHasThreeCorePluginsWithTheIdsOf41()
    {
        ICorePlugin[] cores = [new EstPlugin(), new PkiPlugin(), new RadiusPlugin()];

        Assert.Equal(["oadm.est", "oadm.pki", "oadm.radius"], cores.Select(c => c.Id));
        Assert.All(cores, c => Assert.Equal(typeof(PkiPlugin).Assembly, c.GetType().Assembly));
        Assert.All(cores, c => Assert.Equal(CorePluginGroup.Security, c.Group));
        Assert.Equal(["oadm.est.setup"], cores[0].TaskPlugins.Select(t => t.Id));
        Assert.Empty(cores[2].TaskPlugins);
    }

    [Fact]
    public async Task PkisOwnPartsNeverNeedOadmPro()
    {
        // OADM itself is never limited (docs/spec/licensing.md): without a license PKI generates its CA and lists it.
        var core = new FakeCoreContext { Pro = new FakeProLicense(), Secrets = TestSecrets.Create() };
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(core, CancellationToken.None);
        await plugin.Service!.Background;

        var reply = PkiJson.Deserialize<PkiReply>(await plugin.InvokeAsync(PkiMethods.Generate, PkiJson.Serialize(new GenerateRequest("OADM CA", null, 10, true)), CancellationToken.None));
        Assert.Null(reply.Error);
        var state = PkiJson.Deserialize<PkiState>(await plugin.InvokeAsync(PkiMethods.GetState, null, CancellationToken.None));
        Assert.Equal("OADM CA", state.Ca?.CommonName);
        Assert.False(core.Pro!.IsActive);
    }
}
