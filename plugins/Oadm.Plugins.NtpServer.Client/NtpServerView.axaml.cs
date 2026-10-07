using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.NtpServer.Client;

/// <summary>Page view. View only: activates the view model while the page is shown.</summary>
public partial class NtpServerView : UserControl
{
    public NtpServerView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as NtpServerViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as NtpServerViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
