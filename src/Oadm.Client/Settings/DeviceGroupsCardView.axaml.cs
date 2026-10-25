using Avalonia.Controls;
using Avalonia.Interactivity;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Settings;

public partial class DeviceGroupsCardView : UserControl
{
    public DeviceGroupsCardView()
    {
        InitializeComponent();
    }

    /// <summary>The row's ⋯ button: its menu (the entries come from the view model).</summary>
    private void OnRowActions(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceGroupRowViewModel row } button && DataContext is DeviceGroupsViewModel vm)
        {
            RowActions.Show(button, vm.RowActionsOf(row));
        }
    }
}
