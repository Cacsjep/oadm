using System.Diagnostics;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Tests.Persistence;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Devices;

/// <summary>The import's waiting list: one entry per address, passwords encrypted, 30,000 entries at most.</summary>
public sealed class WaitingDeviceStoreTests(ITestOutputHelper output) : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private WaitingDeviceStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = _db.Get<WaitingDeviceStore>();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static WaitingLine Line(string address, string? password = null) =>
        new(address, null, password is null ? null : "root", password, []);

    [Fact]
    public async Task PasswordsAreStoredEncryptedAndOnlyDecryptedForATry()
    {
        await _store.AddAsync([Line("10.0.0.5", "Very-Secret-9")], "Import from file a.csv", "admin", CancellationToken.None);

        await using (var db = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var row = await db.WaitingDevices.SingleAsync();
            Assert.DoesNotContain("Very-Secret-9", Encoding.UTF8.GetString(row.EncryptedPassword!), StringComparison.Ordinal);
        }

        var (items, _) = await _store.ListAsync(0, 0, CancellationToken.None);
        Assert.True(Assert.Single(items).HasLogin);
        var tryOne = Assert.Single(await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None));
        Assert.Equal(("root", "Very-Secret-9"), (tryOne.Login!.UserName, tryOne.Login.Password));
        Assert.DoesNotContain("Very-Secret-9", tryOne.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedLoginsWaitForAnExplicitTryAndResultsAreRecorded()
    {
        await _store.AddAsync([Line("10.0.0.6"), Line("10.0.0.7")], "Import", "admin", CancellationToken.None);
        var all = await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None);
        Guid failed = all.Single(t => t.Address == "10.0.0.6").Id;
        await _store.RecordAsync([new WaitingResult(failed, "The user name or password is wrong.", LoginFailed: true)], CancellationToken.None);

        Assert.Equal("10.0.0.7", Assert.Single(await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None)).Address);
        Assert.Equal(2, (await _store.ToTryAsync(null, includeLoginFailed: true, CancellationToken.None)).Count);
        Assert.Single(await _store.ToTryAsync([failed], includeLoginFailed: false, CancellationToken.None));
        Assert.Equal((2, 1), await _store.CountsAsync(CancellationToken.None));

        // Kept again: tried again like new.
        await _store.AddAsync([Line("10.0.0.6")], "Import", "admin", CancellationToken.None);
        Assert.Equal((2, 0), await _store.CountsAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task ThirtyThousandEntriesFitAndOneMoreIsRefusedWhole()
    {
        var lines = Enumerable.Range(0, WaitingDeviceStore.MaxEntries).Select(i => Line($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", "pass" + i)).ToList();
        var watch = Stopwatch.StartNew();
        var result = await _store.AddAsync(lines, "Import", "admin", CancellationToken.None);
        long add = watch.ElapsedMilliseconds;
        Assert.Equal(WaitingDeviceStore.MaxEntries, result.Total);

        watch.Restart();
        var (page, total) = await _store.ListAsync(0, 5_000, CancellationToken.None);
        long list = watch.ElapsedMilliseconds;
        Assert.Equal((5_000, WaitingDeviceStore.MaxEntries), (page.Count, total));

        var full = await Assert.ThrowsAsync<WaitingListFullException>(() => _store.AddAsync([Line("10.200.0.1")], "Import", "admin", CancellationToken.None));
        Assert.Equal(WaitingDeviceStore.FullText, full.Message);
        Assert.Equal(WaitingDeviceStore.MaxEntries, await _store.CountAsync(CancellationToken.None));

        watch.Restart();
        var tries = await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None);
        long decrypt = watch.ElapsedMilliseconds;
        Assert.Equal(WaitingDeviceStore.MaxEntries, tries.Count);
        output.WriteLine($"30,000 lines kept in {add} ms, a page of 5,000 listed in {list} ms, all decrypted in {decrypt} ms");
        Assert.True(add < 30_000 && list < 3_000 && decrypt < 10_000, "too slow");
    }
}
