using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>The vault of typed secrets and kept files: encrypted, by id only, unnamed ones gone after a day.</summary>
public sealed class VaultTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-prov-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeSettings _settings = new();
    private readonly FakeUploadedFiles _uploads = new();

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private NodeVault Vault() => new(_settings, new FakeSecrets(), _data, _uploads, _time);

    [Fact]
    public async Task Secrets_are_stored_encrypted_and_read_back_after_a_restart()
    {
        using var vault = Vault();
        var id = await vault.AddSecretAsync("card secret", CancellationToken.None);

        Assert.All(_settings.Values.Values, v => Assert.DoesNotContain("card secret", v, StringComparison.Ordinal));
        using var again = Vault();
        await again.LoadAsync(CancellationToken.None);
        Assert.Equal("card secret", again.ReadSecret(id));
        Assert.Null(again.ReadSecret(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.AddSecretAsync("two\nlines", CancellationToken.None));
        using var noKey = new NodeVault(_settings, null, _data, _uploads, _time);
        Assert.Equal(NodeVault.NoSecrets, (await Assert.ThrowsAsync<InvalidOperationException>(() => noKey.AddSecretAsync("x", CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_kept_file_is_an_upload_until_the_server_deletes_it_then_it_is_added_again()
    {
        using var vault = Vault();
        var file = await vault.AddFileAsync("P3265-LVE_12_6_95.bin", new MemoryStream([1, 2, 3]), CancellationToken.None);

        var first = await vault.UploadIdAsync(file.Id, CancellationToken.None);
        Assert.Equal(first, await vault.UploadIdAsync(file.Id, CancellationToken.None)); // still there: the same upload
        _uploads.Remove(first);
        var second = await vault.UploadIdAsync(file.Id, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(file.Sha256, (await _uploads.FindAsync(second, CancellationToken.None))!.Sha256);
        Assert.Equal(NodeVault.FileGone, (await Assert.ThrowsAsync<InvalidOperationException>(() => vault.UploadIdAsync(Guid.NewGuid(), CancellationToken.None))).Message);
    }

    [Fact]
    public async Task What_no_profile_names_goes_after_a_day()
    {
        using var vault = Vault();
        var named = await vault.AddSecretAsync("named", CancellationToken.None);
        var unnamed = await vault.AddSecretAsync("unnamed", CancellationToken.None);
        var keptFile = await vault.AddFileAsync("a.bin", new MemoryStream([1]), CancellationToken.None);
        var oldFile = await vault.AddFileAsync("b.bin", new MemoryStream([2]), CancellationToken.None);
        var profile = new Profile
        {
            Nodes =
            [
                Harness.Node("users", NodeKind.Users, new UsersSettings([new UserAccount("op1", SecretId: named)])),
                Harness.Node("os", NodeKind.UpgradeFiles, new UpgradeFilesSettings([new OsFile(keptFile.Id)])),
            ],
        };

        await vault.PruneAsync([profile], CancellationToken.None);
        Assert.Equal(2, vault.SecretIds.Count); // a profile being edited may name them soon

        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await vault.PruneAsync([profile], CancellationToken.None);

        Assert.Equal([named], vault.SecretIds);
        Assert.Null(vault.ReadSecret(unnamed));
        Assert.Equal([keptFile.Id], vault.Files.Keys);
        Assert.False(File.Exists(Path.Combine(_data, "files", oldFile.Id.ToString("N") + ".bin")));
    }
}
