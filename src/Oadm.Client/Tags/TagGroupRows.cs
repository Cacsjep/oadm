using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Oadm.Client.Tags;

/// <summary>
/// The group header rows of a grid in group mode (<see cref="DeviceTagGrouping.Rows"/>): a header row gets the class
/// <c>tagHeader</c> (the client theme gives it the header template: chevron, dot, "Building A · 42 devices"), and a click
/// on a header only works its chevron, it never selects the header.
/// </summary>
public static class TagGroupRows
{
    public const string HeaderClass = "tagHeader";

    public static void Attach(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.LoadingRow += (_, e) => e.Row.Classes.Set(HeaderClass, e.Row.DataContext is TagGroupKey);
        grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<DataGridRow>(includeSelf: true) is not { } row
            || !row.Classes.Contains(HeaderClass))
        {
            return;
        }

        if (source.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is null)
        {
            e.Handled = true; // the row: no selection
        }
    }
}
