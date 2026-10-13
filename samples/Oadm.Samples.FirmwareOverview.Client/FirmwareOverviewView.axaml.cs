using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Samples.FirmwareOverview.Client;

public sealed partial class FirmwareOverviewView : UserControl
{
    public FirmwareOverviewView() => AvaloniaXamlLoader.Load(this);

    // The host disposes the view model when the page or its window closes; that ends the watch.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as FirmwareOverviewViewModel)?.Start();
    }
}
