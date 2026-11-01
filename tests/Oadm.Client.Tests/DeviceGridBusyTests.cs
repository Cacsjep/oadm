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
/// The device grid with a large selection: a sort shows the ring first, and rebuilt tag groups keep the
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
            Assert.False(f.Devices.IsLoading); // the first device list arrived: no "Loading devices"
            Assert.False(window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.LoadingOverlay>().Single(o => o.Name == "GridBusy").IsVisible);
            Assert.Equal(f.Devices.FilteredDevices.Count, f.Devices.SelectedDevices.Count);
            Assert.Equal("Row.Model", Assert.Single(grid.CollectionView!.SortDescriptions).PropertyPath);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1_100)] // fewer than half selected
    [InlineData(2_500)] // more than half
    public async Task A_sort_keeps_exactly_a_large_partial_selection(int selected)
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.SeedDevices([.. ClientScale.MakeDevices(3_000)]);
            var view = new DevicesView { DataContext = f.Devices };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            var chosen = f.Devices.FilteredDevices.Where((_, i) => i % 7 != 1).Take(selected).ToList();
            foreach (var row in chosen)
            {
                grid.SelectedItems.Add(row);
            }

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(selected, f.Devices.SelectedDevices.Count);

            grid.Columns.First(c => c.Header as string == "Model").Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, view.BusyRuns);
            var expected = chosen.Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expected, f.Devices.SelectedDevices.Select(r => r.Id).Order(StringComparer.Ordinal));
            Assert.Equal(expected, grid.SelectedItems.Cast<DeviceRowViewModel>().Select(r => r.Id).Order(StringComparer.Ordinal));
            Assert.Equal("Row.Model", Assert.Single(grid.CollectionView!.SortDescriptions).PropertyPath);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_search_keeps_exactly_the_shown_rows_of_a_large_selection(bool sorted)
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.SeedDevices([.. ClientScale.MakeDevices(6_000)]);
            var vm = f.Devices;
            var view = new DevicesView { DataContext = vm };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            if (sorted)
            {
                grid.Columns.First(c => c.Header as string == "Model").Sort(ListSortDirection.Descending);
                Dispatcher.UIThread.RunJobs();
            }

            var chosen = vm.FilteredDevices.Where((_, i) => i % 3 != 1).ToList();
            var chosenSet = chosen.ToHashSet<object>();
            Assert.True(Oadm.Client.Controls.GridRangeSelect.TrySelect(grid, chosenSet.Contains)); // row by row would take seconds

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(chosen.Count, grid.SelectedItems.Count);
            var shown = chosen.Where(r => r.Matches("10.0.1")).Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
            Assert.True(shown.Count >= DevicesView.LargeSelection, "the search keeps a large selection");
            int changes = 0;
            grid.SelectionChanged += (_, _) => changes++;

            vm.SearchText = "10.0.1";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(shown, vm.SelectedDevices.Select(r => r.Id).Order(StringComparer.Ordinal));
            Assert.Equal(shown, grid.SelectedItems.Cast<DeviceRowViewModel>().Select(r => r.Id).Order(StringComparer.Ordinal));

            vm.SearchText = "";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(f.Store.Devices.Count, vm.FilteredDevices.Count);
            Assert.Equal(shown, vm.SelectedDevices.Select(r => r.Id).Order(StringComparer.Ordinal));
            Assert.Equal(shown, grid.SelectedItems.Cast<DeviceRowViewModel>().Select(r => r.Id).Order(StringComparer.Ordinal));
            Assert.InRange(changes, 1, 8); // a few per search, not one per row
            Assert.Equal(0, view.BusyRuns);
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

    [Fact]
    public async Task Group_by_tag_turned_on_keeps_the_selected_devices()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.Store.Tags.Reset(TagNames.Select((n, i) => new DeviceTag { Name = n, Color = (TagColor)(i + 1), Defined = true }));
            var devices = Tagged(DevicesView.LargeSelection * 2, version: 0);
            foreach (var d in devices.Where((_, i) => i % 2 == 0))
            {
                d.Tags.Add("Second tag"); // two tag rows for every other device
            }

            f.SeedDevices([.. devices]);
            var vm = f.Devices;
            var view = new DevicesView { DataContext = vm };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            var chosen = vm.FilteredDevices.Take(DevicesView.LargeSelection + 100).ToList();
            foreach (var row in chosen)
            {
                grid.SelectedItems.Add(row);
            }

            Dispatcher.UIThread.RunJobs();

            vm.GroupByTag = true;
            Dispatcher.UIThread.RunJobs();

            var expected = chosen.Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expected, vm.SelectedDevices.Select(r => r.Id).Order(StringComparer.Ordinal));
            Assert.Equal(vm.TagGrouping.Rows.OfType<Oadm.Client.Tags.DeviceTagRow>().Count(r => chosen.Contains(r.Row)), grid.SelectedItems.OfType<Oadm.Client.Tags.DeviceTagRow>().Count()); // every tag row of them
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Group_mode_sorts_within_each_group_and_a_header_click_selects_nothing()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.Store.Tags.Reset(TagNames.Select((n, i) => new DeviceTag { Name = n, Color = (TagColor)(i + 1), Defined = true }));
            f.SeedDevices([.. Tagged(300, version: 0)]);
            var vm = f.Devices;
            vm.GroupByTag = true;
            var view = new DevicesView { DataContext = vm };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");

            grid.Columns.First(c => c.Header as string == "Address").Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();

            // Each header first, then its devices by address descending; the groups keep the tag name order.
            var rows = ((System.Collections.IEnumerable)grid.CollectionView!).Cast<Oadm.Client.Tags.ITagGridRow>().ToList();
            Assert.Equal(TagNames, rows.OfType<Oadm.Client.Tags.TagGroupKey>().Select(g => g.Name));
            Assert.Equal(TagNames.Length + 300, rows.Count);
            for (int i = 0, start = 0; i <= rows.Count; i++)
            {
                if (i == rows.Count || (i > start && rows[i] is Oadm.Client.Tags.TagGroupKey))
                {
                    var group = rows[start..i];
                    Assert.IsType<Oadm.Client.Tags.TagGroupKey>(group[0]);
                    var addresses = group.Skip(1).Cast<Oadm.Client.Tags.DeviceTagRow>().Select(r => r.Row.DisplayAddress).ToList();
                    Assert.All(group.Skip(1), r => Assert.Same(group[0], r.Group));
                    Assert.Equal(addresses.OrderDescending(StringComparer.CurrentCulture), addresses);
                    start = i;
                }
            }

            // A click on a header row selects nothing.
            var header = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.Classes.Contains(Oadm.Client.Tags.TagGroupRows.HeaderClass));
            grid.SelectedItems.Clear();
            var point = Avalonia.VisualExtensions.TranslatePoint(header, new Avalonia.Point(400, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(grid.SelectedItems);
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
