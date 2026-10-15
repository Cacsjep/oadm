using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Devices;
using Oadm.Client.Tests.Perf;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// The device grid with a large selection (docs/30kspec.md): a sort shows the ring first, and rebuilt tag groups keep the
/// selection without the grid regrouping it row by row. The ring's paint delay is zero here, so the work runs at once and
/// the tests stay synchronous (after an await a headless test no longer has its application).
/// </summary>
[Collection("DevicesView busy")]
public sealed class DeviceGridBusyTests : IDisposable
{
    private static readonly string[] TagNames = ["Building A", "Building B", "Building C"];

    public DeviceGridBusyTests() => DevicesView.BusyPaintDelay = TimeSpan.Zero;

    public void Dispose() => DevicesView.BusyPaintDelay = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task A_sort_with_a_large_selection_shows_the_ring_and_keeps_the_selection()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.SeedDevices([.. ClientScale.MakeDevices(DevicesView.LargeSelection + 200)]);
            var view = new DevicesView { DataContext = f.Devices };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            grid.SelectAll();
            Dispatcher.UIThread.RunJobs();

            grid.Columns.First(c => c.Header as string == "Model").Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, view.BusyRuns);
            Assert.False(view.IsGridBusy);
            Assert.False(window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.LoadingOverlay>().Single().IsVisible);
            Assert.Equal(f.Devices.FilteredDevices.Count, f.Devices.SelectedDevices.Count);
            Assert.Equal("Row.Model", Assert.Single(grid.CollectionView!.SortDescriptions).PropertyPath);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_small_selection_sorts_at_once_without_the_ring()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.SeedDevices([.. ClientScale.MakeDevices(50)]);
            var view = new DevicesView { DataContext = f.Devices };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            grid.SelectAll();

            grid.Columns.First(c => c.Header as string == "Model").Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, view.BusyRuns);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Rebuilt_tag_groups_keep_a_large_selection()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.Store.Tags.Reset(TagNames.Select((n, i) => new DeviceTag { Name = n, Color = (TagColor)(i + 1), Defined = true }));
            f.SeedDevices([.. Tagged(DevicesView.LargeSelection + 200, version: 0)]);
            var vm = f.Devices;
            var view = new DevicesView { DataContext = vm };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            vm.GroupByTag = true;
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            grid.SelectAll();
            Dispatcher.UIThread.RunJobs();
            var selected = vm.SelectedDevices.Count;

            f.Store.ApplyBatch([.. Tagged(300, version: 1).Select(d => new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = d })]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, view.BusyRuns);
            Assert.Equal(selected, vm.SelectedDevices.Count);
            Assert.Equal(vm.TagGrouping.Rows.Count, grid.SelectedItems.Count);
            Assert.IsType<Avalonia.Collections.DataGridCollectionView>(grid.ItemsSource);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private static List<Device> Tagged(int count, int version)
    {
        var devices = ClientScale.MakeDevices(count, version);
        for (var i = 0; i < devices.Count; i++)
        {
            devices[i].Tags.Add(TagNames[(i + version) % TagNames.Length]);
        }

        return devices;
    }
}
