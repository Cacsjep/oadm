using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>A stored OADM CA that cannot be read is kept: no default CA over it, New OADM CA refused, the page says so.</summary>
public sealed class StoredCaUnreadableTests
{
    [Fact]
    public async Task A_damaged_stored_ca_is_never_overwritten()
    {
        var settings = new Oadm.Core.Plugins.InMemoryPluginSettingsProvider();
        const string damaged = "\"damaged\""; // valid JSON (the settings store takes nothing else), not a CA
        await settings.GetSettings(PkiPluginInfo.PluginId).SetAsync(PkiStore.CaKey, damaged, CancellationToken.None);

        await using var pki = await PkiHarness.StartAsync(settings);

        Assert.Null(pki.Service.Ca); // no new default CA over it
        Assert.Equal(damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
        Assert.Equal(PkiService.CaUnreadableStatus, (await pki.StateAsync()).Status);
        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true));
        Assert.False(reply.Ok);
        Assert.Equal(PkiService.CaUnreadableText, reply.Error);
        Assert.Equal(damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
    }
}
