using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DateAndTime.Client;

/// <summary>Client part of "Date and time": the ADM "Set date and time" dialog; returns the payload JSON.</summary>
public sealed class DateTimeDialog : ITaskPluginDialog
{
    public string PluginId => DateTimeTaskPlugin.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);
        if (devices.Count == 0)
        {
            return null;
        }

        var model = new DateTimeDialogViewModel(devices);
        var window = new DateTimeWindow { DataContext = model };
        using var cts = new CancellationTokenSource();
        var load = model.LoadCurrentAsync(ctx, cts.Token);
        var applied = await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        await cts.CancelAsync().ConfigureAwait(true);
        await load.ConfigureAwait(true);
        return applied ? model.ResultJson : null;
    }
}
