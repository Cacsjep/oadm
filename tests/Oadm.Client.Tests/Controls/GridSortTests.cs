using System.ComponentModel;
using System.Diagnostics;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Sdk.Client.Controls;

using Xunit.Abstractions;

namespace Oadm.Client.Tests.Controls;

/// <summary>
/// Every grid with a large selection: the selection comes off for a sort and goes back right after, the same rows, and a
/// selection handler that asks <see cref="GridSort.IsMovingSelection"/> sees no change.
/// </summary>
public sealed class GridSortTests
{
    [Theory]
    [InlineData(3_000, false)] // every row: back in one step
    [InlineData(1_100, false)] // fewer than half: row by row
    [InlineData(2_500, false)] // more than half: all, then the others off
    [InlineData(3_000, true)]
    [InlineData(1_100, true)]
    [InlineData(2_500, true)] // grouped: row by row (removing a row is slow there)
    public async Task A_sort_keeps_exactly_the_selection(int selected, bool grouped)
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var g = GridWindow.Show(3_000, grouped);
            var chosen = g.Rows.Where((_, i) => i % 7 != 1).Take(selected).ToHashSet();
            if (selected == g.Rows.Count)
            {
                g.Grid.SelectAll();
            }
            else
            {
                foreach (var row in chosen)
                {
                    g.Grid.SelectedItems.Add(row);
                }
            }

            Dispatcher.UIThread.RunJobs();
            int handed = 0;
            g.Grid.SelectionChanged += (_, _) =>
            {
                if (!GridSort.IsMovingSelection(g.Grid))
                {
                    handed++;
                }
            };

            g.Grid.Columns[0].Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Name", Assert.Single(g.Grid.CollectionView!.SortDescriptions).PropertyPath);
            if (!grouped)
            {
                Assert.Equal(g.Rows.Max(r => r.Name), ((TestRow)((DataGridCollectionView)g.Grid.CollectionView)[0]!).Name);
            }

            Assert.True(g.Grid.SelectedItems.Cast<TestRow>().ToHashSet().SetEquals(selected == g.Rows.Count ? g.Rows : chosen));
            Assert.False(GridSort.IsMovingSelection(g.Grid));
            Assert.Equal(0, handed);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_small_selection_stays_on_the_grid()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var g = GridWindow.Show(3_000, grouped: false);
            foreach (var row in g.Rows.Take(GridSort.LargeSelection - 1))
            {
                g.Grid.SelectedItems.Add(row);
            }

            Dispatcher.UIThread.RunJobs();
            bool moved = false;
            g.Grid.SelectionChanged += (_, _) => moved = true;

            g.Grid.Columns[0].Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();

            Assert.False(moved);
            Assert.Equal(GridSort.LargeSelection - 1, g.Grid.SelectedItems.Count);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_grid_that_handles_its_own_sort_keeps_its_selection_on()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            var grid = new DataGrid();
            grid.Sorting += (_, e) => e.Handled = true; // the device grid: its own sort, subscribed before the hook
            using var g = GridWindow.Show(3_000, grouped: false, grid);
            g.Grid.SelectAll();
            Dispatcher.UIThread.RunJobs();
            bool moved = false;
            g.Grid.SelectionChanged += (_, _) => moved = true;

            g.Grid.Columns[0].Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();

            Assert.False(moved);
            Assert.Empty(g.Grid.CollectionView!.SortDescriptions);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    internal sealed class TestRow(int id)
    {
        public int Id { get; } = id;

        public string Name { get; } = $"Camera {id * 7919 % 100_003:D6}";

        public string Group { get; } = $"Site {id % 5}";
    }

    internal sealed class GridWindow : IDisposable
    {
        private readonly Window _window;

        private GridWindow(Window window, DataGrid grid, List<TestRow> rows)
        {
            _window = window;
            Grid = grid;
            Rows = rows;
        }

        public DataGrid Grid { get; }

        public List<TestRow> Rows { get; }

        public static GridWindow Show(int count, bool grouped, DataGrid? grid = null)
        {
            GridSort.Install();
            var rows = Enumerable.Range(0, count).Select(i => new TestRow(i)).ToList();
            grid ??= new DataGrid();
            grid.IsReadOnly = true;
            grid.CanUserSortColumns = true;
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(TestRow.Name)) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Site", Binding = new Binding(nameof(TestRow.Group)) });
            var view = new DataGridCollectionView(rows);
            if (grouped)
            {
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(TestRow.Group)));
            }

            grid.ItemsSource = view;
            var window = new Window { Width = 1200, Height = 800, Content = grid };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return new GridWindow(window, grid, rows);
        }

        public void Dispose() => _window.Close();
    }
}

/// <summary>Sorting a grid of 30,000 selected rows (writes its times).</summary>
[Trait("Category", "Perf")]
public sealed class GridSortScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sort_with_30000_selected_rows_stays_fast(bool grouped)
    {
        var elapsed = await HeadlessSession.Shared.Dispatch(() =>
        {
            using var g = GridSortTests.GridWindow.Show(30_000, grouped);
            g.Grid.SelectAll();
            Dispatcher.UIThread.RunJobs();

            var watch = Stopwatch.StartNew();
            g.Grid.Columns[0].Sort(ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();
            watch.Stop();

            Assert.Equal(30_000, g.Grid.SelectedItems.Count);
            return Task.FromResult(watch.Elapsed);
        }, CancellationToken.None);

        output.WriteLine($"Sort of 30,000 selected rows ({(grouped ? "grouped" : "plain")}): {elapsed.TotalMilliseconds:F0} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed.TotalMilliseconds:F0} ms");
    }
}
