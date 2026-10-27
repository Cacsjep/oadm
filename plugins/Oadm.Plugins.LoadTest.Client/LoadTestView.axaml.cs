using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>Shows the page; the view model watches the live events while the view is in the visual tree.</summary>
public partial class LoadTestView : UserControl
{
    public LoadTestView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LoadTestViewModel vm)
        {
            vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner
                && await vm.DeviceChoice.PickAsync(owner, "Choose cameras").ConfigureAwait(true);
            vm.ShowRunList = async runs =>
            {
                if (TopLevel.GetTopLevel(this) is Window owner)
                {
                    await RunListWindow.ShowAsync(owner, runs).ConfigureAwait(true);
                }
            };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as LoadTestViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as LoadTestViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
