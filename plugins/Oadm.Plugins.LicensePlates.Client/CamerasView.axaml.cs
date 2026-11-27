using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>The Cameras tab. View only: double click sets up the camera, several rows with Ctrl or Shift go to the view model in one step.</summary>
public partial class CamerasView : UserControl
{
    public CamerasView()
    {
        InitializeComponent();
        CameraGrid.SelectionChanged += (_, _) =>
        {
            if (DataContext is CamerasViewModel vm && !GridSort.IsMovingSelection(CameraGrid))
            {
                vm.SetSelection(CameraGrid.SelectedItems.OfType<CameraRowViewModel>());
            }
        };
        CameraGrid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: CameraRowViewModel } && DataContext is CamerasViewModel vm && vm.SetUpCommand.CanExecute(null))
            {
                vm.SetUpCommand.Execute(null);
            }
        };
    }

    /// <summary>The grid (tests).</summary>
    internal DataGrid Grid => CameraGrid;
}
