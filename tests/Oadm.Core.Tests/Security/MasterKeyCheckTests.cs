using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Security;

/// <summary>Production hardening 3: a missing or different master.key is replaced, unreadable credentials removed.</summary>
public sealed class MasterKeyCheckTests : IDisposable
{
    private readonly string _dir = TestDatabase.NewTempDirectory();

    public void Dispose() => TestDatabase.DeleteDirectory(_dir);

    [Fact]
    public async Task ARestartWithTheSameKeyKeepsEverything()
    {
        var (devices, _) = await SeedAsync();

        var db = await TestDatabase.CreateAsync(_dir);

        Assert.Null(db.Get<DatabaseInitializer>().KeyReplaced);
        Assert.Equal(("root", "pw-0"), await PasswordAsync(db, devices[0]));
        Assert.Single(await db.Get<CredentialListStore>().GetAllAsync(CancellationToken.None));
        Assert.NotNull(await db.Get<ServerSettingsStore>().GetJsonAsync(MasterKeyCheck.KeyCheckSetting, CancellationToken.None));
        Assert.Null(await MasterKeyCheck.ReadNoticeAsync(db.Get<ServerSettingsStore>(), CancellationToken.None));
        await db.CloseAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different")]
    [InlineData("wrong-size")]
    public async Task AMissingOrDifferentKeyIsReplacedAndTheUnreadableRowsRemoved(string damage)
    {
        var (devices, untouched) = await SeedAsync();
        var keyPath = new OadmPaths(_dir).MasterKeyPath;
        switch (damage)
        {
            case "missing":
                File.Delete(keyPath);
                break;
            case "different":
                File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(CredentialProtector.KeySize));
                break;
            default:
                File.WriteAllBytes(keyPath, new byte[7]);
                break;
        }

        var db = await TestDatabase.CreateAsync(_dir);

        var notice = db.Get<DatabaseInitializer>().KeyReplaced;
        Assert.NotNull(notice);
        Assert.Equal(3, notice.Devices);
        Assert.Equal(1, notice.CredentialListEntries);
        Assert.Equal("The server's key was replaced: 3 devices need their credentials again, 1 credential list entry was removed.", notice.Message);
        Assert.Equal(notice, await MasterKeyCheck.ReadNoticeAsync(db.Get<ServerSettingsStore>(), CancellationToken.None));

        await using (var ctx = await db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            Assert.Empty(ctx.DeviceCredentials);
            Assert.Empty(ctx.CredentialListEntries);
            var statuses = await ctx.Devices.ToDictionaryAsync(d => d.Id, d => d.Status);
            Assert.All(devices, id => Assert.Equal(DeviceStatus.CredentialsRequired, statuses[id]));
            Assert.Equal(DeviceStatus.Ok, statuses[untouched]); // had no credentials
        }

        // The new key works and is checked from now on; the old file is kept as a backup when there was one.
        Assert.Equal(CredentialProtector.KeySize, new FileInfo(keyPath).Length);
        Assert.Equal(damage == "missing" ? 0 : 1, Directory.GetFiles(_dir, "master.key.replaced-*").Length);
        await db.Get<CredentialStore>().SetAsync(devices[0], "root", "new", CancellationToken.None);
        await db.CloseAsync();
        var again = await TestDatabase.CreateAsync(_dir);
        Assert.Null(again.Get<DatabaseInitializer>().KeyReplaced);
        Assert.Equal(("root", "new"), await PasswordAsync(again, devices[0]));
        await again.CloseAsync();
    }

    [Fact]
    public async Task DataWithoutAStoredCheckIsCheckedByDecrypting()
    {
        var (devices, _) = await SeedAsync();
        await RemoveCheckAsync();

        // Same key: readable rows, the check value is stored, nothing removed.
        var same = await TestDatabase.CreateAsync(_dir);
        Assert.Null(same.Get<DatabaseInitializer>().KeyReplaced);
        Assert.Equal(("root", "pw-1"), await PasswordAsync(same, devices[1]));
        Assert.NotNull(await same.Get<ServerSettingsStore>().GetJsonAsync(MasterKeyCheck.KeyCheckSetting, CancellationToken.None));
        await same.CloseAsync();

        // Another key and no check value: nothing decrypts, so the key is replaced.
        await RemoveCheckAsync();
        File.WriteAllBytes(new OadmPaths(_dir).MasterKeyPath, RandomNumberGenerator.GetBytes(CredentialProtector.KeySize));
        var other = await TestDatabase.CreateAsync(_dir);
        Assert.Equal(3, other.Get<DatabaseInitializer>().KeyReplaced?.Devices);
        await other.CloseAsync();
    }

    [Fact]
    public void TheNoticeTextUsesSingularAndPlural()
    {
        Assert.Equal("The server's key was replaced: 1 device needs its credentials again.", new KeyReplacedNotice("a", DateTime.UtcNow, 1, 0).Message);
        Assert.Equal("The server's key was replaced: 0 devices need their credentials again, 2 credential list entries were removed.", new KeyReplacedNotice("a", DateTime.UtcNow, 0, 2).Message);
    }

    /// <summary>Three devices with credentials, one without, one credential list entry; then closes the database.</summary>
    private async Task<(List<Guid> Devices, Guid WithoutCredentials)> SeedAsync()
    {
        var db = await TestDatabase.CreateAsync(_dir); // closed, not disposed: the folder stays
        var repository = db.Get<DeviceRepository>();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var device = await repository.AddAsync(new Device { Serial = "ACCC8E0A0B0" + i, Address = "10.0.0." + (10 + i), Status = DeviceStatus.Ok }, CancellationToken.None);
            await db.Get<CredentialStore>().SetAsync(device.Id, "root", "pw-" + i, CancellationToken.None);
            ids.Add(device.Id);
        }

        var plain = await repository.AddAsync(new Device { Serial = "ACCC8E0A0B09", Address = "10.0.0.19", Status = DeviceStatus.Ok }, CancellationToken.None);
        await db.Get<CredentialListStore>().AddAsync("admin", "list-pw", CancellationToken.None);
        await db.CloseAsync();
        return (ids, plain.Id);
    }

    private async Task RemoveCheckAsync()
    {
        var db = await TestDatabase.CreateAsync(_dir);
        await db.Get<ServerSettingsStore>().SetJsonAsync(MasterKeyCheck.KeyCheckSetting, null, CancellationToken.None);
        await db.CloseAsync();
    }

    private static async Task<(string, string)> PasswordAsync(TestDatabase db, Guid deviceId)
    {
        var credentials = await db.Get<CredentialStore>().GetAsync(deviceId, CancellationToken.None);
        return (credentials!.UserName, credentials.Password);
    }
}
