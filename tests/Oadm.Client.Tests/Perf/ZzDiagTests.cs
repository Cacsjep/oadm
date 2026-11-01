using System.ComponentModel;
using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Devices;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Perf;

[Trait("Category", "Diag")]
public sealed class ZzDiagTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(30_000, true, true)]
    [InlineData(100_000, true, true)]
    [InlineData(100_000, false, true)]
    [InlineData(100_000, true, false)]
    public async Task Search(int count, bool sorted, bool selectAll)
    {
        var lines = await HeadlessSession.Shared.Dispatch(() =>
        {
            var log = new List<string>();
            using var f = new DevicesFixture();
            f.SeedDevices([.. ClientScale.MakeDevices(count)]);
            var vm = f.Devices;
            var view = new DevicesView { DataContext = vm };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            if (selectAll)
            {
                grid.SelectAll();
                Dispatcher.UIThread.RunJobs();
            }

            if (sorted)
            {
                var model = grid.Columns.First(c => c.Header as string == "Model");
                model.Sort(ListSortDirection.Descending);
                Dispatcher.UIThread.RunJobs();
            }

            void Time(string what, Action a)
            {
                var w = Stopwatch.StartNew();
                a();
                var call = w.ElapsedMilliseconds;
                Dispatcher.UIThread.RunJobs();
                log.Add($"{count} sorted={sorted} all={selectAll} {what}: call {call} ms, jobs {w.ElapsedMilliseconds} ms; grid sel {grid.SelectedItems.Count}, vm sel {vm.SelectedDevices.Count}, filtered {vm.FilteredDevices.Count}");
            }

            Time("search 10.0.1", () => vm.SearchText = "10.0.1");
            Time("clear", () => vm.SearchText = "");
            window.Close();
            return Task.FromResult(log);
        }, CancellationToken.None);
        foreach (var l in lines)
        {
            output.WriteLine(l);
        }
    }
}
