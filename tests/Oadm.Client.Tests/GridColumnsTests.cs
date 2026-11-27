using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>
/// The shared column chooser pieces (docs/spec/visual-style.md "Small and scaled screens"): with many columns the grid
/// scrolls sideways, with few it does not; the saved order comes back and Reset restores the default columns.
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

    private static bool ScrollsSideways(DataGrid grid) =>
        grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Any(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal && b.IsEffectivelyVisible);

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public async Task Many_columns_scroll_sideways_and_few_do_not()
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var grid = Grid(18);
            var layout = new GridColumnLayout([.. Enumerable.Range(0, 18).Select(i => new GridColumnDefinition("c" + i, "Column " + i))], [], _ => { });
            using var binder = GridColumnBinder.Attach(grid, layout);
            var window = new Window { Width = 1200, Height = 400, Content = grid };
            window.Show();
            Pump();

            Assert.True(ScrollsSideways(grid)); // many columns: the grid scrolls sideways

            // Few columns: the stars share the whole width.
            foreach (var option in layout.Columns.Skip(4))
            {
                option.IsVisible = false;
            }

            Pump();
            Assert.Equal(4, grid.Columns.Count(c => c.IsVisible));
            Assert.False(ScrollsSideways(grid)); // few columns fit
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public void The_saved_order_comes_back_and_reset_restores_the_default_columns()
    {
        IReadOnlyList<GridColumnState> saved = [];
        GridColumnDefinition[] definitions = [new("a", "A"), new("b", "B"), new("c", "C", IsDefault: false)];
        var layout = new GridColumnLayout(definitions, [], s => saved = s);
        layout.Find("c")!.IsVisible = true;
        layout.Capture([("b", 0, 200), ("a", 1, 100)]);

        var again = new GridColumnLayout(definitions, saved, _ => { });
        Assert.Equal(["b", "a", "c"], again.VisibleKeys);

        again.ResetToDefault();
        Assert.Equal(["a", "b"], again.VisibleKeys);
    }
}
