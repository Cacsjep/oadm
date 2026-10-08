using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Discovery;

namespace Oadm.Core.Tests.Discovery;

public class DiscoveryServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RangeScanStreamsDevicesProgressAndFinish()
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>
        {
            ["10.0.0.48"] = FakeProbe.Device("10.0.0.48", "B8A44F631339"),
            ["10.0.0.200"] = FakeProbe.Device("10.0.0.200", "B8A44F3B34BB", "M3206-LVE", DiscoveredDeviceStatus.PasswordNotSet),
        });
        await using var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe);

        var session = service.StartRangeScan(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.254"));
        var events = await Collect(service, session.Id);

        Assert.Equal(DiscoverySessionKind.RangeScan, session.Kind);
        var last = events[^1];
        Assert.True(last.Finished);
        Assert.Equal(100, last.ProgressPercent);
        Assert.Null(last.Device);
        Assert.Single(events, e => e.Finished);

        var devices = events.Where(e => e.Device is not null).Select(e => e.Device!).ToList();
        Assert.Equal(2, devices.Count);
        var m3206 = devices.Single(d => d.Serial == "B8A44F3B34BB");
        Assert.Equal("B8A44F3B34BB", m3206.DiscoveredId);
        Assert.Equal(IPAddress.Parse("10.0.0.200"), m3206.Address);
        Assert.Equal("M3206-LVE", m3206.Model);
        Assert.Equal(DiscoveredDeviceStatus.PasswordNotSet, m3206.Status);
        Assert.Equal("https", m3206.Scheme);
        Assert.Equal(DiscoverySources.RangeScan, m3206.Sources);

        var progress = events.Select(e => e.ProgressPercent).ToList();
        Assert.Equal(progress.Order(), progress);
    }

    [Fact]
    public async Task WatchAfterFinishReplaysDevicesAndFinishEvent()
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult> { ["10.0.0.48"] = FakeProbe.Device("10.0.0.48", "B8A44F631339") });
        await using var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe);
        var session = service.StartRangeScan(IPAddress.Parse("10.0.0.40"), IPAddress.Parse("10.0.0.50"));
        await Collect(service, session.Id);

        var replay = await Collect(service, session.Id);

        Assert.Equal(2, replay.Count);
        Assert.Equal("B8A44F631339", replay[0].Device!.Serial);
        Assert.True(replay[1].Finished);
        Assert.Single(service.GetDevices(session.Id));
    }

    [Fact]
    public async Task ZeroConfDeduplicatesBySerialAndEnrichesFromProbe()
    {
        var browser = new FakeMdnsBrowser();
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>
        {
            ["10.0.0.48"] = FakeProbe.Device("10.0.0.48", "B8A44F631339", "P3265-V"),
        });
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe);
        var session = service.StartZeroConf();

        browser.Announce("10.0.0.48", "B8A44F631339", "axis-b8a44f631339");
        browser.Announce("10.0.0.48", "B8A44F631339", "axis-b8a44f631339");

        var device = await WaitForAsync(service, session.Id, d => d.Model is not null);
        Assert.Equal("P3265-V", device.Model);
        Assert.Equal("axis-b8a44f631339", device.HostName);
        Assert.Equal(DiscoveredDeviceStatus.CredentialsRequired, device.Status);
        Assert.Equal(DiscoverySources.Mdns, device.Sources);
        Assert.Single(service.GetDevices(session.Id));
        Assert.Single(probe.Probed); // same address probed once

        await service.StopAsync(session.Id);
        Assert.Throws<KeyNotFoundException>(() => service.GetDevices(session.Id));
    }

    [Fact]
    public async Task ZeroConfDeviceWithoutHttpAnswerBecomesUnreachable()
    {
        var browser = new FakeMdnsBrowser();
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>());
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe);
        var session = service.StartZeroConf();

        browser.Announce("10.0.0.77", "ACCC8E000077");

        var device = await WaitForAsync(service, session.Id, d => d.Status == DiscoveredDeviceStatus.Unreachable);
        Assert.Equal("ACCC8E000077", device.Serial);
        Assert.Null(device.Model);
    }

    [Fact]
    public async Task ZeroConfInstanceWithoutSerialUsesProbeSerial()
    {
        var browser = new FakeMdnsBrowser();
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult> { ["10.0.0.9"] = FakeProbe.Device("10.0.0.9", "ACCC8E000009") });
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe);
        var session = service.StartZeroConf();

        browser.Announce("10.0.0.9", serial: null);

        var device = await WaitForAsync(service, session.Id, _ => true);
        Assert.Equal("ACCC8E000009", device.Serial);
    }

    [Fact]
    public async Task StopEndsWatchStreamOfRunningSession()
    {
        var browser = new FakeMdnsBrowser();
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>());
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe);
        var session = service.StartZeroConf();
        var watch = Collect(service, session.Id);

        await service.StopAsync(session.Id);

        Assert.Empty(await watch.WaitAsync(TestTimeout));
        Assert.Empty(service.Sessions);
        await service.StopAsync(session.Id); // idempotent
    }

    [Fact]
    public async Task ZeroConfWithDurationFinishesAfterTheTimeLimitAndKeepsItsDevices()
    {
        var browser = new FakeMdnsBrowser();
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult> { ["10.0.0.48"] = FakeProbe.Device("10.0.0.48", "B8A44F631339", "P3265-V") });
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe, timeProvider: time);
        var session = service.StartZeroConf(duration: TimeSpan.FromSeconds(30));
        var watch = Collect(service, session.Id);

        browser.Announce("10.0.0.48", "B8A44F631339");
        await WaitForAsync(service, session.Id, d => d.Model is not null);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(watch.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        var events = await watch.WaitAsync(TestTimeout);

        Assert.True(events[^1].Finished);
        Assert.Equal(100, events[^1].ProgressPercent);
        Assert.Single(events, e => e.Finished);
        Assert.Contains(events, e => e.Device?.Model == "P3265-V");
        Assert.Single(service.GetDevices(session.Id)); // the session stays until Stop
        var replay = await Collect(service, session.Id);
        Assert.True(replay[^1].Finished);

        browser.Announce("10.0.0.49", "B8A44F631340"); // browsing has ended
        await Task.Delay(50);
        Assert.Single(service.GetDevices(session.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.StartZeroConf(duration: TimeSpan.Zero));
    }

    [Fact]
    public async Task StopScanEndsZeroConfWithAFinishedEvent()
    {
        var browser = new FakeMdnsBrowser();
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult> { ["10.0.0.48"] = FakeProbe.Device("10.0.0.48", "B8A44F631339") });
        await using var service = new DiscoveryService(browser, new RangeScanner(probe), probe);
        var session = service.StartZeroConf(); // no time limit
        browser.Announce("10.0.0.48", "B8A44F631339");
        await WaitForAsync(service, session.Id, d => d.Status != DiscoveredDeviceStatus.Unknown);
        var watch = Collect(service, session.Id);

        service.StopScan(session.Id);

        var events = await watch.WaitAsync(TestTimeout);
        Assert.True(events[^1].Finished);
        Assert.Single(service.GetDevices(session.Id));
        service.StopScan(session.Id); // finished: ignored
        service.StopScan("unknown");
        await service.StopAsync(session.Id);
        Assert.Empty(service.Sessions);
    }

    [Fact]
    public async Task StopScanEndsARangeScanWithTheAddressesProbedSoFar()
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult> { ["10.0.0.1"] = FakeProbe.Device("10.0.0.1", "B8A44F000001") }, delay: TimeSpan.FromMilliseconds(200));
        await using var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe);
        var session = service.StartRangeScan(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.3.254"), new RangeScanOptions { Parallelism = 4 });
        var watch = Collect(service, session.Id);

        // Stop once the first address answered (a fixed delay raced on busy CI runners); 1,020 addresses at 4 x 200 ms
        // take about 50 s, so the scan is still far from done here.
        var deadline = DateTime.UtcNow + TestTimeout;
        while (service.GetDevices(session.Id).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        service.StopScan(session.Id);

        var events = await watch.WaitAsync(TestTimeout);
        Assert.True(events[^1].Finished);
        Assert.True(events[^1].ProgressPercent < 100);
        Assert.Single(service.GetDevices(session.Id));
    }

    [Fact]
    public void InvalidRangeThrowsAndUnknownSessionThrows()
    {
        var probe = new FakeProbe(new Dictionary<string, DeviceProbeResult>());
        var service = new DiscoveryService(new FakeMdnsBrowser(), new RangeScanner(probe), probe);

        Assert.Throws<ArgumentException>(() => service.StartRangeScan(IPAddress.Parse("10.0.0.9"), IPAddress.Parse("10.0.0.1")));
        Assert.Throws<KeyNotFoundException>(() => service.WatchAsync("nope").GetAsyncEnumerator());
        Assert.Empty(service.Sessions);
    }

    [Fact]
    public void MergeFillsMissingFieldsAccumulatesSourcesAndIgnoresNoOps()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var t1 = t0.AddSeconds(5);
        var mdns = new DiscoveryService.DeviceObservation("B8A44F631339", IPAddress.Parse("10.0.0.48"), "axis-b8a44f631339", null, null, null, null, DiscoverySources.Mdns);
        var scan = new DiscoveryService.DeviceObservation("B8A44F631339", IPAddress.Parse("10.0.0.48"), null, "P3265-V", "12.11.77", "https", DiscoveredDeviceStatus.CredentialsRequired, DiscoverySources.RangeScan);

        var first = DiscoveryService.Merge(null, mdns, t0)!;
        Assert.Equal(DiscoveredDeviceStatus.Unknown, first.Status);
        Assert.Equal(t0, first.LastSeenUtc);

        var merged = DiscoveryService.Merge(first, scan, t1)!;
        Assert.Equal("axis-b8a44f631339", merged.HostName);
        Assert.Equal("P3265-V", merged.Model);
        Assert.Equal("12.11.77", merged.FirmwareVersion);
        Assert.Equal("https", merged.Scheme);
        Assert.Equal(DiscoveredDeviceStatus.CredentialsRequired, merged.Status);
        Assert.Equal(DiscoverySources.Mdns | DiscoverySources.RangeScan, merged.Sources);
        Assert.Equal(t1, merged.LastSeenUtc);

        Assert.Null(DiscoveryService.Merge(merged, mdns, t1.AddSeconds(1)));
        Assert.Null(DiscoveryService.Merge(merged, scan, t1.AddSeconds(1)));

        var moved = DiscoveryService.Merge(merged, mdns with { Address = IPAddress.Parse("10.0.0.49") }, t1)!;
        Assert.Equal(IPAddress.Parse("10.0.0.49"), moved.Address);
        Assert.Equal("P3265-V", moved.Model);
    }

    private static async Task<List<DiscoveryEvent>> Collect(DiscoveryService service, string sessionId)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var list = new List<DiscoveryEvent>();
        await foreach (var e in service.WatchAsync(sessionId, cts.Token))
        {
            list.Add(e);
        }

        return list;
    }

    private static async Task<DiscoveredDevice> WaitForAsync(DiscoveryService service, string sessionId, Func<DiscoveredDevice, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await foreach (var e in service.WatchAsync(sessionId, cts.Token))
        {
            if (e.Device is { } d && predicate(d))
            {
                return d;
            }
        }

        throw new InvalidOperationException("stream ended");
    }
}
