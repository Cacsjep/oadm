using System.Globalization;

using Avalonia.Controls;

using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Est.Client;

/// <summary>Texts of the EST task confirmations (the profiles come from the EST server page).</summary>
public static class EstConfirmations
{
    public static (string Title, string Message, string Confirm) Remove(int devices) =>
        (EstTaskIds.RemoveName,
         $"Remove certificate enrollment from {DeviceCount(devices)}? They keep their certificates until they end, but no longer renew them.",
         "Remove");

    public static string DeviceCount(int devices) =>
        devices == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{devices:N0} devices");
}

/// <summary>"Set up certificate enrollment": the confirmation with the IEEE 802.1X choice (<see cref="EstSetupWindow"/>).</summary>
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

        var vm = new EstSetupViewModel(devices);
        var window = new EstSetupWindow { DataContext = vm };
        var ok = false;
        vm.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await window.ShowDialog(owner).ConfigureAwait(true);
        return ok ? vm.Payload : null;
    }
}

/// <summary>"Remove certificate enrollment": the shared confirmation popup (red: it removes); the payload is empty.</summary>
public sealed class EstRemoveDialog : ITaskPluginDialog
{
    public string PluginId => EstTaskIds.Remove;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var (title, message, confirm) = EstConfirmations.Remove(devices.Count);
        return devices.Count > 0 && await MessageWindow.ConfirmAsync(owner, title, message, confirm).ConfigureAwait(true) ? "{}" : null;
    }
}
