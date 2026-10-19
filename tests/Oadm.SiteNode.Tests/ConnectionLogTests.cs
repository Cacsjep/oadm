using Microsoft.Extensions.Time.Testing;

using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

public sealed class ConnectionLogTests
{
    [Fact]
    public async Task TheNewestEntriesStayInMemoryNewestFirst()
    {
        using var folder = new TempFolder();
        await using var log = new ConnectionLog(null, TimeProvider.System, capacity: 3);

        for (var i = 1; i <= 5; i++)
            log.Add("OADM-SRV", $"192.168.0.{i}:80", "Short", "Connected");

        Assert.Equal(3, log.Count);
        Assert.Equal(["192.168.0.5:80", "192.168.0.4:80", "192.168.0.3:80"], log.Newest(10).Select(e => e.Target));
        Assert.Equal(["192.168.0.5:80"], log.Newest(1).Select(e => e.Target));
    }

    [Fact]
    public async Task EveryLineGoesToTheFileOfItsDayAndOldFilesAreRemoved()
    {
        using var folder = new TempFolder();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 23, 59, 0, TimeSpan.Zero));
        var old = Path.Combine(folder.Path, "connections-20260901.log");
        var recent = Path.Combine(folder.Path, "connections-20261001.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        await using var log = new ConnectionLog(folder.Path, time);

        log.Add("OADM-SRV", "192.168.0.20:443", "Short", "Connected");
        time.Advance(TimeSpan.FromMinutes(2));
        log.Add("OADM-SRV", "192.168.0.21:554", "Permanent", "Refused\nwith\ttabs");
        await log.FlushAsync(CancellationToken.None);

        var first = File.ReadAllText(Path.Combine(folder.Path, "connections-20261019.log"));
        var second = File.ReadAllText(Path.Combine(folder.Path, "connections-20261020.log"));
        Assert.Contains("192.168.0.20:443\tShort\tConnected", first, StringComparison.Ordinal);
        Assert.Contains("192.168.0.21:554\tPermanent\tRefused with tabs", second, StringComparison.Ordinal);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
