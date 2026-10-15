using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.IoMonitor.Client;

/// <summary>The I/O Monitor page. View only: the device is read while the page is shown; the device button opens the shared picker.</summary>
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
        if (DataContext is IoMonitorViewModel vm)
        {
            vm.PickDevice = PickDeviceAsync;
        }

        if (TopLevel.GetTopLevel(this) is not null)
        {
            (DataContext as IoMonitorViewModel)?.Activate();
        }
    }

    private async Task<Guid?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, Guid? selected) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowOneAsync(owner, devices, selected, heading: "Choose a device").ConfigureAwait(true)
            : null;
}
