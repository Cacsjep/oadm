using System.Collections;

using Avalonia;
using Avalonia.Controls;

using Oadm.Client.Infrastructure;

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
        if (sender is not DataGrid grid || GetSelectedItems(grid) is not { } target)
        {
            return;
        }

        // Scale: select all on 5,000 rows is one SelectionChanged with 5,000 added items. Item by item that
        // is 5,000 Contains scans and 5,000 change events (each rebuilding the context menu and the toolbar
        // state); a resettable target takes the grid's selection in one step instead.
        if (target is IResettableList resettable && e.AddedItems.Count + e.RemovedItems.Count > 1)
        {
            resettable.ResetTo(grid.SelectedItems);
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
