using System.Text.Json;

using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>Production hardening 3: the key replacement reaches the clients through ServerSettings.key_replaced.</summary>
public sealed class KeyReplacedNoticeTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SettingsCarryTheNoticeOnlyAfterAReplacement()
    {
        await using var host = await TestServerHost.StartAsync();

        var fresh = await host.Settings.GetAsync(new Proto.Empty());
        Assert.Null(fresh.KeyReplaced);

        var notice = new KeyReplacedNotice("abc", new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), 4, 1);
        await host.Get<ServerSettingsStore>().SetJsonAsync(MasterKeyCheck.KeyReplacedSetting, JsonSerializer.Serialize(notice, WebJson), CancellationToken.None);

        var settings = await host.Settings.GetAsync(new Proto.Empty());
        Assert.Equal("abc", settings.KeyReplaced.Id);
        Assert.Equal(4, settings.KeyReplaced.Devices);
        Assert.Equal(1, settings.KeyReplaced.CredentialListEntries);
        Assert.Equal(notice.Message, settings.KeyReplaced.Message);
        Assert.Equal(notice.ReplacedUtc, settings.KeyReplaced.Replaced.ToDateTime());

        // A fresh data folder has a key check value and no notice.
        Assert.NotNull(await host.Get<ServerSettingsStore>().GetJsonAsync(MasterKeyCheck.KeyCheckSetting, CancellationToken.None));
    }
}
