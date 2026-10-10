using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Oadm.Plugins.DeviceTriggers.Client;

/// <summary>The Device triggers page. View only: follows the server while shown, double click edits.</summary>
public partial class DeviceTriggersView : UserControl
{
    public DeviceTriggersView()
    {
        InitializeComponent();
        TriggerGrid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: TriggerRowViewModel } && DataContext is DeviceTriggersViewModel vm && vm.EditCommand.CanExecute(null))
            {
                vm.EditCommand.Execute(null);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as DeviceTriggersViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as DeviceTriggersViewModel)?.Deactivate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null)
        {
            (DataContext as DeviceTriggersViewModel)?.Activate();
        }
    }
}
