using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The overlap check and the warnings of the policies stay fast at 30,000 devices (CLAUDE.md "scale to thousands of devices";
/// docs/spec/pki.md "Policies"): a save checks its kind against every device, the card's rows look for later overlaps, the
/// expiry days of every device.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PolicyPerfTests(ITestOutputHelper output)
{
    private const int Devices = 30_000;
    private const int Policies = 60;

    [Fact]
    public async Task Saves_rows_and_expiry_days_at_30000_devices_stay_fast()
    {
        var sites = new FakeSites();
        var siteIds = Enumerable.Range(0, 10).Select(i => sites.Add("Site " + i.ToString(CultureInfo.InvariantCulture)).Id).ToList();
        var devices = Enumerable.Range(0, Devices).Select(i => (IDeviceInfo)new FakeDevice
        {
            Serial = "ACCC8E" + i.ToString("X6", CultureInfo.InvariantCulture),
            Address = "10." + (i / 62500).ToString(CultureInfo.InvariantCulture) + "." + (i / 250 % 250).ToString(CultureInfo.InvariantCulture) + "." + (i % 250).ToString(CultureInfo.InvariantCulture),
            Tags = ["Tag " + (i % Policies).ToString(CultureInfo.InvariantCulture), i % 2 == 0 ? "Outdoor" : "Indoor"],
            SiteId = i % 3 == 0 ? siteIds[i % siteIds.Count] : null,
        }).ToList();
        using var store = new PolicyStore(new FakeSettings(), _ => Task.FromResult<IReadOnlyList<IDeviceInfo>>(devices), () => sites.List,
            () => [new CertificateAuthorityInfo(CertificateAuthorityInfo.OadmId, "OADM CA", CertificateAuthorityKind.Oadm, [], DateTime.UtcNow.AddYears(1), true)],
            new FakeProtectedTags());

        // One VLAN policy per tag (no overlaps) and expiry warnings per site: every save checks its kind against 30,000 devices.
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Policies; i++)
        {
            var saved = await store.SaveAsync(new PkiPolicy
            {
                Name = "VLAN " + i.ToString(CultureInfo.InvariantCulture),
                Kind = PolicyKinds.Vlan,
                Vlan = 100 + i,
                Devices = new PolicyDevices { Tags = ["Tag " + i.ToString(CultureInfo.InvariantCulture)] },
            }, "admin", PkiService.CipherSuites, CancellationToken.None);
            Assert.Null(saved.Error);
        }

        foreach (var site in siteIds)
        {
            Assert.Null((await store.SaveAsync(new PkiPolicy { Name = site.ToString(), Kind = PolicyKinds.ExpiryWarning, Days = 45, Devices = new PolicyDevices { SiteIds = [site] } },
                "admin", PkiService.CipherSuites, CancellationToken.None)).Error);
        }

        var saves = watch.Elapsed;
        var perSave = saves / (Policies + siteIds.Count);

        // The last one overlaps with every VLAN policy: refused with the first device.
        watch.Restart();
        var refused = await store.SaveAsync(new PkiPolicy { Name = "Everything", Kind = PolicyKinds.Vlan, Vlan = 999, Devices = new PolicyDevices { ThisServer = true, SiteIds = siteIds } },
            "admin", PkiService.CipherSuites, CancellationToken.None);
        var overlap = watch.Elapsed;
        Assert.Equal(PolicyFields.Devices, refused.Error?.Field);

        // The card: summaries and the warnings of later overlaps (a tag that moved: here the Outdoor devices in a second VLAN policy).
        devices = [.. devices.Select((d, i) => i < 100 ? ((FakeDevice)d) with { Tags = [.. d.Tags, "Tag 1"] } : d)];
        watch.Restart();
        var rows = store.Rows(devices);
        var card = watch.Elapsed;
        Assert.Equal(Policies + siteIds.Count, rows.Count);
        Assert.Contains(rows, r => r.Warning is not null);

        watch.Restart();
        var soon = devices.Count(d => store.ExpiryDaysFor(d) == 45);
        var expiry = watch.Elapsed;
        Assert.Equal(Devices / 3, soon);

        output.WriteLine($"30,000 devices: {Policies + siteIds.Count} saves {saves.TotalMilliseconds:F0} ms ({perSave.TotalMilliseconds:F1} ms each); refused overlap {overlap.TotalMilliseconds:F0} ms; card rows {card.TotalMilliseconds:F0} ms; expiry days of every device {expiry.TotalMilliseconds:F0} ms");
        Assert.True(perSave < TimeSpan.FromMilliseconds(500), $"a save took {perSave.TotalMilliseconds:F0} ms");
        Assert.True(overlap < TimeSpan.FromSeconds(3), $"the overlap check took {overlap.TotalMilliseconds:F0} ms");
        Assert.True(card < TimeSpan.FromSeconds(3), $"the rows took {card.TotalMilliseconds:F0} ms");
        Assert.True(expiry < TimeSpan.FromSeconds(1), $"the expiry days took {expiry.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Check_a_device_and_the_cards_with_150_rules_at_30000_devices_stay_fast()
    {
        const int Rules = 150;
        var devices = Enumerable.Range(0, Devices).Select(i => (IDeviceInfo)new FakeDevice
        {
            Serial = "ACCC8E" + i.ToString("X6", CultureInfo.InvariantCulture),
            Address = "10.1." + (i / 250).ToString(CultureInfo.InvariantCulture) + "." + (i % 250).ToString(CultureInfo.InvariantCulture),
            Tags = ["Tag " + (i % Rules).ToString(CultureInfo.InvariantCulture)],
        }).ToList();
        await using var pki = await PkiHarness.StartAsync(devices: devices);
        for (var i = 0; i < Rules; i++)
        {
            Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy
            {
                Name = "VLAN " + i.ToString(CultureInfo.InvariantCulture),
                Kind = PolicyKinds.Vlan,
                Vlan = 100 + i,
                Devices = new PolicyDevices { Tags = ["Tag " + i.ToString(CultureInfo.InvariantCulture)] },
            })).Error);
        }

        // The server checks one device: one pass over the devices to find it, then O(policies).
        var watch = Stopwatch.StartNew();
        var check = await pki.InvokeAsync<PolicyCheck>(PolicyMethods.CheckDevice, new PolicyCheckRequest(devices[^1].Id));
        var checkTime = watch.Elapsed;
        Assert.Contains(check.Lines, l => l.Text == "It gets VLAN " + (100 + (Devices - 1) % Rules).ToString(CultureInfo.InvariantCulture) + ".");

        // The page: the rule lists and the settings in place from one list (one reset per list).
        var list = await pki.InvokeAsync<PolicyList>(PolicyMethods.GetPolicies);
        var card = new Client.PoliciesViewModel(new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = devices });
        watch.Restart();
        card.Apply(list);
        var apply = watch.Elapsed;
        Assert.Equal(Rules, card.Vlans.Rows.Count);

        output.WriteLine($"30,000 devices, {Rules} VLAN rules: Check a device {checkTime.TotalMilliseconds:F0} ms; the cards {apply.TotalMilliseconds:F0} ms");
        Assert.True(checkTime < TimeSpan.FromMilliseconds(500), $"Check a device took {checkTime.TotalMilliseconds:F0} ms");
        Assert.True(apply < TimeSpan.FromMilliseconds(500), $"the cards took {apply.TotalMilliseconds:F0} ms");
    }
}
