using Avalonia.Controls;

using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Est.Client;

/// <summary>"Certificate enrollment": the cameras' enrollment profiles with Set up and Remove (<see cref="EstSetupWindow"/>).</summary>
public sealed class EstSetupDialog : ITaskPluginDialog
{
    public string PluginId => EstTaskIds.Setup;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            return null;
        }

        var vm = new EstSetupViewModel(devices, ctx);
        var window = new EstSetupWindow { DataContext = vm };
        var ok = false;
        vm.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        window.Opened += (_, _) => _ = vm.LoadAsync();
        await window.ShowDialog(owner).ConfigureAwait(true);
        return ok ? vm.Payload : null;
    }
}
