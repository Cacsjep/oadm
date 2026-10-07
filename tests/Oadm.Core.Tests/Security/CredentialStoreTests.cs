using System.Text;
using Microsoft.EntityFrameworkCore;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Tests.Persistence;

namespace Oadm.Core.Tests.Security;

public sealed class CredentialStoreTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private CredentialStore _store = null!;
    private Device _device = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = _db.Get<CredentialStore>();
        _device = await _db.Get<DeviceRepository>().AddAsync(
            new Device { Serial = "ACCC8E0A0B0C", Address = "10.0.0.5" }, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task SetGetReplaceRemove()
    {
        Assert.Null(await _store.GetAsync(_device.Id, CancellationToken.None));
        Assert.False(await _store.HasCredentialsAsync(_device.Id, CancellationToken.None));

        await _store.SetAsync(_device.Id, "root", "first", CancellationToken.None);
        await _store.SetAsync(_device.Id, "admin", "second", CancellationToken.None);

        var creds = await _store.GetAsync(_device.Id, CancellationToken.None);
        Assert.Equal("admin", creds!.UserName);
        Assert.Equal("second", creds.Password);
        Assert.True(await _store.HasCredentialsAsync(_device.Id, CancellationToken.None));
        Assert.Contains(_device.Id, await _store.ListDeviceIdsWithCredentialsAsync(CancellationToken.None));

        Assert.True(await _store.RemoveAsync(_device.Id, CancellationToken.None));
        Assert.False(await _store.RemoveAsync(_device.Id, CancellationToken.None));
        Assert.Null(await _store.GetAsync(_device.Id, CancellationToken.None));
    }

    [Fact]
    public async Task PasswordIsNotStoredInPlaintext()
    {
        const string password = "VerySecretPassword123";
        await _store.SetAsync(_device.Id, "root", password, CancellationToken.None);

        await using var ctx = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        var row = await ctx.DeviceCredentials.SingleAsync();
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(row.EncryptedPassword), StringComparison.Ordinal);
        Assert.Equal(CredentialProtector.NonceSize + password.Length + CredentialProtector.TagSize, row.EncryptedPassword.Length);
    }

    [Fact]
    public async Task CiphertextCopiedToAnotherDeviceDoesNotDecrypt()
    {
        var other = await _db.Get<DeviceRepository>().AddAsync(
            new Device { Serial = "ACCC8E0A0B0D", Address = "10.0.0.6" }, CancellationToken.None);
        await _store.SetAsync(_device.Id, "root", "secret", CancellationToken.None);
        await _store.SetAsync(other.Id, "root", "other", CancellationToken.None);

        await using (var ctx = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var source = await ctx.DeviceCredentials.SingleAsync(c => c.DeviceId == _device.Id);
            var target = await ctx.DeviceCredentials.SingleAsync(c => c.DeviceId == other.Id);
            target.EncryptedPassword = source.EncryptedPassword;
            await ctx.SaveChangesAsync();
        }

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => _store.GetAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownDeviceIsRejected()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _store.SetAsync(Guid.NewGuid(), "root", "pass", CancellationToken.None));
    }

    [Fact]
    public async Task SettingCredentialsPublishesDeviceUpdate()
    {
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();

        await _store.SetAsync(_device.Id, "root", "pass", CancellationToken.None);

        Assert.True(subscription.Reader.TryRead(out var change));
        Assert.Equal(DeviceChangeKind.Updated, change.Kind);
        Assert.Equal(_device.Id, change.DeviceId);
    }

    [Fact]
    public void ToStringRedactsPassword()
    {
        var creds = new DeviceCredentials("root", "hunter2");
        Assert.DoesNotContain("hunter2", creds.ToString(), StringComparison.Ordinal);
        Assert.Contains("root", creds.ToString(), StringComparison.Ordinal);
    }
}
