using System.Globalization;

using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>Texts of the confirmations that run before a risky Security task (no dialog window of their own).</summary>
public static class PkiConfirmations
{
    /// <summary>"Disable HTTPS on 3 devices?" + the warning.</summary>
    public static (string Title, string Message, string Confirm) HttpsDisable(int devices) =>
        ("Disable HTTPS", $"Disable HTTPS on {DeviceCount(devices)}? {PkiTaskIds.HttpsDisableWarning} OADM connects to them over HTTP afterwards.", "Disable");

    /// <summary>"Enable IEEE 802.1X on 3 devices?" + the warning and the settings it uses.</summary>
    public static (string Title, string Message, string Confirm) Dot1xEnable(int devices) =>
        ("Enable IEEE 802.1X",
         $"Enable IEEE 802.1X on {DeviceCount(devices)}? {PkiTaskIds.Dot1xWarning} The devices get a client certificate from the OADM CA and the 802.1X settings of the PKI page.",
         "Enable");

    public static string DeviceCount(int devices) =>
        devices == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{devices:N0} devices");
}

/// <summary>"HTTPS: Disable": the shared confirmation popup only; the payload is empty.</summary>
public sealed class HttpsDisableDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.HttpsDisable;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var (title, message, confirm) = PkiConfirmations.HttpsDisable(devices.Count);
        return devices.Count > 0 && await MessageWindow.ConfirmAsync(owner, title, message, confirm).ConfigureAwait(true) ? "{}" : null;
    }
}

/// <summary>"IEEE 802.1X: Enable/Update": the shared confirmation popup only; the settings come from the PKI page.</summary>
public sealed class Dot1xEnableDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.Dot1xEnable;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var (title, message, confirm) = PkiConfirmations.Dot1xEnable(devices.Count);
        return devices.Count > 0 && await MessageWindow.ConfirmAsync(owner, title, message, confirm).ConfigureAwait(true) ? "{}" : null;
    }
}

/// <summary>"View installed certificates": read-only window, never starts a task.</summary>
public sealed class ViewCertificatesDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.View;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var window = new CertificatesWindow();
        var viewModel = new CertificatesViewModel(ctx, devices, deleteMode: false);
        window.Attach(viewModel);
        _ = viewModel.LoadAsync();
        await window.ShowDialog<string?>(owner).ConfigureAwait(true);
        return null;
    }
}

/// <summary>"Delete certificates": the list with check boxes; returns the aliases per device.</summary>
public sealed class DeleteCertificatesDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.Delete;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var window = new CertificatesWindow();
        var viewModel = new CertificatesViewModel(ctx, devices, deleteMode: true);
        window.Attach(viewModel);
        _ = viewModel.LoadAsync();
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }
}

/// <summary>"Install certificates manually": files, password, purpose; uploads and returns the payload.</summary>
public sealed class InstallCertificatesDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.Install;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var window = new InstallCertificatesWindow();
        window.Attach(new InstallCertificatesViewModel(ctx, devices));
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }
}

/// <summary>"Install CA certificates": CA certificate files (PEM, bundles, DER); returns the certificates as the payload.</summary>
public sealed class InstallCaCertificatesDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.InstallCa;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var window = new InstallCaCertificatesWindow();
        window.Attach(new InstallCaCertificatesViewModel(devices));
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }
}
