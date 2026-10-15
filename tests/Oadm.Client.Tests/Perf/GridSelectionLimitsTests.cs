using System.ComponentModel;
using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Devices;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Perf;

[Trait("Category", "Perf")]
/// <summary>The device grid with large selections at 30,000 devices (opt-in, OADM_LIMITS_DEVICES; writes its times).</summary>
public sealed class GridSelectionLimitsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WhereTheGridSortTimeGoes()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OADM_LIMITS_DEVICES")))
        {
            return;
        }

        var lines = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var log = new List<string>();
            using var f = new DevicesFixture();
            f.SeedDevices([.. ClientScale.MakeDevices(30_000)]);
            var vm = f.Devices;
            var view = new DevicesView { DataContext = vm };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            var model = grid.Columns.First(c => c.Header as string == "Model");
            var firmware = grid.Columns.First(c => c.Header as string == "Firmware");

            async Task TimeAsync(string what, Action action)
            {
                var watch = Stopwatch.StartNew();
                action();
                var call = watch.ElapsedMilliseconds;
                await Task.Delay(100);
                while (view.IsGridBusy)
                {
                    await Task.Delay(20);
                }

                Dispatcher.UIThread.RunJobs();
                log.Add($"{what}: call {call} ms, with jobs {watch.ElapsedMilliseconds} ms");
            }

            await TimeAsync("Sort by Model, nothing selected", () => model.Sort(ListSortDirection.Descending));
            await TimeAsync("Sort by Firmware, nothing selected", () => firmware.Sort(ListSortDirection.Ascending));
            await TimeAsync("Select 10 rows", () => vm.SelectedDevices.ReplaceAll(vm.FilteredDevices.Take(10)));
            await TimeAsync("Sort by Model, 10 selected", () => model.Sort(ListSortDirection.Ascending));
            await TimeAsync("Select all", grid.SelectAll);
            await TimeAsync("Sort by Firmware, all selected", () => firmware.Sort(ListSortDirection.Descending));
            await TimeAsync("Select none", () => grid.SelectedItems.Clear());
            f.Store.Tags.Reset(Enumerable.Range(0, 10).Select(i => new Oadm.Contracts.V1.DeviceTag { Name = $"Building {(char)('A' + i)}", Color = (Oadm.Contracts.V1.TagColor)(i % 8 + 1), Defined = true }));
            var tagged = ClientScale.MakeDevices(30_000);
            for (int i = 0; i < tagged.Count; i++)
            {
                tagged[i].Tags.AddRange(Enumerable.Range(0, i % 4).Select(k => $"Building {(char)('A' + (i + k) % 10)}").Order(StringComparer.Ordinal));
            }

            await TimeAsync("Tags arrive for every device (not grouped)", () => f.Store.ApplyBatch([.. tagged.Select(d => new Oadm.Contracts.V1.DeviceChanged { Kind = Oadm.Contracts.V1.DeviceChanged.Types.Kind.Updated, Device = d })]));
            await TimeAsync("Group by tag on", () => vm.GroupByTag = true);
            var changes = ClientScale.MakeDevices(1000, version: 3);
            for (int i = 0; i < changes.Count; i++)
            {
                changes[i].Tags.Add("Building J");
            }

            var vmWatch = Stopwatch.StartNew();
            f.Store.ApplyBatch([.. changes.Select(d => new Oadm.Contracts.V1.DeviceChanged { Kind = Oadm.Contracts.V1.DeviceChanged.Types.Kind.Updated, Device = d })]);
            log.Add($"1,000 tag changes grouped, view model only: {vmWatch.ElapsedMilliseconds} ms");
            await TimeAsync("... then the grid", () => { });
            await TimeAsync("Select all grouped", grid.SelectAll);
            var more = ClientScale.MakeDevices(1000, version: 4);
            for (int i = 0; i < more.Count; i++)
            {
                more[i].Tags.Add("Building I");
            }

            await TimeAsync("1,000 tag changes grouped, all selected", () => f.Store.ApplyBatch([.. more.Select(d => new Oadm.Contracts.V1.DeviceChanged { Kind = Oadm.Contracts.V1.DeviceChanged.Types.Kind.Updated, Device = d })]));
            log.Add($"Selected after the change: grid {grid.SelectedItems.Count}, view model {vm.SelectedDevices.Count}");
            await TimeAsync("Select none grouped", () => grid.SelectedItems.Clear());
            window.Close();
            return log;
        }, CancellationToken.None);
        foreach (var line in lines)
        {
            output.WriteLine(line);
        }
    }
}
