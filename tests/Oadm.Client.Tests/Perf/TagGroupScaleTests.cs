using System.ComponentModel;
using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Perf;

/// <summary>Group mode with 5,000 devices and up to three tags each (8,750 device x tag rows).</summary>
[Trait("Category", "Perf")]
public sealed class TagGroupScaleTests(ITestOutputHelper output)
{
    private static readonly string[] TagNames = [.. Enumerable.Range(0, 10).Select(i => $"Building {(char)('A' + i)}")];

    /// <summary>Device i has i % 4 tags (0..3), so a quarter is in "No tag".</summary>
    private static List<Device> TaggedDevices(int version = 0)
    {
        List<Device> devices = ClientScale.MakeDevices(version: version);
        for (int i = 0; i < devices.Count; i++)
        {
            devices[i].Tags.AddRange(Enumerable.Range(0, i % 4).Select(k => TagNames[(i + k + version) % TagNames.Length]).Order(StringComparer.Ordinal));
        }

        return devices;
    }

    private static DevicesFixture Seeded()
    {
        var fixture = new DevicesFixture();
        fixture.Store.Tags.Reset(TagNames.Select((n, i) => new DeviceTag { Name = n, Color = (TagColor)(i % 8 + 1), Defined = true }));
        fixture.SeedDevices([.. TaggedDevices()]);
        return fixture;
    }

    [Fact]
    public void Group_mode_builds_filters_selects_and_follows_bursts_for_5000_devices()
    {
        using DevicesFixture f = Seeded();
        DevicesViewModel vm = f.Devices;
        int expectedRows = vm.FilteredDevices.Sum(d => Math.Max(1, d.Tags.Count));

        ClientScale.Measure(output, "Group mode on: 5,000 devices x up to 3 tags", TimeSpan.FromSeconds(1), () => vm.GroupByTag = true);
        Assert.Equal(expectedRows, vm.TagGrouping.Rows.Count);
        Assert.Equal(11, vm.TagGrouping.Groups.Count);
        output.WriteLine($"Rows: {vm.TagGrouping.Rows.Count}, groups: {vm.TagGrouping.Groups.Count}");

        ClientScale.Measure(output, "Search '10.0.1' and clear it in group mode", TimeSpan.FromSeconds(2), () =>
        {
            vm.SearchText = "10.0.1";
            vm.SearchText = "";
        });
        Assert.Equal(expectedRows, vm.TagGrouping.Rows.Count);

        ClientScale.Measure(output, "Select all rows (distinct devices)", TimeSpan.FromSeconds(1), () => vm.SelectedGridItems.ReplaceAll(vm.TagGrouping.Rows));
        Assert.Equal(ClientScale.Devices, vm.SelectedDevices.Count);
        vm.SelectedGridItems.Clear();

        ClientScale.Measure(output, "Burst of 1,000 device updates that change tags", TimeSpan.FromSeconds(2), () =>
            f.Store.ApplyBatch([.. TaggedDevices(version: 1).Take(1000).Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d })]));
        Assert.Equal(vm.FilteredDevices.Sum(d => Math.Max(1, d.Tags.Count)), vm.TagGrouping.Rows.Count);

        int resets = 0;
        vm.TagGrouping.Rows.CollectionChanged += (_, _) => resets++;
        ClientScale.Measure(output, "Burst of 1,000 status updates (no regrouping)", TimeSpan.FromSeconds(1), () =>
        {
            List<Device> devices = TaggedDevices(version: 1).Take(1000).ToList();
            devices.ForEach(d => d.Status = DeviceStatus.Unreachable);
            f.Store.ApplyBatch([.. devices.Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d })]);
        });
        Assert.Equal(0, resets);
    }

    [Fact]
    public async Task Group_mode_grid_with_5000_devices_renders_selects_all_and_sorts()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        List<string> lines = await session.Dispatch(() =>
        {
            var log = new List<string>();
            using DevicesFixture f = Seeded();
            DevicesViewModel vm = f.Devices;
            var window = new Window { Width = 1440, Height = 900, Content = new DevicesView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");

            Measure(log, "Group mode on and render (8,750 rows, 11 groups)", TimeSpan.FromSeconds(10), () =>
            {
                vm.GroupByTag = true;
                Dispatcher.UIThread.RunJobs();
            });
            int realized = grid.GetVisualDescendants().OfType<DataGridRow>().Count();
            log.Add($"Realized rows: {realized} of {vm.TagGrouping.Rows.Count} (virtualized)");
            Assert.InRange(realized, 1, 200);
            Assert.NotEmpty(grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>());

            Measure(log, "Select all in group mode", TimeSpan.FromSeconds(5), () =>
            {
                grid.SelectAll();
                Dispatcher.UIThread.RunJobs();
            });
            Assert.Equal(ClientScale.Devices, vm.SelectedDevices.Count);

            Measure(log, "Sort by Model within the groups", TimeSpan.FromSeconds(5), () =>
            {
                grid.Columns.First(c => c.Header as string == "Model").Sort(ListSortDirection.Descending);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            });

            Measure(log, "1,000 tag changes while grouped and sorted", TimeSpan.FromSeconds(10), () =>
            {
                f.Store.ApplyBatch([.. TaggedDevices(version: 2).Take(1000).Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d })]);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            });

            window.Close();
            return Task.FromResult(log);
        }, CancellationToken.None);

        foreach (string line in lines)
        {
            output.WriteLine(line);
        }
    }

    private static void Measure(List<string> log, string what, TimeSpan budget, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        log.Add($"{what}: {watch.Elapsed.TotalMilliseconds:F0} ms (budget {budget.TotalMilliseconds:F0} ms)");
        Assert.True(watch.Elapsed < budget, $"{what} took {watch.Elapsed.TotalMilliseconds:F0} ms, budget {budget.TotalMilliseconds:F0} ms");
    }
}
