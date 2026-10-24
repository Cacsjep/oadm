using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// 30,000 devices with four certificates each (HTTPS and IEEE 802.1X, each renewed once, as OADM before 4.5 kept them): the
/// list of current certificates loads, pages, filters and searches in one pass, a revocation of 5,000 is one write, and the
/// 802.1X server's revocation check stays a hash lookup. Budgets about ten times the measured times; the times go to the test output.
/// </summary>
[Trait("Category", "Perf")]
public sealed class IssuedScaleTests(ITestOutputHelper output)
{
    private const int Devices = 30_000;

    [Fact]
    public async Task Thirty_thousand_devices_with_four_certificates_each_stay_fast()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var records = new MemoryPluginRecords();
        var store = new PkiStore(new InMemoryPluginSettingsProvider().GetSettings(PkiPluginInfo.PluginId));
        var devices = new Dictionary<Guid, IDeviceInfo>(Devices);
        var rows = new List<KeyValuePair<string, string?>>(Devices * 4);
        for (var i = 0; i < Devices; i++)
        {
            var device = new PkiFakeDevice(Guid.NewGuid(), string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"), string.Create(CultureInfo.InvariantCulture, $"ACCC8E{i:X6}"));
            devices[device.Id] = device;
            for (var k = 0; k < 4; k++)
            {
                var entry = new IssuedCertificate
                {
                    SerialNumber = string.Create(CultureInfo.InvariantCulture, $"{i:X8}{k:X2}AB"),
                    DeviceId = device.Id,
                    DeviceSerial = device.Serial,
                    Purpose = k % 2 == 0 ? CertificatePurpose.Https : CertificatePurpose.Dot1x,
                    IssuedUtc = now.UtcDateTime.AddDays(-200 + (k / 2 * 100)).AddSeconds(i),
                    NotAfterUtc = now.UtcDateTime.AddDays(165 + (k / 2 * 100)),
                    Source = k == 3 ? IssuedCertificateSources.Est : null,
                };
                rows.Add(new(entry.SerialNumber, PkiJson.Serialize(entry)));
            }
        }

        foreach (var chunk in rows.Chunk(10_000))
        {
            await records.SetManyAsync(IssuedRegistry.IssuedCollection, chunk, CancellationToken.None);
        }

        var watch = Stopwatch.StartNew();
        var registry = new IssuedRegistry(store, time, records);
        await registry.LoadAsync(CancellationToken.None);
        var load = watch.Elapsed;
        Assert.Equal(Devices * 4, registry.Count);

        watch.Restart();
        var first = registry.Query(new IssuedCertificateQuery { Limit = 500 }, devices, onlyListedDevices: false, 30);
        var page = watch.Elapsed;
        Assert.Equal((Devices * 2, 500), (first.Total, first.Rows.Count)); // the current HTTPS and IEEE 802.1X ones

        watch.Restart();
        var dot1x = registry.Query(new IssuedCertificateQuery { State = IssuedCertificateState.Valid, Purpose = CertificatePurpose.Dot1x, Limit = 500 }, devices, false, 30);
        var search = registry.Query(new IssuedCertificateQuery { Search = "10.0.100.", Limit = 500 }, devices, false, 30);
        var filters = watch.Elapsed;
        Assert.Equal(Devices, dot1x.Total);
        Assert.Equal(256 * 2, search.Total);
        Assert.Equal(Devices * 4, registry.Query(new IssuedCertificateQuery { Limit = 1 }, devices, false, _ => 30, currentOnly: false).Total); // the whole store

        // A user limited to a group of 3,000 devices.
        var group = devices.Take(3000).ToDictionary(p => p.Key, p => p.Value);
        watch.Restart();
        var groupPage = registry.Query(new IssuedCertificateQuery { Offset = 5_500, Limit = 500 }, group, onlyListedDevices: true, 30);
        var groupTime = watch.Elapsed;
        Assert.Equal((6_000, 500), (groupPage.Total, groupPage.Rows.Count));

        // Revoke 5,000 at once: one write of the revocations.
        var chosen = first.Rows.Select(r => r.SerialNumber).Concat(registry.Query(new IssuedCertificateQuery { Offset = 500, Limit = 500 }, devices, false, 30).Rows.Select(r => r.SerialNumber)).ToList();
        chosen.AddRange(Enumerable.Range(0, 4000).Select(i => string.Create(CultureInfo.InvariantCulture, $"{i:X8}00AB")));
        var written = records.WrittenRecords;
        watch.Restart();
        var result = await registry.RevokeAsync(chosen, RevocationReason.KeyCompromised, "admin", CancellationToken.None);
        var revoke = watch.Elapsed;
        Assert.Equal(chosen.Distinct().Count(), result.Revoked);
        Assert.True(records.WrittenRecords > written); // the revocations, and the revoked ones leave the store
        Assert.Equal(Devices * 2 - 1000, registry.Query(new IssuedCertificateQuery { Limit = 1 }, devices, false, 30).Total); // the 1,000 listed ones; the other 4,000 were not current

        // The 802.1X server asks for every authentication: a million lookups.
        watch.Restart();
        var hits = 0;
        for (var i = 0; i < 1_000_000; i++)
        {
            if (registry.IsRevoked(string.Create(CultureInfo.InvariantCulture, $"{i % Devices:X8}00AB")))
            {
                hits++;
            }
        }

        var lookups = watch.Elapsed;
        Assert.True(hits > 0);
        output.WriteLine($"load {load.TotalMilliseconds:F0} ms, first page {page.TotalMilliseconds:F0} ms, two filtered pages {filters.TotalMilliseconds:F0} ms, group page {groupTime.TotalMilliseconds:F0} ms, revoke {result.Revoked:N0} {revoke.TotalMilliseconds:F0} ms, 1,000,000 lookups {lookups.TotalMilliseconds:F0} ms");
        Assert.True(load < TimeSpan.FromSeconds(5), $"load took {load}");
        Assert.True(page < TimeSpan.FromSeconds(2), $"a page took {page}");
        Assert.True(filters < TimeSpan.FromSeconds(4), $"two filtered pages took {filters}");
        Assert.True(groupTime < TimeSpan.FromSeconds(2), $"a group's page took {groupTime}");
        Assert.True(revoke < TimeSpan.FromSeconds(1), $"revoking took {revoke}");
        Assert.True(lookups < TimeSpan.FromSeconds(3), $"the lookups took {lookups}");
        await registry.DisposeAsync();
    }
}
