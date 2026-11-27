using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Devices;

/// <summary>
/// The device grid's use of the shared <see cref="GridColumnBinder"/>: the saved visibility, order and widths of
/// <see cref="ColumnLayoutViewModel"/>, the columns a mode hides (<see cref="HiddenByGroupMode"/>), the icon column fixed.
/// </summary>
internal static class DeviceGridLayoutBinder
{
    private static readonly HashSet<string> Fixed = new(StringComparer.Ordinal) { ColumnLayoutViewModel.IconKey };

    /// <summary>
    /// Binds the grid to the layout until the result is disposed. The layout lives longer than a view: the view disposes
    /// the result when it lets go of its view model (a grid that was never shown never leaves a visual tree).
    /// </summary>
    public static IDisposable Attach(DataGrid grid, ColumnLayoutViewModel layout)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(layout);
        return GridColumnBinder.Attach(grid, layout.Layout, key => HiddenByGroupMode(grid, key), Fixed);
    }

    /// <summary>
    /// Columns a mode hides whatever the column chooser says: the Tags column in group mode (grid class <c>tagGroups</c>: the
    /// group header names the tag), the Compliance column while each check has its own column (<c>complianceExpanded</c>) or
    /// the role may not see compliance (<c>noCompliance</c>), the Site column while the server has no sites (<c>noSites</c>).
    /// </summary>
    public static bool HiddenByGroupMode(DataGrid grid, string key) =>
        (key == "tags" && grid.Classes.Contains("tagGroups"))
        || (key == DevicesViewModel.SiteColumnKey && grid.Classes.Contains("noSites"))
        || (key == ColumnLayoutViewModel.ComplianceKey && (grid.Classes.Contains("complianceExpanded") || grid.Classes.Contains("noCompliance")));
}
