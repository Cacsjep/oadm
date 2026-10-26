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

        var reply = PkiJson.Deserialize<PkiReply>(await plugin.InvokeAsync(PkiMethods.CreateAuthority, PkiJson.Serialize(new CreateCaRequest("Contoso CA", false, null, CaKeys.Ecc, 10)), CancellationToken.None));
        Assert.Null(reply.Error);
        var state = PkiJson.Deserialize<PkiState>(await plugin.InvokeAsync(PkiMethods.GetState, null, CancellationToken.None));
        Assert.StartsWith("OADM Root CA", state.Ca?.CommonName, StringComparison.Ordinal); // the first-start CA
        Assert.Contains(plugin.Service.Authorities(), a => a.Name == "Contoso CA" && a.Issues); // and the created one
        Assert.False(core.Pro!.IsActive);
    }

    [Fact]
    public async Task Starting_the_PRO_trial_tells_the_open_pages_to_read_the_policies_again()
    {
        // The 802.1X and EST policies show only with OADM PRO: the Policies tab must show them at once, not after a page change.
        var pro = new FakeProLicense();
        var core = new FakeCoreContext { Pro = pro, Secrets = TestSecrets.Create() };
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(core, CancellationToken.None);
        await plugin.Service!.Background;
        while (core.PublishedEvents.TryDequeue(out _))
        {
        }

        pro.Current = FakeProLicense.ActivePro(trial: true).Current;

        Assert.Contains(core.PublishedEvents, e => e.Topic == Oadm.Plugins.Pki.Policies.PolicyMethods.Topic);
    }
}
