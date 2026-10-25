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
        Assert.Equal(PkiService.CaUnreadableStatus, (await pki.StateAsync()).Status);
        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true));
        Assert.False(reply.Ok);
        Assert.Equal(PkiService.CaUnreadableText, reply.Error);
        Assert.Equal(damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_confirmed_new_ca_keeps_the_damaged_value_in_a_file_then_replaces_it()
    {
        await using var pki = await StartDamagedAsync(_data);
        Assert.True((await pki.StateAsync()).CaUnreadable);

        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true, ReplaceUnreadable: true));

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal("OADM Root CA NEW", pki.Service.Ca!.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false));
        Assert.NotEqual(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
        var kept = Assert.Single(Directory.GetFiles(_data));
        Assert.Matches(@"^ca-unreadable-\d{8}-\d{6}-\d{3}\.json$", Path.GetFileName(kept));
        Assert.Equal(Damaged, await File.ReadAllTextAsync(kept));
        var state = await pki.StateAsync();
        Assert.False(state.CaUnreadable);
        Assert.NotEqual(PkiService.CaUnreadableStatus, state.Status);
    }

    [Fact]
    public async Task Without_a_data_folder_the_confirmed_replace_changes_nothing()
    {
        await using var pki = await StartDamagedAsync(dataDirectory: null);

        var reply = await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true, ReplaceUnreadable: true));

        Assert.False(reply.Ok);
        Assert.Equal(PkiService.CaUnreadableNotSavedText, reply.Error);
        Assert.Null(pki.Service.Ca);
        Assert.Equal(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));
    }

    [Fact]
    public async Task New_oadm_ca_on_the_page_asks_first_and_replaces_only_after_replace()
    {
        await using var pki = await StartDamagedAsync(_data);
        var dialogs = new FakeDialogs();
        using var vm = new Oadm.Plugins.Pki.Client.PkiViewModel(new PluginPageContext(pki.Plugin, pki.Hub), new FakeClientTrust(), new Oadm.Plugins.Pki.Client.PkiClientSettingsStore(_settingsFile)) { Dialogs = dialogs };
        await vm.LoadAsync();
        dialogs.ConfirmAnswer = false;
        dialogs.Drive = d => ((Oadm.Plugins.Pki.Client.GenerateCaViewModel)d).GenerateCommand.ExecuteAsync(null);

        await vm.GenerateCommand.ExecuteAsync(null); // the user cancels the question
        var (title, message) = Assert.Single(dialogs.Confirmations);
        Assert.Equal(Oadm.Plugins.Pki.Client.PkiViewModel.ReplaceTitle, title);
        Assert.Equal("The stored OADM CA cannot be read. A new CA replaces it; cameras with certificates of the old CA need new ones.", message);
        Assert.Null(pki.Service.Ca);
        Assert.Equal(Damaged, await pki.Settings.GetAsync(PkiStore.CaKey, CancellationToken.None));

        dialogs.ConfirmAnswer = true;
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(dialogs.Last!.Completed);
        Assert.NotNull(pki.Service.Ca);
        Assert.True(vm.HasCa);
        Assert.Single(Directory.GetFiles(_data));
    }

    private static async Task<PkiHarness> StartDamagedAsync(string? dataDirectory)
    {
        var settings = new Oadm.Core.Plugins.InMemoryPluginSettingsProvider();
        await settings.GetSettings(PkiPluginInfo.PluginId).SetAsync(PkiStore.CaKey, Damaged, CancellationToken.None);
        return await PkiHarness.StartAsync(settings, dataDirectory: dataDirectory);
    }
}
