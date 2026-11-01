using System.Collections;
using System.Runtime.CompilerServices;

using Avalonia.Collections;
using Avalonia.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// Selects many rows of a plain grid (no groups) in one step. The grid's public way is row by row: every row added is looked
/// up in the whole list and raises its own SelectionChanged that walks the whole selection (quadratic: putting back 28,000
/// selected of 100,000 rows after a search took 6 to 19 s). This uses the grid's own range selection (the one of select all
/// and Shift+click, internal in Avalonia.Controls.DataGrid 12) for each run of rows, with one SelectionChanged at the end:
/// O(rows). <see cref="TrySelect"/> returns false when the grid has no such members (another DataGrid version) and changes
/// nothing then; the caller selects row by row. A test pins that the members exist (GridRangeSelectTests).
/// </summary>
internal static class GridRangeSelect
{
    private static bool? _supported;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetRowsSelection")]
    private static extern void SetRowsSelection(DataGrid grid, int startSlot, int endSlot);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_noSelectionChangeCount")]
    private static extern ref int NoSelectionChangeCount(DataGrid grid);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_NoSelectionChangeCount")]
    private static extern void SetNoSelectionChangeCount(DataGrid grid, int value);

    /// <summary>Whether this DataGrid version has the members (probed once with calls that change nothing).</summary>
    internal static bool IsSupported(DataGrid grid)
    {
        if (_supported is { } known)
        {
            return known;
        }

        try
        {
            int count = NoSelectionChangeCount(grid);
            SetNoSelectionChangeCount(grid, count); // the same value: nothing to flush while nothing changed
            SetRowsSelection(grid, 0, -1); // an empty range
            _supported = true;
        }
        catch (Exception e) when (e is MissingMemberException or MemberAccessException)
        {
            _supported = false;
        }

        return _supported.Value;
    }

    /// <summary>
    /// Adds the rows <paramref name="selected"/> picks to the grid's selection, walking the grid's rows once in their shown
    /// order. Only a plain grid (no groups: a row's place is its slot). False when not possible; nothing changed then.
    /// </summary>
    public static bool TrySelect(DataGrid grid, Func<object, bool> selected)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(selected);
        if (grid.CollectionView is not { } view || ((IDataGridCollectionView)view).IsGrouping
            || grid.SelectionMode != DataGridSelectionMode.Extended || !IsSupported(grid))
        {
            return false;
        }

        NoSelectionChangeCount(grid)++; // one SelectionChanged for all runs, raised when the count is back at zero
        try
        {
            int index = 0;
            int start = -1;
            foreach (object? row in (IEnumerable)view)
            {
                if (row is not null && selected(row))
                {
                    if (start < 0)
                    {
                        start = index;
                    }
                }
                else if (start >= 0)
                {
                    SetRowsSelection(grid, start, index - 1); // runs in ascending order: each one appends to the grid's table
                    start = -1;
                }

                index++;
            }

            if (start >= 0)
            {
                SetRowsSelection(grid, start, index - 1);
            }
        }
        finally
        {
            SetNoSelectionChangeCount(grid, NoSelectionChangeCount(grid) - 1);
        }

        return true;
    }
}
