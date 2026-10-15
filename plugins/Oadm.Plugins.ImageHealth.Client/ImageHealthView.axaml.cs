using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.ImageHealth.Client;

/// <summary>Shows the dashboard; the view model connects while the view is in the visual tree.</summary>
public partial class ImageHealthView : UserControl
{
    public ImageHealthView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ImageHealthViewModel vm)
        {
            vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner
                && await vm.DeviceChoice.PickAsync(owner, "Choose cameras").ConfigureAwait(true);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ImageHealthViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as ImageHealthViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
