using System.Collections;

using Avalonia;
using Avalonia.Controls;

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
