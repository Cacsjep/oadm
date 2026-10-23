using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Perf;

/// <summary>The device group editor (the shared device picker in its group mode) with 30,000 devices: open, search, choose, count.</summary>
[Trait("Category", "Perf")]
public sealed class DeviceGroupEditorScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void The_editor_stays_fast_with_30000_devices()
    {
        var sites = Enumerable.Range(0, 100).Select(i => new DevicePickerSite(Guid.NewGuid(), $"Site {i:D3}")).ToList();
        string[] tags = [.. Enumerable.Range(0, 500).Select(i => $"Tag {i:D3}")];
        var devices = new List<FakeDevice>(ClientScale.Devices);
        for (int i = 0; i < ClientScale.Devices; i++)
        {
            DevicePickerSite? site = i % 4 == 0 ? null : sites[i % sites.Count];
            devices.Add(new FakeDevice
            {
                Id = Guid.NewGuid(),
                Serial = $"ACCC8E{i:X6}",
                Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                SiteId = site?.Id,
                SiteName = site?.Name,
                Tags = [tags[i % tags.Length], tags[(i * 7) % tags.Length]],
            });
        }

        var options = new DeviceGroupPickerOptions
        {
            Sites = [new DevicePickerSite(null, "This server"), .. sites],
            Tags = [.. tags.Select((t, i) => new DevicePickerTag($"tag-{i}", t))],
        };
        var initial = new DeviceGroupChoice(false, [sites[1].Id!.Value], ["tag-3"], [.. devices.Take(5_000).Select(d => d.Serial)], [.. devices.Skip(29_000).Select(d => d.Serial)]);
        DevicePickerViewModel picker = null!;
        TimeSpan budget = TimeSpan.FromSeconds(2);
        ClientScale.Measure(output, "Editor opens with a group (5,000 single devices, 1,000 excepted)", budget, () =>
            picker = new DevicePickerViewModel(devices, initial, options));
        ClientScale.Measure(output, "Search (one keystroke)", TimeSpan.FromMilliseconds(500), () => picker.SearchText = "Site 042");
        Assert.Equal(300, picker.Visible.Count);
        ClientScale.Measure(output, "Select all of the search", TimeSpan.FromMilliseconds(500), () => picker.SelectAllCommand.Execute(null));
        ClientScale.Measure(output, "Search cleared", TimeSpan.FromMilliseconds(500), () => picker.SearchText = "");
        ClientScale.Measure(output, "Choose a site", TimeSpan.FromMilliseconds(500), () => picker.Sites[0].IsChecked = true);
        ClientScale.Measure(output, "Choose a tag", TimeSpan.FromMilliseconds(500), () => picker.Tags[10].IsChecked = true);
        ClientScale.Measure(output, "Except a device", TimeSpan.FromMilliseconds(500), () => picker.Visible.First(i => i.CanExcept).ExceptCommand.Execute(null));
        output.WriteLine(picker.Summary);
        DeviceGroupChoice choice = null!;
        ClientScale.Measure(output, "The rules for Save", TimeSpan.FromMilliseconds(500), () => choice = picker.GroupChoice);
        Assert.True(choice.ThisServer);
        Assert.True(picker.CoveredCount > 7_500); // every 4th device has no site
        Assert.EndsWith("of 30,000 devices", picker.Summary, StringComparison.Ordinal);
    }
}
