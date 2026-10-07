using System.ComponentModel;
using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Perf;

/// <summary>
/// The real Devices page (DataGrid, GridSelection, context menu, toolbar) rendered headless with 5,000
/// devices: first render, select all, sort, search and a burst of updates while sorted. Set
/// OADM_SCREENSHOT_DIR for a PNG.
/// </summary>
[Trait("Category", "Perf")]
public sealed class DeviceGridScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Devices_grid_with_5000_devices_renders_selects_all_sorts_and_filters()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        List<string> lines = await session.Dispatch(async () =>
        {
            var log = new List<string>();
            using var fixture = new DevicesFixture();
            fixture.SeedDevices([.. ClientScale.MakeDevices()]);
            IEnumerable<string> runnable = fixture.Store.Devices.Where(d => d.ContractStatus == DeviceStatus.Ok).Select(d => d.Id);
            await fixture.SetPluginsAsync([.. Enumerable.Range(0, 10).Select(i => ClientScale.RunnableOnAll("perf.plugin" + i, runnable))]);
            DevicesViewModel vm = fixture.Devices;

            var window = new Window { Width = 1440, Height = 900, Content = new DevicesView { DataContext = vm } };
            Measure(log, "First render of the grid with 5,000 rows", TimeSpan.FromSeconds(10), () =>
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
            });
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            int realizedRows = grid.GetVisualDescendants().OfType<DataGridRow>().Count();
            log.Add($"Realized rows: {realizedRows} of {vm.FilteredDevices.Count} (virtualized)");
            Assert.InRange(realizedRows, 1, 200);

            Measure(log, "Select all in the grid (GridSelection -> view model, context menu)", TimeSpan.FromSeconds(5), () =>
            {
                grid.SelectAll();
                Dispatcher.UIThread.RunJobs();
            });
            Assert.Equal(ClientScale.Devices, vm.SelectedDevices.Count);

            DataGridColumn modelColumn = grid.Columns.First(c => c.Header as string == "Model");
            Measure(log, "Sort 5,000 rows by Model", TimeSpan.FromSeconds(5), () =>
            {
                modelColumn.Sort(ListSortDirection.Descending);
                Dispatcher.UIThread.RunJobs();
            });

            Measure(log, "1,000 device updates while sorted", TimeSpan.FromSeconds(5), () =>
            {
                List<Device> newer = ClientScale.MakeDevices(version: 2);
                fixture.Store.ApplyBatch([.. newer.Take(1000).Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d })]);
                Dispatcher.UIThread.RunJobs();
            });

            Measure(log, "Search '10.0.1' and clear it", TimeSpan.FromSeconds(5), () =>
            {
                vm.SearchText = "10.0.1";
                Dispatcher.UIThread.RunJobs();
                vm.SearchText = "";
                Dispatcher.UIThread.RunJobs();
            });
            Assert.Equal(ClientScale.Devices, vm.FilteredDevices.Count);

            if (!string.IsNullOrEmpty(outDir))
            {
                WriteableBitmap? frame = window.CaptureRenderedFrame();
                Directory.CreateDirectory(outDir);
                frame?.Save(Path.Combine(outDir, "devices-5000-headless.png"));
            }

            window.Close();
            return log;
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
