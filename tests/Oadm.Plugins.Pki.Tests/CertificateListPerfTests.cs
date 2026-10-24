using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The Certificates card at 30,000 devices with two certificates each, plus certificates created here: the first page, a
/// deep page, the search over every column and each filter are one pass on the server. Budgets about ten times the
/// measured times; the times go to the test output.
/// </summary>
[Trait("Category", "Perf")]
public sealed class CertificateListPerfTests(ITestOutputHelper output)
{
    private const int Devices = 30_000;

    [Fact]
    public async Task Thirty_thousand_devices_with_two_certificates_each_page_search_and_filter_fast()
    {
        var now = DateTime.UtcNow;
        var records = new MemoryPluginRecords();
        var devices = new List<IDeviceInfo>(Devices);
        var rows = new List<KeyValuePair<string, string?>>(Devices * 2);
        for (var i = 0; i < Devices; i++)
        {
            var device = new PkiFakeDevice(Guid.NewGuid(), string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"), string.Create(CultureInfo.InvariantCulture, $"ACCC8E{i:X6}"));
            devices.Add(device);
            for (var k = 0; k < 2; k++)
            {
                var entry = new IssuedCertificate
                {
                    SerialNumber = string.Create(CultureInfo.InvariantCulture, $"{i:X8}{k:X2}CD"),
                    DeviceId = device.Id,
                    DeviceSerial = device.Serial,
                    Purpose = k == 0 ? CertificatePurpose.Https : CertificatePurpose.Dot1x,
                    IssuedUtc = now.AddSeconds(-i),
                    NotAfterUtc = now.AddDays(i % 100 == 0 ? 10 : 300),
                };
                rows.Add(new(entry.SerialNumber, PkiJson.Serialize(entry)));
            }
        }

        foreach (var chunk in rows.Chunk(10_000))
        {
            await records.SetManyAsync(IssuedRegistry.IssuedCollection, chunk, CancellationToken.None);
        }

        await using var pki = await PkiHarness.StartAsync(records: records, devices: devices);
        for (var i = 0; i < 50; i++)
        {
            Assert.True((await CertificateListTests.CreateAsync(pki, name: $"nvr{i}.example.com", addresses: $"nvr{i}.example.com")).Ok);
        }

        async Task<(CertificatesReply Reply, long Ms)> TimedAsync(CertificatesRequest request)
        {
            var watch = Stopwatch.StartNew();
            var reply = await pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, request);
            return (reply, watch.ElapsedMilliseconds);
        }

        await TimedAsync(new CertificatesRequest()); // warm up
        var first = await TimedAsync(new CertificatesRequest());
        var deep = await TimedAsync(new CertificatesRequest { Offset = 59_500 });
        var search = await TimedAsync(new CertificatesRequest { Search = "10.0.117.3" });
        var searchUse = await TimedAsync(new CertificatesRequest { Search = "IEEE 802.1X" });
        var soon = await TimedAsync(new CertificatesRequest { State = IssuedCertificateState.ExpiresSoon });
        var issuer = await TimedAsync(new CertificatesRequest { IssuedBy = PkiService.OtherIssuerId });
        output.WriteLine($"first page {first.Ms} ms, offset 59,500 {deep.Ms} ms, search address {search.Ms} ms, search use {searchUse.Ms} ms, state filter {soon.Ms} ms, issuer filter {issuer.Ms} ms");

        Assert.True(first.Reply.Total > Devices * 2 + 50);
        Assert.Equal(500, first.Reply.Rows.Count);
        Assert.Equal(59_500, deep.Reply.Offset);
        Assert.Contains(search.Reply.Rows, r => r.Device == "10.0.117.3");
        Assert.Equal(Devices, searchUse.Reply.Total);
        Assert.Equal(Devices / 100 * 2, soon.Reply.Total);
        Assert.Equal(Devices * 2, issuer.Reply.Total); // the test rows name no CA PKI lists
        foreach (var (name, ms) in new[] { ("first", first.Ms), ("deep", deep.Ms), ("search", search.Ms), ("use", searchUse.Ms), ("state", soon.Ms), ("issuer", issuer.Ms) })
        {
            Assert.True(ms < 1000, $"{name}: {ms} ms (budget 1,000 ms)");
        }
    }
}
