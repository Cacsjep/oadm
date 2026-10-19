using Avalonia;
using Avalonia.Controls;

namespace Oadm.Client.Sites;

/// <summary>The Sites page. View only: the list is watched while the page is shown.</summary>
public partial class SitesView : UserControl
{
    public SitesView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as SitesViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as SitesViewModel)?.Deactivate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null)
        {
            (DataContext as SitesViewModel)?.Activate();
        }
    }
}
