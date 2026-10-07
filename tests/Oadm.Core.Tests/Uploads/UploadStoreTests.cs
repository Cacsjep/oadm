using System.Security.Cryptography;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Persistence;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Uploads;

namespace Oadm.Core.Tests.Uploads;

public sealed class UploadStoreTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow); // file times are real
    private TestDatabase _db = null!;
    private UploadStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = new UploadStore(_db.Paths, _db.Get<ServerSettingsStore>(), _time);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task UploadInChunksComputesSha256AndCanBeReadBack()
    {
        var content = RandomNumberGenerator.GetBytes(600_000);

        var file = await UploadAsync("firmware.bin", content, chunk: 256 * 1024);

        Assert.True(UploadStore.IsValidId(file.Id));
        Assert.Equal("firmware.bin", file.Name);
        Assert.Equal(content.Length, file.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), file.Sha256);
        Assert.Equal(file, await _store.FindAsync(file.Id, CancellationToken.None));
        await using var read = await _store.OpenReadAsync(file.Id, CancellationToken.None);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());
        Assert.True(File.Exists(Path.Combine(_db.Paths.DataDirectory, "uploads", file.Id + ".bin")));
        Assert.True(File.Exists(Path.Combine(_db.Paths.DataDirectory, "uploads", file.Id + ".json")));
    }

    [Fact]
    public async Task NamesAreReducedToTheFileName()
    {
        var file = await UploadAsync("../../etc/C:\\temp\\AXIS_app.eap", [1, 2, 3]);

        Assert.Equal("AXIS_app.eap", file.Name);
    }

    [Fact]
    public async Task TheSizeLimitComesFromTheSetting()
    {
        Assert.Equal(2048L * 1024 * 1024, await _store.GetMaxBytesAsync(CancellationToken.None));
        await _db.Get<ServerSettingsStore>().SetAsync(SettingKeys.UploadsMaxMegabytes, 1, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UploadRejectedException>(() => _store.BeginAsync("big.bin", 1024 * 1024 + 1, CancellationToken.None));

        Assert.True(ex.TooLarge);
        Assert.Contains("1 MB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreDataThanAnnouncedIsRejectedAndNothingIsKept()
    {
        await using (var writer = await _store.BeginAsync("a.bin", 4, CancellationToken.None))
        {
            await writer.WriteAsync(new byte[3], CancellationToken.None);
            await Assert.ThrowsAsync<UploadRejectedException>(() => writer.WriteAsync(new byte[2], CancellationToken.None));
        }

        Assert.Empty(Directory.GetFiles(_store.Directory));
    }

    [Fact]
    public async Task AnIncompleteUploadIsRejectedAndNothingIsKept()
    {
        await using (var writer = await _store.BeginAsync("a.bin", 10, CancellationToken.None))
        {
            await writer.WriteAsync(new byte[3], CancellationToken.None);
            var ex = await Assert.ThrowsAsync<UploadRejectedException>(() => writer.CompleteAsync(CancellationToken.None));
            Assert.False(ex.TooLarge);
        }

        Assert.Empty(Directory.GetFiles(_store.Directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("folder/")]
    public async Task AnEmptyNameIsRejected(string name)
    {
        await Assert.ThrowsAsync<UploadRejectedException>(() => _store.BeginAsync(name, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("../oadm")]
    [InlineData("..\\..\\master.key")]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFAB")]
    [InlineData("0123")]
    public async Task InvalidIdsAreNeverResolved(string id)
    {
        Assert.False(UploadStore.IsValidId(id));
        Assert.Null(await _store.FindAsync(id, CancellationToken.None));
        Assert.False(await _store.DeleteAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.OpenReadAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteRemovesTheUpload()
    {
        var file = await UploadAsync("a.bin", [1]);

        Assert.True(await _store.DeleteAsync(file.Id, CancellationToken.None));
        Assert.Null(await _store.FindAsync(file.Id, CancellationToken.None));
        Assert.False(await _store.DeleteAsync(file.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UploadsOlderThanTheRetentionAreDeleted()
    {
        var old = await UploadAsync("old.bin", [1]);
        var fresh = await UploadAsync("fresh.bin", [2]);
        var oldTime = _time.GetUtcNow().UtcDateTime.AddHours(-25);
        foreach (var path in Directory.GetFiles(_store.Directory, old.Id + ".*"))
        {
            File.SetLastWriteTimeUtc(path, oldTime);
        }

        var orphan = Path.Combine(_store.Directory, Guid.NewGuid().ToString("N") + ".part");
        await File.WriteAllBytesAsync(orphan, [9]);
        File.SetLastWriteTimeUtc(orphan, oldTime);

        Assert.Equal(TimeSpan.FromHours(24), await _store.GetRetentionAsync(CancellationToken.None));
        Assert.Equal(1, await _store.DeleteExpiredAsync(CancellationToken.None));

        Assert.Null(await _store.FindAsync(old.Id, CancellationToken.None));
        Assert.NotNull(await _store.FindAsync(fresh.Id, CancellationToken.None));
        Assert.False(File.Exists(orphan));
        Assert.Equal(2, Directory.GetFiles(_store.Directory).Length); // fresh .bin + .json
    }

    [Fact]
    public async Task CleanupWithoutUploadsDoesNothing()
    {
        Assert.Equal(0, await new UploadStore(new OadmPaths(TestDatabase.NewTempDirectory())).DeleteExpiredAsync(CancellationToken.None));
    }

    private async Task<Oadm.Sdk.Plugins.UploadedFile> UploadAsync(string name, byte[] content, int chunk = 1000)
    {
        await using var writer = await _store.BeginAsync(name, content.Length, CancellationToken.None);
        for (var offset = 0; offset < content.Length; offset += chunk)
        {
            await writer.WriteAsync(content.AsMemory(offset, Math.Min(chunk, content.Length - offset)), CancellationToken.None);
        }

        return await writer.CompleteAsync(CancellationToken.None);
    }
}
