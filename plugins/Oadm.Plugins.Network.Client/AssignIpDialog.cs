using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>Client part of "Assign IP address...": DHCP or an IP range for the selected devices, returns the payload JSON.</summary>
public sealed class AssignIpDialog : ITaskPluginDialog
{
    /// <summary>Dialog size relative to the main window, so no fixed pixel sizes are needed.</summary>
    private const double WidthShare = 0.55;
    private const double HeightShare = 0.8;

    public string PluginId => AssignIpTaskPlugin.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);
        if (devices.Count == 0)
        {
            return null;
        }

        using var model = new AssignIpViewModel(devices);
        var window = new AssignIpWindow { DataContext = model };
        if (owner.Bounds.Width > 0 && owner.Bounds.Height > 0)
        {
            window.Width = owner.Bounds.Width * WidthShare;
            window.Height = owner.Bounds.Height * HeightShare;
        }

        using var cts = new CancellationTokenSource();
        var prefill = model.LoadCurrentAsync(ctx, cts.Token);
        var finished = await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        await cts.CancelAsync().ConfigureAwait(true);
        await prefill.ConfigureAwait(true);
        return finished ? model.ResultJson : null;
    }
}
