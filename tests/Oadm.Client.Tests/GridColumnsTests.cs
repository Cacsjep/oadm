using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>
/// The shared column chooser pieces (docs/spec/visual-style.md "Small and scaled screens"): every column keeps its minimum
/// width, star columns share only the room left over, and the grid scrolls sideways when the minimums do not fit; saved
/// widths come back as star weights.
/// </summary>
public sealed class GridColumnsTests
{
    private static DataGrid Grid(int columns)
    {
        var grid = new DataGrid();
        for (var i = 0; i < columns; i++)
        {
            grid.Columns.Add(new DataGridTextColumn { Header = "Column " + i, Tag = "c" + i, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120 });
        }

        return grid;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public async Task Columns_keep_their_minimum_and_the_grid_scrolls_sideways_when_they_do_not_fit()
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var grid = Grid(18);
            var layout = new GridColumnLayout([.. Enumerable.Range(0, 18).Select(i => new GridColumnDefinition("c" + i, "Column " + i))], [], _ => { });
            using var binder = GridColumnBinder.Attach(grid, layout);
            var window = new Window { Width = 1200, Height = 400, Content = grid };
            window.Show();
            Pump();

            Assert.All(grid.Columns, c => Assert.True(c.ActualWidth >= c.MinWidth - 0.5, $"{c.Header}: {c.ActualWidth} < {c.MinWidth}"));
            Assert.True(grid.Columns.Sum(c => c.ActualWidth) > 1200); // wider than the window: the grid scrolls sideways

            // Few columns: the stars share the whole width.
            foreach (var option in layout.Columns.Skip(4))
            {
                option.IsVisible = false;
            }

            Pump();
            Assert.Equal(4, grid.Columns.Count(c => c.IsVisible));
            Assert.All(grid.Columns.Where(c => c.IsVisible), c => Assert.True(c.ActualWidth > c.MinWidth));
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public void Saved_widths_and_order_come_back_and_new_columns_take_their_default()
    {
        IReadOnlyList<GridColumnState> saved = [];
        GridColumnDefinition[] definitions = [new("a", "A"), new("b", "B"), new("c", "C", IsDefault: false)];
        var layout = new GridColumnLayout(definitions, [], s => saved = s);
        layout.Find("c")!.IsVisible = true;
        layout.Capture([("b", 0, 200), ("a", 1, 100)]);

        var again = new GridColumnLayout(definitions, saved, _ => { });
        Assert.Equal(["b", "a", "c"], again.VisibleKeys);
        Assert.Equal(200, again.Find("b")!.Width);

        again.ResetToDefault();
        Assert.Equal(["a", "b"], again.VisibleKeys);
    }
}
