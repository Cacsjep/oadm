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
    public async Task The_policy_table_and_its_search_with_150_policies_at_30000_devices_stay_fast()
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
                Vlan = 1000 + i, // summaries "VLAN 1014": the search below matches names only
                Devices = new PolicyDevices { Tags = ["Tag " + i.ToString(CultureInfo.InvariantCulture)] },
            })).Error);
        }

        // The server's list: the rows with their device texts and warnings at 30,000 devices.
        var watch = Stopwatch.StartNew();
        var list = await pki.InvokeAsync<PolicyList>(PolicyMethods.GetPolicies);
        var read = watch.Elapsed;

        // The page: one table from one list (one reset), then a search.
        var page = new Client.PoliciesViewModel(new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = devices });
        watch.Restart();
        page.Apply(list);
        var apply = watch.Elapsed;
        Assert.Equal(Rules, page.Rows.Count(r => r.Source.Kind == PolicyKinds.Vlan));
        watch.Restart();
        page.SearchText = "VLAN 14";
        var search = watch.Elapsed;
        Assert.Equal(11, page.Rows.Count); // VLAN 14 and VLAN 140..149

        output.WriteLine($"30,000 devices, {Rules} VLAN policies: the list {read.TotalMilliseconds:F0} ms; the table {apply.TotalMilliseconds:F0} ms; the search {search.TotalMilliseconds:F0} ms");
        Assert.True(read < TimeSpan.FromSeconds(3), $"the list took {read.TotalMilliseconds:F0} ms");
        Assert.True(apply < TimeSpan.FromMilliseconds(500), $"the table took {apply.TotalMilliseconds:F0} ms");
        Assert.True(search < TimeSpan.FromMilliseconds(200), $"the search took {search.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Opening_policies_and_typing_in_their_form_at_30000_devices_build_no_device_picker()
    {
        var devices = Enumerable.Range(0, Devices).Select(i => (IDeviceInfo)new FakeDevice
        {
            Serial = "ACCC8E" + i.ToString("X6", CultureInfo.InvariantCulture),
            Address = "10.2." + (i / 250).ToString(CultureInfo.InvariantCulture) + "." + (i % 250).ToString(CultureInfo.InvariantCulture),
        }).ToList();
        await using var pki = await PkiHarness.StartAsync(devices: devices);
        var page = new Client.PoliciesViewModel(new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = devices });
        await page.LoadAsync(CancellationToken.None);

        // Every row clicked, three rounds: a form each time, no picker over the 30,000 devices (Choose devices makes one).
        var watch = Stopwatch.StartNew();
        for (var round = 0; round < 3; round++)
        {
            foreach (var row in page.Rows.ToList())
            {
                page.SelectedRow = row;
                Assert.False(page.Editor!.HasPicker);
            }
        }

        var opens = watch.Elapsed / (3 * page.Rows.Count);

        // 500 keystrokes in the name of a new VLAN policy: one comparison with the stored form per change.
        var editor = page.NewEditor(PolicyKinds.Vlan);
        watch.Restart();
        for (var i = 0; i < 500; i++)
        {
            editor.Name = "Building " + i.ToString(CultureInfo.InvariantCulture);
        }

        var keystroke = watch.Elapsed / 500;
        Assert.False(editor.HasPicker);
        Assert.NotNull(editor.NewPicker()); // the dialog's picker, only now

        output.WriteLine($"30,000 devices: open a policy {opens.TotalMilliseconds:F2} ms; a keystroke {keystroke.TotalMilliseconds:F3} ms");
        Assert.True(opens < TimeSpan.FromMilliseconds(25), $"opening a policy took {opens.TotalMilliseconds:F1} ms");
        Assert.True(keystroke < TimeSpan.FromMilliseconds(2), $"a keystroke took {keystroke.TotalMilliseconds:F2} ms");
    }
}
