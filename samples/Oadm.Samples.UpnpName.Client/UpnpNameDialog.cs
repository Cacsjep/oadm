using System.Text.Json;

using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Samples.UpnpName.Client;

/// <summary>Found by the client loader: opens before the task runs and returns the payload (null = cancelled).</summary>
public sealed class UpnpNameDialog : ITaskPluginDialog
{
    public string PluginId => UpnpName.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        var model = new UpnpNameViewModel(devices.Count);
        var window = new UpnpNameWindow { DataContext = model };

        // Prefill with the name of the first device; the dialog works without it.
        using var cts = new CancellationTokenSource();
        _ = model.LoadAsync(token => ctx.QueryAsync(devices[0].Id, "current", null, token), cts.Token);

        var ok = await window.ShowDialog<bool>(owner);
        await cts.CancelAsync();
        return ok ? JsonSerializer.Serialize(new UpnpNamePayload(model.Name.Trim()), UpnpName.Json) : null;
    }
}
