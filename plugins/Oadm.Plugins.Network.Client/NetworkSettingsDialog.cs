using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>Client part of "Network settings...": collects the settings and returns the payload JSON.</summary>
public sealed class NetworkSettingsDialog : ITaskPluginDialog
{
    /// <summary>Dialog size relative to the main window, so no fixed pixel sizes are needed.</summary>
    private const double WidthShare = 0.45;
    private const double HeightShare = 0.85;

    public string PluginId => NetworkSettingsTaskPlugin.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);
        if (devices.Count == 0)
        {
            return null;
        }

        var model = new NetworkSettingsViewModel(devices);
        var window = new NetworkSettingsWindow { DataContext = model };
        if (owner.Bounds.Width > 0 && owner.Bounds.Height > 0)
        {
            window.Width = owner.Bounds.Width * WidthShare;
            window.Height = owner.Bounds.Height * HeightShare;
        }

        using var cts = new CancellationTokenSource();
        var prefill = model.LoadCurrentAsync(ctx, cts.Token);
        var applied = await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        await cts.CancelAsync().ConfigureAwait(true);
        await prefill.ConfigureAwait(true);
        return applied ? model.ResultJson : null;
    }
}
