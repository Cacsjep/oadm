using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Client.Controls;

namespace Oadm.Client.Tests.Controls;

/// <summary>
/// The grid's own range selection (internal in Avalonia.Controls.DataGrid): runs of rows in one step with one
/// SelectionChanged. Fails when a DataGrid update renames the members (the device grid would fall back to row by row,
/// quadratic at 100,000 rows).
/// </summary>
public sealed class GridRangeSelectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runs_of_rows_are_selected_in_one_step_in_the_shown_order(bool sorted)
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var g = GridSortTests.GridWindow.Show(500, grouped: false);
            Assert.True(GridRangeSelect.IsSupported(g.Grid), "Avalonia.Controls.DataGrid has no SetRowsSelection or NoSelectionChangeCount any more");
            if (sorted)
            {
                g.Grid.Columns[0].Sort(ListSortDirection.Descending);
                Dispatcher.UIThread.RunJobs();
            }

            var chosen = g.Rows.Where(r => r.Id % 3 != 0 || r.Id % 50 == 0).ToHashSet();
            g.Grid.SelectedItems.Add(g.Rows[4]); // already selected: stays, added once
            int changes = 0;
            g.Grid.SelectionChanged += (_, _) => changes++;

            Assert.True(GridRangeSelect.TrySelect(g.Grid, row => chosen.Contains((GridSortTests.TestRow)row)));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, changes);
            Assert.Equal(chosen.Select(r => r.Id).Order(), g.Grid.SelectedItems.Cast<GridSortTests.TestRow>().Select(r => r.Id).Order());
            Assert.Contains(g.Grid.SelectedItem, chosen);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_grouped_grid_is_left_to_the_caller()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var g = GridSortTests.GridWindow.Show(50, grouped: true);
            int before = g.Grid.SelectedItems.Count;
            Assert.False(GridRangeSelect.TrySelect(g.Grid, _ => true));
            Assert.Equal(before, g.Grid.SelectedItems.Count);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }
}
