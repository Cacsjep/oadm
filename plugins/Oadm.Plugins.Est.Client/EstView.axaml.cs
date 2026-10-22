using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.Est.Client;

/// <summary>Page view. View only: activates the view model while the page is shown.</summary>
public partial class EstView : UserControl
{
    public EstView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as EstViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as EstViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
