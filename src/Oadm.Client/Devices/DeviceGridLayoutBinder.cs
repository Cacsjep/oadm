using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Oadm.Client.Devices;

/// <summary>
/// View glue between the device DataGrid and <see cref="ColumnLayoutViewModel"/>: applies saved visibility,
/// order and widths (columns are matched by their Tag), and captures user reorder/resize for persistence.
/// </summary>
internal sealed class DeviceGridLayoutBinder
{
    private readonly DataGrid _grid;
    private readonly ColumnLayoutViewModel _layout;

    private DeviceGridLayoutBinder(DataGrid grid, ColumnLayoutViewModel layout)
    {
        _grid = grid;
        _layout = layout;
    }

    public static void Attach(DataGrid grid, ColumnLayoutViewModel layout)
    {
        var binder = new DeviceGridLayoutBinder(grid, layout);
        binder.Apply();
        foreach (ColumnOptionViewModel option in layout.Columns)
        {
            option.PropertyChanged += binder.OnOptionChanged;
        }

        grid.ColumnReordered += (_, _) => binder.Capture();
        grid.AddHandler(InputElement.PointerReleasedEvent, (_, _) => binder.Capture(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        grid.DetachedFromVisualTree += (_, _) =>
        {
            binder.Capture();
            foreach (ColumnOptionViewModel option in layout.Columns)
            {
                option.PropertyChanged -= binder.OnOptionChanged;
            }
        };
    }

    private static string? KeyOf(DataGridColumn column) => column.Tag as string;

    private void Apply()
    {
        var ordered = _grid.Columns
            .Select(c => (Column: c, Option: KeyOf(c) is { } key ? _layout.Find(key) : null))
            .Where(x => x.Option is not null)
            .OrderBy(x => x.Option!.DisplayIndex)
            .ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            (DataGridColumn column, ColumnOptionViewModel? option) = ordered[i];
            column.IsVisible = option!.IsVisible;
            // Saved widths are used as star weights: columns keep their proportions and the
            // grid always fills its full width. The icon column stays fixed.
            if (option.Width > 0 && option.Key != "icon")
            {
                column.Width = new DataGridLength(option.Width, DataGridLengthUnitType.Star);
            }

            column.DisplayIndex = i;
        }
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ColumnOptionViewModel option && e.PropertyName == nameof(ColumnOptionViewModel.IsVisible))
        {
            DataGridColumn? column = _grid.Columns.FirstOrDefault(c => KeyOf(c) == option.Key);
            if (column is not null)
            {
                column.IsVisible = option.IsVisible;
            }
        }
    }

    private void Capture()
    {
        var state = _grid.Columns
            .Where(c => KeyOf(c) is not null)
            .Select(c => (KeyOf(c)!, c.DisplayIndex, c.IsVisible && c.ActualWidth > 0 ? c.ActualWidth : _layout.Find(KeyOf(c)!)?.Width ?? 0))
            .ToList();
        _layout.Capture(state);
    }
}
