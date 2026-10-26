using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// A stored OADM CA that cannot be read is kept: no default CA over it, New OADM CA refused without the user's confirmation
/// (fail closed), the page says so. Confirmed, the stored value goes to a file in the plugin's data folder, then a new CA
/// replaces it.
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
    public async Task A_confirmed_new_ca_keeps_the_damaged_value_in_a_file_then_replaces_it()
    {
        await using var pki = await StartDamagedAsync(_data);
        Assert.True((await pki.StateAsync()).CaUnreadable);

        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(PkiService.UnreadableRowPrefix + Oadm.Sdk.Certificates.CertificateAuthorityInfo.OadmId));

        Assert.True(reply.Ok, reply.Error);
        Assert.NotEqual(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
        var kept = Assert.Single(Directory.GetFiles(_data));
        Assert.Matches(@"^ca-unreadable-\d{8}-\d{6}-\d{3}\.json$", Path.GetFileName(kept));
        Assert.Equal(Damaged, await File.ReadAllTextAsync(kept));
        var state = await pki.StateAsync();
        Assert.False(state.CaUnreadable);
    }

    [Fact]
    public async Task Without_a_data_folder_the_confirmed_replace_changes_nothing()
    {
        await using var pki = await StartDamagedAsync(dataDirectory: null);

        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(PkiService.UnreadableRowPrefix + Oadm.Sdk.Certificates.CertificateAuthorityInfo.OadmId));

        Assert.False(reply.Ok);
        Assert.Equal(PkiService.CaUnreadableNotSavedText, reply.Error);
        Assert.Null(pki.Service.Ca);
        Assert.Equal(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
    }

    private static async Task<PkiHarness> StartDamagedAsync(string? dataDirectory)
    {
        var settings = new Oadm.Core.Plugins.InMemoryPluginSettingsProvider();
        await settings.GetSettings(PkiPluginInfo.PluginId).SetAsync(PkiStore.CaKey, Damaged, CancellationToken.None);
        return await PkiHarness.StartAsync(settings, dataDirectory: dataDirectory);
    }
}
