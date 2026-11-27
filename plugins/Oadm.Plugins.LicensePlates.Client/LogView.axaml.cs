using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// The Log tab. View only: a row the grid creates reads its plate crop, the last rows read the next older page, several rows
/// with Ctrl or Shift go to the view model in one step.
/// </summary>
public partial class LogView : UserControl
{
    /// <summary>The next older page is read once a row this close to the end shows.</summary>
    public const int LoadAhead = 20;

    public LogView()
    {
        InitializeComponent();
        // The makes of the Vehicle filter are read when its flyout opens first, never before.
        VehicleButton.Flyout?.Opened += (_, _) => (DataContext as LogViewModel)?.EnsureMakes();
        LogGrid.LoadingRow += (_, e) =>
        {
            if (DataContext is not LogViewModel vm || e.Row.DataContext is not LogRow row)
            {
                return;
            }

            vm.EnsurePicture(row);
            if (e.Row.Index >= vm.Rows.Count - LoadAhead)
            {
                vm.LoadOlder();
            }
        };
        LogGrid.SelectionChanged += (_, _) =>
        {
            if (DataContext is LogViewModel vm && !GridSort.IsMovingSelection(LogGrid))
            {
                vm.SetSelection(LogGrid.SelectedItems.OfType<LogRow>());
            }
        };
    }

    /// <summary>The grid (tests).</summary>
    internal DataGrid Grid => LogGrid;
}
