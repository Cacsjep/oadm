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

    private static readonly HashSet<string> FixedWidth = new(StringComparer.Ordinal) { LogColumns.Picture };
    private IDisposable? _columns;

    /// <summary>The chosen columns, their order and widths (Choose columns, like the device grid).</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _columns?.Dispose();
        _columns = DataContext is LogViewModel vm ? GridColumnBinder.Attach(LogGrid, vm.Columns, fixedWidth: FixedWidth) : null;
    }

    /// <summary>The grid (tests).</summary>
    internal DataGrid Grid => LogGrid;
}
