using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Plugins.DhcpServer.Client;

/// <summary>Page view. View only: activates the view model while shown, opens the static lease dialog, refreshes "Expires".</summary>
public partial class DhcpServerView : UserControl, IDhcpPageUi
{
    private readonly DispatcherTimer _expires = new() { Interval = TimeSpan.FromMinutes(1) };

    public DhcpServerView()
    {
        InitializeComponent();
        _expires.Tick += (_, _) => (DataContext as DhcpServerViewModel)?.RefreshExpires();
    }

    public async Task ShowStaticLeaseDialogAsync(StaticLeaseDialogViewModel dialog)
    {
        var window = new StaticLeaseWindow();
        window.Attach(dialog);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DhcpServerViewModel vm)
        {
            vm.Ui = this;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as DhcpServerViewModel)?.Activate();
        _expires.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _expires.Stop();
        (DataContext as DhcpServerViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
