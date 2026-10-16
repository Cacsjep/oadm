using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Samples.FirmwareOverview.Client;

public sealed partial class FirmwareOverviewView : UserControl
{
    public FirmwareOverviewView() => AvaloniaXamlLoader.Load(this);

    // The picker needs the window: the view hands the view model a way to open it.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FirmwareOverviewViewModel vm)
        {
            vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner && await vm.Devices.PickAsync(owner, "Choose devices");
        }
    }

    // The host disposes the view model when the page or its window closes; that ends the watch.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as FirmwareOverviewViewModel)?.Start();
    }
}
