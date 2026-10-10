using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.IoMonitor.Client;

/// <summary>The I/O Monitor page. View only: the device is read while the page is shown.</summary>
public partial class IoMonitorView : UserControl
{
    public IoMonitorView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as IoMonitorViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as IoMonitorViewModel)?.Deactivate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null)
        {
            (DataContext as IoMonitorViewModel)?.Activate();
        }
    }
}
