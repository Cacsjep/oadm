using System.Diagnostics;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Tests.Persistence;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Devices;

/// <summary>The import's waiting list: one entry per address, passwords encrypted, 30,000 entries at most.</summary>
#pragma warning disable CA1001 // disposed in DisposeAsync (IAsyncLifetime)
public sealed class WaitingDeviceStoreTests(ITestOutputHelper output) : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private WaitingDeviceStore _store = null!;
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = new WaitingDeviceStore(_db.Get<IDbContextFactory<OadmDbContext>>(), _db.Get<Oadm.Core.Security.CredentialProtector>(), _time);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _db.DisposeAsync();
    }

    private static WaitingLine Line(string address, string? password = null) =>
        new(address, null, password is null ? null : "root", password, []);

    [Fact]
    public async Task OneEntryPerSiteAndAddressAndTheDropCheckComparesPerRoute()
    {
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var result = await _store.AddAsync(
        [
            Line("192.168.0.20") with { SiteId = siteA },
            Line("192.168.0.20") with { SiteId = siteB },
            Line("192.168.0.20"),
            Line("https://192.168.0.20/") with { SiteId = siteA }, // the same (site, address) again: replaces
        ], "Import", "admin", CancellationToken.None);
        Assert.Equal((3, 3), (result.Added, result.Total));
        Assert.NotEqual(WaitingDeviceStore.Normalize("192.168.0.20", siteA), WaitingDeviceStore.Normalize("192.168.0.20", siteB));
        Assert.Equal(WaitingDeviceStore.Normalize("192.168.0.20"), WaitingDeviceStore.Normalize("192.168.0.20", null));

        var page = await _store.ListAsync(null, 0, CancellationToken.None);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(new HashSet<Guid?> { null, siteA, siteB }, page.Items.Select(i => i.SiteId).ToHashSet());
        Assert.Equal(3, (await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None)).Count(t => t.Address.Contains("192.168.0.20", StringComparison.Ordinal)));

        // A device at site A with that address: only site A's entry leaves; site B's and the one for everywhere stay.
        var gone = await _store.RemoveManagedAsync([new ManagedAddress("ACCC8E0000A1", "192.168.0.20", siteA)], CancellationToken.None);
        Assert.Single(gone);
        var left = (await _store.ListAsync(null, 0, CancellationToken.None)).Items;
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, i => i.SiteId == siteA);

        // On the server's own network: the entry for everywhere leaves too.
        await _store.RemoveManagedAsync([new ManagedAddress("ACCC8E0000C1", "192.168.0.20", null)], CancellationToken.None);
        Assert.Equal(siteB, Assert.Single((await _store.ListAsync(null, 0, CancellationToken.None)).Items).SiteId);
    }

    [Fact]
    public async Task PasswordsAreStoredEncryptedAndOnlyDecryptedForATry()
    {
        await _store.AddAsync([Line("10.0.0.5", "Very-Secret-9")], "Import from file a.csv", "admin", CancellationToken.None);

        await using (var db = await _db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var row = await db.WaitingDevices.SingleAsync();
            Assert.DoesNotContain("Very-Secret-9", Encoding.UTF8.GetString(row.EncryptedPassword!), StringComparison.Ordinal);
        }

        var page = await _store.ListAsync(null, 0, CancellationToken.None);
        Assert.True(Assert.Single(page.Items).HasLogin);
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
        await _store.RecordAsync([new WaitingResult(failed, all.Single(t => t.Id == failed).Kept, "The user name or password is wrong.", LoginFailed: true)], CancellationToken.None);

        Assert.Equal("10.0.0.7", Assert.Single(await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None)).Address);
        Assert.Equal(2, (await _store.ToTryAsync(null, includeLoginFailed: true, CancellationToken.None)).Count);
        Assert.Single(await _store.ToTryAsync([failed], includeLoginFailed: false, CancellationToken.None));
        Assert.Equal((2, 1), await _store.CountsAsync(CancellationToken.None));

        // Kept again: tried again like new.
        await _store.AddAsync([Line("10.0.0.6")], "Import", "admin", CancellationToken.None);
        Assert.Equal((2, 0), await _store.CountsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AResultOfATryThatStartedBeforeTheLineWasKeptAgainIsNotWritten()
    {
        await _store.AddAsync([Line("10.0.0.8", "old-Pass-1")], "Import", "admin", CancellationToken.None);
        var stale = Assert.Single(await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None));
        Assert.True(await _store.IsCurrentAsync(stale.Id, stale.Kept, CancellationToken.None));

        // Kept again with a corrected password while the try with the old one runs.
        _time.Advance(TimeSpan.FromSeconds(5));
        await _store.AddAsync([Line("10.0.0.8", "new-Pass-1")], "Import", "admin", CancellationToken.None);
        Assert.False(await _store.IsCurrentAsync(stale.Id, stale.Kept, CancellationToken.None));
        await _store.RecordAsync([new WaitingResult(stale.Id, stale.Kept, "The user name or password is wrong.", LoginFailed: true)], CancellationToken.None);

        var fresh = Assert.Single((await _store.ListAsync(null, 0, CancellationToken.None)).Items);
        Assert.False(fresh.LoginFailed);
        Assert.Equal(0, fresh.Tries);
        Assert.Equal("new-Pass-1", Assert.Single(await _store.ToTryAsync(null, includeLoginFailed: false, CancellationToken.None)).Login!.Password);

        // Removed: not current either.
        await _store.RemoveAsync([fresh.Id], all: false, CancellationToken.None);
        Assert.False(await _store.IsCurrentAsync(fresh.Id, fresh.CreatedUtc, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://10.0.0.60:8443/", "10.0.0.60:8443")]
    [InlineData(" http://Cam1.example.com ", "CAM1.EXAMPLE.COM")]
    [InlineData("[fe80::1]", "FE80::1")]
    [InlineData("[fe80::1]:8443", "[FE80::1]:8443")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void AddressesAreComparedAsADeviceStoresThem(string address, string normalized) =>
        Assert.Equal(normalized, WaitingDeviceStore.Normalize(address));

    [Fact]
    public async Task PagesByCursorNeverSkipAnEntryWhenOthersAreRemovedMeanwhile()
    {
        await _store.AddAsync([.. Enumerable.Range(10, 30).Select(i => Line($"10.0.1.{i}"))], "Import", "admin", CancellationToken.None);
        var first = await _store.ListAsync(null, 10, CancellationToken.None);
        await _store.RemoveAsync([.. first.Items.Take(5).Select(i => i.Id)], all: false, CancellationToken.None); // a round adds five

        var seen = first.Items.Select(i => i.Address).ToList();
        for (var after = first.NextAfter; after is not null;)
        {
            var page = await _store.ListAsync(after, 10, CancellationToken.None);
            seen.AddRange(page.Items.Select(i => i.Address));
            after = page.NextAfter;
        }

        Assert.Equal(30, seen.Count);
        Assert.Equal(30, seen.Distinct().Count());
    }

    [Fact]
    public async Task TwoImportsAtOnceKeepOneEntryPerAddressAndTheLimit()
    {
        var a = Task.Run(() => _store.AddAsync([Line("10.0.2.1"), Line("10.0.2.2")], "Import a", "tech1", CancellationToken.None));
        var b = Task.Run(() => _store.AddAsync([Line("10.0.2.2"), Line("10.0.2.3")], "Import b", "tech2", CancellationToken.None));
        await Task.WhenAll(a, b);
        Assert.Equal(3, await _store.CountAsync(CancellationToken.None));
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
        var first = await _store.ListAsync(null, 5_000, CancellationToken.None);
        long list = watch.ElapsedMilliseconds;
        Assert.Equal((5_000, WaitingDeviceStore.MaxEntries), (first.Items.Count, first.Total));
        Assert.NotNull(first.NextAfter);

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
