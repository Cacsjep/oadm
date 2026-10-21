using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Server.AddDevices;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

namespace Oadm.Server.Tests.Perf;

/// <summary>The waiting list with 30,000 entries tried through sites (docs/spec/sites.md, Tests: Perf).</summary>
[Trait("Category", "Perf")]
public sealed class SiteDiscoveryScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ThirtyThousandWaitingDevicesAreTriedThroughTheirSites()
    {
        var sites = new FakeSiteTransport();
        var a = sites.AddSite("Building A");
        var b = sites.AddSite("Building B");
        var network = new FakeAxisNetwork();
        await using var host = await TestServerHost.StartAsync(network, configureServices: s =>
        {
            s.AddSingleton<ISiteStates>(sites);
            s.AddSingleton<ISiteNetwork>(sites);
        });

        // 10,000 for each site and 10,000 for everywhere; 1 of 100 at each site answers (factory default: no login needed).
        const int perPart = 10_000;
        var lines = new List<WaitingLine>(3 * perPart);
        for (var i = 0; i < perPart; i++)
        {
            var address = $"192.168.{i / 250}.{i % 250 + 1}";
            lines.Add(new WaitingLine(address, null, null, null, [], a.Id));
            lines.Add(new WaitingLine(address, null, null, null, [], b.Id));
            lines.Add(new WaitingLine($"10.50.{i / 250}.{i % 250 + 1}", null, null, null, []));
            if (i % 100 == 0)
            {
                network.AtSite(a.Id).Add(address, "ACCCA" + i.ToString("X7", System.Globalization.CultureInfo.InvariantCulture), null);
                network.AtSite(b.Id).Add(address, "ACCCB" + i.ToString("X7", System.Globalization.CultureInfo.InvariantCulture), null);
            }
        }

        var store = host.Get<WaitingDeviceStore>();
        var watch = Stopwatch.StartNew();
        await store.AddAsync(lines, "Import from file sites.csv", "admin", CancellationToken.None);
        var kept = watch.ElapsedMilliseconds;
        Assert.Equal(3 * perPart, await store.CountAsync(CancellationToken.None));

        watch.Restart();
        var added = await host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);
        var round = watch.ElapsedMilliseconds;

        output.WriteLine($"30,000 waiting entries (2 sites): kept in {kept} ms, one round through the sites in {round} ms, {added} added");
        Assert.Equal(200, added);
        Assert.Equal(3 * perPart - 200, await store.CountAsync(CancellationToken.None));
        var repository = host.Get<DeviceRepository>();
        Assert.Equal(a.Id, (await repository.FindBySerialAsync("ACCCA0000000", CancellationToken.None))!.SiteId);
        Assert.Equal(b.Id, (await repository.FindBySerialAsync("ACCCB0000000", CancellationToken.None))!.SiteId);
        Assert.True(round < 120_000, $"too slow: {round} ms");
    }
}
