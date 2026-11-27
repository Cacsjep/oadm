using Avalonia.Controls;
using Avalonia.Input;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>The Plates tab. View only: double click or Enter edits, several rows with Ctrl or Shift go to the view model in one step.</summary>
public partial class PlatesView : UserControl
{
    public PlatesView()
    {
        InitializeComponent();
        PlateGrid.SelectionChanged += (_, _) =>
        {
            if (DataContext is PlatesViewModel vm && !GridSort.IsMovingSelection(PlateGrid))
            {
                vm.SetSelection(PlateGrid.SelectedItems.OfType<PlateRow>());
            }
        };
        PlateGrid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: PlateRow } && DataContext is PlatesViewModel vm && vm.EditCommand.CanExecute(null))
            {
                vm.EditCommand.Execute(null);
            }
        };
        PlateGrid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is PlatesViewModel vm && vm.EditCommand.CanExecute(null))
            {
                vm.EditCommand.Execute(null);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The grid (tests).</summary>
    internal DataGrid Grid => PlateGrid;
}
