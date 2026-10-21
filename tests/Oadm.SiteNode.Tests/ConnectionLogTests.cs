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

    [Fact]
    public async Task TheSameLineWithinAMinuteIsCountedAndWrittenOnceWithItsCount()
    {
        using var folder = new TempFolder();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 5, TimeSpan.Zero));
        await using var log = new ConnectionLog(folder.Path, time);

        for (var i = 0; i < 5; i++)
            log.Add("OADM-SRV", "192.168.0.20:443", "Short", "Connected");
        log.Add("OADM-SRV", "192.168.0.21:443", "Short", "Connected");
        Assert.Equal(2, log.Newest(10).Count);

        time.Advance(TimeSpan.FromMinutes(1));
        log.Add("OADM-SRV", "192.168.0.20:443", "Short", "Connected");

        Assert.Equal(["Connected", "Connected (4 more)", "Connected", "Connected"], log.Newest(10).Select(e => e.Result));
        await log.FlushAsync(CancellationToken.None);
        var lines = File.ReadAllLines(Path.Combine(folder.Path, "connections-20261019.log"));
        Assert.Equal(4, lines.Length);
        Assert.Contains(lines, l => l.EndsWith("192.168.0.20:443	Short	Connected (4 more)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFilesStayUnderTheSizeCapOldestFirst()
    {
        using var folder = new TempFolder();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        foreach (var day in new[] { "20261016", "20261017", "20261018" })
            File.WriteAllText(Path.Combine(folder.Path, $"connections-{day}.log"), new string('x', 1000));
        await using var log = new ConnectionLog(folder.Path, time, maxBytes: 2500);

        log.Add("OADM-SRV", "192.168.0.20:443", "Short", "Connected");
        await log.FlushAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(folder.Path, "connections-20261016.log")));
        Assert.True(File.Exists(Path.Combine(folder.Path, "connections-20261018.log")));
        Assert.True(File.Exists(Path.Combine(folder.Path, "connections-20261019.log")));
        Assert.True(Directory.EnumerateFiles(folder.Path).Sum(f => new FileInfo(f).Length) <= 2500 + 200);
    }

    [Fact]
    public async Task LinesTheDiskCannotTakeAreDroppedAndCountedNeverQueuedWithoutEnd()
    {
        using var folder = new TempFolder();
        await using var log = new ConnectionLog(folder.Path, TimeProvider.System, capacity: 100, maxPending: 1);

        for (var i = 0; i < 5000; i++)
            log.Add("OADM-SRV", $"10.{i / 65536}.{i / 256 % 256}.{i % 256}:443", "Short", "Connected");
        await log.FlushAsync(CancellationToken.None);

        Assert.Equal(100, log.Count);
        var written = Directory.EnumerateFiles(folder.Path).SelectMany(File.ReadAllLines).Count(l => l.Contains("	Short	", StringComparison.Ordinal));
        Assert.Equal(5000, written + log.Dropped);
    }
}
