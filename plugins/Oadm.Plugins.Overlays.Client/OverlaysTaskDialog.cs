using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Overlays.Client;

/// <summary>Client part of "Text overlays": opens <see cref="OverlaysWindow"/> and returns the payload.</summary>
public sealed class OverlaysTaskDialog : ITaskPluginDialog
{
    private static readonly OverlaysTaskPlugin Plugin = new();

    public string PluginId => OverlaysTaskPlugin.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);

        var window = new OverlaysWindow();
        var all = ctx.AllDevices.Count > 0 ? ctx.AllDevices : devices;
        var vm = new OverlaysDialogViewModel(
            devices,
            all,
            (id, method, ct) => ctx.QueryAsync(id, method, null, ct),
            async cameras =>
            {
                var id = await DevicePickerWindow.ShowOneAsync(window, cameras, null,
                    d => OverlaysTaskPlugin.CanRunOn(d) ? null : Plugin.NotSupportedReason(d), "Choose a camera").ConfigureAwait(true);
                return id is { } chosen ? cameras.FirstOrDefault(d => d.Id == chosen) : null;
            });
        using var closing = new CancellationTokenSource();
        window.DataContext = vm;
        var load = vm.LoadAsync(closing.Token);
        try
        {
            return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
        }
        finally
        {
            await closing.CancelAsync().ConfigureAwait(true);
            await load.ConfigureAwait(true);
        }
    }
}
