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
