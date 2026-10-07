using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Settings;

public sealed class ServerSettingsStoreTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private ServerSettingsStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = _db.Get<ServerSettingsStore>();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task DefaultsApplyOnEmptyDatabase()
    {
        var settings = await _store.GetServerSettingsAsync(CancellationToken.None);

        Assert.Equal(60, settings.PollingIntervalSeconds);
        Assert.Equal(32, settings.ScanParallelism);
        Assert.Equal(1500, settings.ScanTimeoutMs);
        Assert.Equal(30, settings.ZeroConfSeconds);
        Assert.Equal("http://0.0.0.0:5080", settings.ListenUrl);
        Assert.Equal(ServerSettings.DefaultServerName(), settings.ServerName);
        Assert.False(string.IsNullOrWhiteSpace(settings.ServerName));

        Assert.Equal(60, await _store.GetAsync<int>(SettingKeys.PollingIntervalSeconds, CancellationToken.None));
        Assert.Equal("\"http://0.0.0.0:5080\"", await _store.GetJsonAsync(SettingKeys.ListenUrl, CancellationToken.None));
        Assert.Null(await _store.GetJsonAsync("Unknown.Key", CancellationToken.None));
    }

    [Fact]
    public async Task TypedSetOverridesAndResetRestoresDefault()
    {
        var changes = new List<string>();
        _store.Changed += (_, e) => changes.Add(e.Key);

        await _store.SetAsync(SettingKeys.PollingIntervalSeconds, 15, CancellationToken.None);
        Assert.Equal(15, (await _store.GetServerSettingsAsync(CancellationToken.None)).PollingIntervalSeconds);

        await _store.ResetAsync(SettingKeys.PollingIntervalSeconds, CancellationToken.None);
        Assert.Equal(60, await _store.GetAsync<int>(SettingKeys.PollingIntervalSeconds, CancellationToken.None));

        Assert.Equal([SettingKeys.PollingIntervalSeconds, SettingKeys.PollingIntervalSeconds], changes);
    }

    [Fact]
    public async Task SetServerSettingsWritesAll()
    {
        var wanted = new ServerSettings(30, 8, 3000, "oadm-lab", "http://127.0.0.1:6000", 20, UseHostName: true, ZeroConfSeconds: 120);

        await _store.SetServerSettingsAsync(wanted, CancellationToken.None);

        Assert.Equal(wanted, await _store.GetServerSettingsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(SettingKeys.PollingIntervalSeconds, "0")]
    [InlineData(SettingKeys.ScanParallelism, "\"many\"")]
    [InlineData(SettingKeys.ScanTimeoutMs, "1.5")]
    [InlineData(SettingKeys.ServerName, "\"\"")]
    [InlineData(SettingKeys.ListenUrl, "\"not a url\"")]
    [InlineData(SettingKeys.DevicesUseHostName, "1")]
    [InlineData(SettingKeys.DiscoveryZeroConfSeconds, "4")]
    [InlineData(SettingKeys.DiscoveryZeroConfSeconds, "301")]
    [InlineData("Any.Key", "{not json")]
    public async Task InvalidValuesAreRejected(string key, string json)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetJsonAsync(key, json, CancellationToken.None));
    }

    [Fact]
    public async Task UseHostNameDefaultsToFalse()
    {
        Assert.False((await _store.GetServerSettingsAsync(CancellationToken.None)).UseHostName);
        Assert.False(await _store.GetAsync<bool>(SettingKeys.DevicesUseHostName, CancellationToken.None));

        await _store.SetAsync(SettingKeys.DevicesUseHostName, true, CancellationToken.None);

        Assert.True((await _store.GetServerSettingsAsync(CancellationToken.None)).UseHostName);
    }

    [Fact]
    public async Task InvalidServerSettingsWriteNothing()
    {
        var bad = new ServerSettings(30, 0, 3000, "x", "http://127.0.0.1:6000");

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetServerSettingsAsync(bad, CancellationToken.None));
        Assert.Equal(60, (await _store.GetServerSettingsAsync(CancellationToken.None)).PollingIntervalSeconds);
    }

    [Fact]
    public async Task PluginSettingsAreNamespacedPerPlugin()
    {
        var factory = _db.Get<IPluginSettingsProvider>();
        IPluginSettings ntp = factory.GetSettings("oadm.ntp");
        IPluginSettings dhcp = factory.GetSettings("oadm.dhcp");

        await ntp.SetAsync("Server", "\"pool.ntp.org\"", CancellationToken.None);

        Assert.Equal("\"pool.ntp.org\"", await ntp.GetAsync("Server", CancellationToken.None));
        Assert.Null(await dhcp.GetAsync("Server", CancellationToken.None));
        Assert.Null(await _store.GetJsonAsync("Server", CancellationToken.None));
        Assert.Equal("\"pool.ntp.org\"", await _store.GetJsonAsync("Plugin:oadm.ntp:Server", CancellationToken.None));

        await ntp.SetAsync("Server", null, CancellationToken.None);
        Assert.Null(await ntp.GetAsync("Server", CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => ntp.SetAsync("Bad", "{", CancellationToken.None));
        Assert.Throws<ArgumentException>(() => factory.GetSettings("evil:id"));
    }

    [Fact]
    public async Task PluginKeysDoNotLeakIntoServerSettings()
    {
        await _db.Get<IPluginSettingsProvider>().GetSettings("x").SetAsync(SettingKeys.PollingIntervalSeconds, "1", CancellationToken.None);

        Assert.Equal(60, (await _store.GetServerSettingsAsync(CancellationToken.None)).PollingIntervalSeconds);
    }
}
