using System.Diagnostics;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Plugins;

/// <summary>Plugin records in the PluginRecords table: one row per record, per plugin and collection, batch writes.</summary>
#pragma warning disable CA1001 // disposed in DisposeAsync (IAsyncLifetime)
public sealed class PluginRecordsTests(ITestOutputHelper output) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private DbPluginRecords Records(string pluginId) => new(_db.Get<IDbContextFactory<OadmDbContext>>(), pluginId);

    [Fact]
    public async Task RecordsAreWrittenChangedAndDeletedPerPluginAndCollection()
    {
        var est = Records("oadm.est");
        var other = Records("acme.other");
        await est.SetManyAsync("certificates", [new("A1", """{"n":1}"""), new("A2", """{"n":2}""")], CancellationToken.None);
        await other.SetManyAsync("certificates", [new("A1", """{"other":true}""")], CancellationToken.None);
        await est.SetManyAsync("sites", [new("S1", "[]")], CancellationToken.None);

        await est.SetManyAsync("certificates", [new("A1", """{"n":10}"""), new("A2", null), new("A3", """{"n":3}""")], CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["A1"] = """{"n":10}""", ["A3"] = """{"n":3}""" }, await est.ListAsync("certificates", CancellationToken.None));
        Assert.Equal("""{"other":true}""", (await other.ListAsync("certificates", CancellationToken.None))["A1"]);
        Assert.Single(await est.ListAsync("sites", CancellationToken.None));

        await est.ClearAsync("certificates", CancellationToken.None);
        Assert.Empty(await est.ListAsync("certificates", CancellationToken.None));
        Assert.Single(await other.ListAsync("certificates", CancellationToken.None));
        Assert.Single(await est.ListAsync("sites", CancellationToken.None));
    }

    [Fact]
    public async Task BadKeysAndValuesAreRefusedBeforeAnythingIsWritten()
    {
        var est = Records("oadm.est");

        await Assert.ThrowsAsync<ArgumentException>(() => est.SetManyAsync("certificates", [new("A1", "{}"), new("A2", "not json")], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => est.SetManyAsync("certificates", [new(new string('k', 256), "{}")], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => est.SetManyAsync(new string('c', 65), [new("A1", "{}")], CancellationToken.None));

        Assert.Empty(await est.ListAsync("certificates", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task SixtyThousandRecordsAreWrittenInOneCallAndOneOfThemChangesAlone()
    {
        var est = Records("oadm.est");
        var value = """{"serialNumber":"0123456789ABCDEF0123456789ABCDEF","device":"10.0.0.48","profile":"https"}""";
        var watch = Stopwatch.StartNew();
        await est.SetManyAsync("certificates", [.. Enumerable.Range(0, 60_000).Select(i => new KeyValuePair<string, string?>($"C{i}", value))], CancellationToken.None);
        var all = watch.Elapsed;

        watch.Restart();
        await est.SetManyAsync("certificates", [new("C30000", """{"changed":true}""")], CancellationToken.None);
        var one = watch.Elapsed;

        watch.Restart();
        var listed = await est.ListAsync("certificates", CancellationToken.None);
        var read = watch.Elapsed;

        output.WriteLine($"60,000 records written in {all.TotalMilliseconds:N0} ms, one changed in {one.TotalMilliseconds:N0} ms, read in {read.TotalMilliseconds:N0} ms");
        Assert.Equal(60_000, listed.Count);
        Assert.Equal("""{"changed":true}""", listed["C30000"]);
        Assert.True(all < TimeSpan.FromSeconds(60), $"60,000 records took {all}");
        Assert.True(one < TimeSpan.FromSeconds(2), $"one record took {one}");
    }
}
