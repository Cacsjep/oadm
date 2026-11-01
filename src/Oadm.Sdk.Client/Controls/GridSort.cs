using System.Collections;
using System.Runtime.CompilerServices;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// Sorting a grid with a large selection. After a sort the grid looks up every selected row in the whole list (quadratic:
/// 30,000 selected rows took 4 s, 100,000 took 45 s). From <see cref="LargeSelection"/> rows on the selection comes off
/// the grid before the sort and goes back right after it, all rows in one step. The host installs this for every grid of
/// the client and of the plugins (<see cref="Install"/>); a grid that handles its own sort sets <c>Handled</c> in an
/// earlier <c>Sorting</c> handler (the device grid).
/// </summary>
public static class GridSort
{
    /// <summary>Selections from this size on are taken off the grid while it sorts.</summary>
    public const int LargeSelection = 1_000;

    private static readonly ConditionalWeakTable<DataGrid, object> Hooked = [];
    private static readonly ConditionalWeakTable<DataGrid, object> Moving = [];
    private static bool _installed;

    /// <summary>
    /// True while the grid's selection is off for a sort and on its way back: a <c>SelectionChanged</c> handler that hands the
    /// selection to a view model ignores the changes then (the same rows are selected afterwards).
    /// </summary>
    public static bool IsMovingSelection(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return Moving.TryGetValue(grid, out _);
    }

    /// <summary>Hooks the sort of every grid once it is loaded (the host calls this at start; tests too).</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        Control.LoadedEvent.AddClassHandler<DataGrid>(static (grid, _) => Attach(grid), RoutingStrategies.Direct);
    }

    /// <summary>Hooks the sort of one grid (once per grid).</summary>
    public static void Attach(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (Hooked.TryGetValue(grid, out _))
        {
            return;
        }

        Hooked.Add(grid, new object());
        grid.Sorting += OnSorting;
    }

    /// <summary>
    /// The grid's sort event, raised right before the grid sorts in the same job: the selection comes off now and goes back
    /// in the next job, before anything else runs or paints.
    /// </summary>
    private static void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (e.Handled || sender is not DataGrid grid || grid.SelectedItems.Count < LargeSelection || Moving.TryGetValue(grid, out _))
        {
            return;
        }

        int rows = RowCount(grid.CollectionView);
        HashSet<object>? keep = grid.SelectedItems.Count >= rows ? null : [.. grid.SelectedItems.Cast<object>()];
        Moving.Add(grid, new object());
        try
        {
            grid.SelectedItems.Clear();
        }
        catch
        {
            Moving.Remove(grid);
            throw;
        }

        Dispatcher.UIThread.Post(() => PutBack(grid, keep), DispatcherPriority.Send);
    }

    private static int RowCount(IDataGridCollectionView? view) => view switch
    {
        DataGridCollectionView rows => rows.Count,
        ICollection rows => rows.Count,
        _ => int.MaxValue,
    };

    /// <summary>
    /// Selects the rows again (<paramref name="keep"/> null: every row). All rows in one step; otherwise row by row (the grid
    /// looks up each row it adds in the whole list), in a plain grid from the smaller side (in a grouped one removing a row
    /// makes the grid walk the whole selection).
    /// </summary>
    private static void PutBack(DataGrid grid, HashSet<object>? keep)
    {
        try
        {
            if (grid.CollectionView is not { } view)
            {
                return;
            }

            if (keep is null || keep.Count >= RowCount(view))
            {
                grid.SelectAll();
                return;
            }

            bool grouped = ((IDataGridCollectionView)view).IsGrouping;
            if (!grouped && keep.Count * 2 > RowCount(view))
            {
                var others = ((IEnumerable)view).Cast<object>().Where(row => !keep.Contains(row)).ToList();
                grid.SelectAll();
                foreach (object row in others)
                {
                    grid.SelectedItems.Remove(row);
                }

                return;
            }

            foreach (object row in keep)
            {
                grid.SelectedItems.Add(row);
            }
        }
        finally
        {
            Moving.Remove(grid);
        }
    }
}
