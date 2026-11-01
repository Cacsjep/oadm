using System.Collections;

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

using Oadm.Client.Infrastructure;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// Attached behavior that mirrors a DataGrid's multi-selection into a view model collection
/// (DataGrid.SelectedItems is not bindable).
/// </summary>
public static class GridSelection
{
    public static readonly AttachedProperty<IList?> SelectedItemsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IList?>("SelectedItems", typeof(GridSelection));

    static GridSelection()
    {
        SelectedItemsProperty.Changed.AddClassHandler<DataGrid>(OnSelectedItemsChanged);
    }

    public static IList? GetSelectedItems(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return grid.GetValue(SelectedItemsProperty);
    }

    public static void SetSelectedItems(DataGrid grid, IList? value)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.SetValue(SelectedItemsProperty, value);
    }

    /// <summary>
    /// Stops mirroring until the result is disposed: the view takes a large selection off the grid while it sorts or
    /// regroups and puts it back, and the view model's selection stays as it was meanwhile.
    /// </summary>
    public static IDisposable Pause(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.SelectionChanged -= OnGridSelectionChanged;
        return new Resume(grid);
    }

    /// <summary>
    /// Hands the grid's selection to the view model in one step (after the view changed it without mirroring);
    /// <paramref name="selected"/>: the rows the view just selected, when it knows them.
    /// </summary>
    public static void Sync(DataGrid grid, IEnumerable? selected = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (GetSelectedItems(grid) is not { } target)
        {
            return;
        }

        selected ??= SelectedOf(grid);
        if (target is IResettableList resettable)
        {
            resettable.ResetTo(selected);
            return;
        }

        target.Clear();
        foreach (object? item in selected)
        {
            target.Add(item);
        }
    }

    /// <summary>
    /// The grid's selected rows. In a grouped view the grid finds each selected row by walking its group from the start
    /// (quadratic: 100,000 selected rows took 12 s): with every row selected the rows themselves are the selection.
    /// </summary>
    internal static IEnumerable SelectedOf(DataGrid grid)
    {
        if (grid.ItemsSource is DataGridCollectionView { Filter: null, SourceCollection: { } rows } view
            && ((IDataGridCollectionView)view).IsGrouping && grid.SelectedItems.Count == view.Count)
        {
            return rows;
        }

        return grid.SelectedItems;
    }

    private sealed class Resume(DataGrid grid) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            if (GetSelectedItems(grid) is not null)
            {
                grid.SelectionChanged -= OnGridSelectionChanged;
                grid.SelectionChanged += OnGridSelectionChanged;
            }
        }
    }

    private static void OnSelectedItemsChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        grid.SelectionChanged -= OnGridSelectionChanged;
        if (e.NewValue is IList target)
        {
            target.Clear();
            foreach (object? item in grid.SelectedItems)
            {
                target.Add(item);
            }

            grid.SelectionChanged += OnGridSelectionChanged;
        }
    }

    private static void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid || GetSelectedItems(grid) is not { } target || GridSort.IsMovingSelection(grid))
        {
            return; // while a sort moves the selection: the same rows are selected after
        }

        // Scale: select all on 5,000 rows is one SelectionChanged with 5,000 added items. Item by item that
        // is 5,000 Contains scans and 5,000 change events (each rebuilding the context menu and the toolbar
        // state); a resettable target takes the grid's selection in one step instead.
        if (target is IResettableList resettable && e.AddedItems.Count + e.RemovedItems.Count > 1)
        {
            resettable.ResetTo(SelectedOf(grid));
            return;
        }

        foreach (object? removed in e.RemovedItems)
        {
            target.Remove(removed);
        }

        foreach (object? added in e.AddedItems)
        {
            if (!target.Contains(added))
            {
                target.Add(added);
            }
        }
    }
}
