using System.Net;

using Oadm.Core.Discovery;

namespace Oadm.Core.Tests.Discovery;

public class RangeScannerTests
{
    private static Ipv4Range Range(string from, string to) => Ipv4Range.Create(IPAddress.Parse(from), IPAddress.Parse(to));

    [Fact]
    public async Task ProbesEveryAddressAndYieldsOnlyDevices()
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>
        {
            ["10.0.0.48"] = FakeProbe.Device("10.0.0.48", "B8A44F631339"),
            ["10.0.0.200"] = FakeProbe.Device("10.0.0.200", "B8A44F3B34BB"),
            ["10.0.1.1"] = FakeProbe.Device("10.0.1.1", "000000000000"), // outside the range
        });
        var scanner = new RangeScanner(probe);

        var found = await scanner.ScanAsync(Range("10.0.0.1", "10.0.0.254")).ToListAsync();

        Assert.Equal(["B8A44F3B34BB", "B8A44F631339"], found.Select(f => f.Serial).Order());
        Assert.Equal(254, probe.Probed.Count);
        Assert.Equal(254, probe.Probed.Distinct().Count());
    }

    [Fact]
    public async Task ProgressIsMonotonicAndEndsAt100()
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>());
        var reports = new List<int>();
        var progress = new InlineProgress(p =>
        {
            lock (reports)
            {
                reports.Add(p);
            }
        });

        await new RangeScanner(probe).ScanAsync(Range("10.0.0.1", "10.0.0.7"), new RangeScanOptions { Parallelism = 3 }, progress).ToListAsync();

        Assert.Equal(0, reports[0]);
        Assert.Equal(100, reports[^1]);
        Assert.Equal(reports.Order(), reports);
        Assert.Equal(reports.Distinct(), reports);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(32)]
    public async Task RespectsParallelism(int parallelism)
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>(), TimeSpan.FromMilliseconds(5));

        await new RangeScanner(probe).ScanAsync(Range("10.0.0.1", "10.0.0.64"), new RangeScanOptions { Parallelism = parallelism }).ToListAsync();

        Assert.InRange(probe.MaxConcurrency, 1, parallelism);
        Assert.Equal(64, probe.Probed.Count);
    }

    [Fact]
    public async Task PassesTimeoutToProbe()
    {
        var probe = new TimeoutRecordingProbe();

        await new RangeScanner(probe).ScanAsync(Range("10.0.0.1", "10.0.0.2"), new RangeScanOptions { Timeout = TimeSpan.FromMilliseconds(777) }).ToListAsync();

        Assert.All(probe.Timeouts, t => Assert.Equal(TimeSpan.FromMilliseconds(777), t));
        Assert.Equal(2, probe.Timeouts.Count);
    }

    [Fact]
    public async Task DefaultOptionsMatchSpec()
    {
        var options = new RangeScanOptions();
        Assert.Equal(32, options.Parallelism);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), options.Timeout);

        var probe = new TimeoutRecordingProbe();
        await new RangeScanner(probe).ScanAsync(Range("10.0.0.1", "10.0.0.1")).ToListAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(1500), Assert.Single(probe.Timeouts));
    }

    [Fact]
    public async Task CancellationStopsTheScan()
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>(), TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new RangeScanner(probe).ScanAsync(Range("10.0.0.0", "10.0.255.255"), new RangeScanOptions { Parallelism = 4 }, cancellationToken: cts.Token).ToListAsync());

        Assert.True(probe.Probed.Count < 1000);
    }

    [Fact]
    public async Task BreakingOutEarlyStopsProbing()
    {
        var all = Enumerable.Range(1, 254).ToDictionary(i => $"10.0.0.{i}", i => FakeProbe.Device($"10.0.0.{i}", $"ACCC8E0000{i:X2}"));
        var probe = new FakeProbe(all, TimeSpan.FromMilliseconds(5));

        await foreach (var _ in new RangeScanner(probe).ScanAsync(Range("10.0.0.1", "10.0.0.254"), new RangeScanOptions { Parallelism = 2 }))
        {
            break;
        }

        var count = probe.Probed.Count;
        await Task.Delay(100);
        Assert.Equal(count, probe.Probed.Count);
        Assert.True(count < 254);
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class TimeoutRecordingProbe : IDeviceProbe
    {
        public List<TimeSpan> Timeouts { get; } = [];

        public Task<DeviceProbeResult?> ProbeAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (Timeouts)
            {
                Timeouts.Add(timeout);
            }

            return Task.FromResult<DeviceProbeResult?>(null);
        }
    }
}
