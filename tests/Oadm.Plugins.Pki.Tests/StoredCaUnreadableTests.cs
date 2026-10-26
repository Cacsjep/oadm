using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// A stored first-start CA that cannot be read is kept: no default CA over it, the page says so (a row with Delete). Decision
/// 2026-10-26 (item 13): it is deleted like any CA, refused while a policy names it; once the policies name another CA, the
/// stored value goes to a file in the plugin's data folder and the CA leaves the list.
/// </summary>
public sealed class StoredCaUnreadableTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-pki-unreadable-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), "oadm-pki-unreadable-client-" + Guid.NewGuid().ToString("N") + ".json");

    private const string Damaged = "\"damaged\""; // valid JSON (the settings store takes nothing else), not a CA

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }

        File.Delete(_settingsFile);
    }

    [Fact]
    public async Task A_damaged_stored_ca_is_never_overwritten()
    {
        var settings = new Oadm.Core.Plugins.InMemoryPluginSettingsProvider();
        const string damaged = Damaged;
        await settings.GetSettings(PkiPluginInfo.PluginId).SetAsync(PkiStore.CaKey, damaged, CancellationToken.None);

        await using var pki = await PkiHarness.StartAsync(settings);

        Assert.Null(pki.Service.Ca); // no new default CA over it
        Assert.Equal(damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_is_refused_while_a_policy_names_it_then_keeps_the_damaged_value_in_a_file()
    {
        await using var pki = await StartDamagedAsync(_data);
        Assert.True((await pki.StateAsync()).CaUnreadable);

        var refused = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(UnreadableRow));
        Assert.Equal(Policies.PolicyTexts.UsedByPolicy(Policies.PolicyLimits.HttpsName), refused.Error);
        Assert.Equal(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
        Assert.False(Directory.Exists(_data) && Directory.GetFiles(_data).Length > 0);

        await PointPoliciesElsewhereAsync(pki);
        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(UnreadableRow));

        Assert.True(reply.Ok, reply.Error);
        Assert.NotEqual(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
        var kept = Assert.Single(Directory.GetFiles(_data));
        Assert.Matches(@"^ca-unreadable-\d{8}-\d{6}-\d{3}\.json$", Path.GetFileName(kept));
        Assert.Equal(Damaged, await File.ReadAllTextAsync(kept));
        var state = await pki.StateAsync();
        Assert.False(state.CaUnreadable);
    }

    [Fact]
    public async Task Without_a_data_folder_delete_changes_nothing()
    {
        await using var pki = await StartDamagedAsync(dataDirectory: null);
        await PointPoliciesElsewhereAsync(pki);

        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(UnreadableRow));

        Assert.False(reply.Ok);
        Assert.Equal(PkiService.CaUnreadableNotSavedText, reply.Error);
        Assert.Null(pki.Service.Ca);
        Assert.Equal(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
    }

    private const string UnreadableRow = PkiService.UnreadableRowPrefix + Oadm.Sdk.Certificates.CertificateAuthorityInfo.OadmId;

    /// <summary>A created root CA issues for the built-in HTTPS and IEEE 802.1X policies (and checks the RADIUS server) instead of the damaged one.</summary>
    private static async Task PointPoliciesElsewhereAsync(PkiHarness pki)
    {
        var created = await pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority, new CreateCaRequest("Site Root", false, null, CaKeys.Ecc, 10));
        Assert.True(created.Ok, created.Error);
        var id = pki.Service.Authorities().Single(a => a.Name == "Site Root").Id;
        foreach (var kind in new[] { Policies.PolicyKinds.HttpsCertificate, Policies.PolicyKinds.Dot1xCertificate })
        {
            var policy = pki.Policies.FirstOf(kind)!;
            var saved = await pki.InvokeAsync<Policies.PolicySaveReply>(Policies.PolicyMethods.SavePolicy,
                policy with { CaId = id, RadiusCaId = kind == Policies.PolicyKinds.Dot1xCertificate ? id : policy.RadiusCaId });
            Assert.Null(saved.Error);
        }
    }

    private static async Task<PkiHarness> StartDamagedAsync(string? dataDirectory)
    {
        var settings = new Oadm.Core.Plugins.InMemoryPluginSettingsProvider();
        await settings.GetSettings(PkiPluginInfo.PluginId).SetAsync(PkiStore.CaKey, Damaged, CancellationToken.None);
        return await PkiHarness.StartAsync(settings, dataDirectory: dataDirectory);
    }
}
