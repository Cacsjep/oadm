using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Acap.Client;

/// <summary>Client part of the "Applications (ACAP)..." task: opens <see cref="AcapWindow"/> and returns its payload.</summary>
public sealed class AcapTaskDialog : ITaskPluginDialog
{
    public string PluginId => AcapPlugin.Id;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);
        if (devices.Count == 0)
        {
            return null;
        }

        var window = new AcapWindow();
        var viewModel = new AcapDialogViewModel(ctx, devices, window);
        window.Attach(viewModel);
        _ = viewModel.InitializeAsync();
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }
}
