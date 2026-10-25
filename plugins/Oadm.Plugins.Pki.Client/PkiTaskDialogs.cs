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

    public static string DeviceCount(int devices) =>
        devices == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{devices:N0} devices");
}

/// <summary>"Disable HTTPS": the shared confirmation popup only; the payload is empty.</summary>
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

/// <summary>"Enable IEEE 802.1X": the policy and its values (<see cref="IssueCertificateWindow"/>, with the warning), then the clock check.</summary>
public sealed class Dot1xEnableDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.Dot1xEnable;

    public Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner) =>
        IssueDialog.ShowAsync(ctx, devices, owner, Policies.PolicyKinds.Dot1xCertificate);
}

/// <summary>"Enable HTTPS": the policy and its values (<see cref="IssueCertificateWindow"/>), then the clock check.</summary>
public sealed class HttpsEnableDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.HttpsEnable;

    public Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner) =>
        IssueDialog.ShowAsync(ctx, devices, owner, Policies.PolicyKinds.HttpsCertificate);
}

/// <summary>
/// "Renew certificates": the IEEE 802.1X certificate policy (<see cref="IssueCertificateWindow"/>, with the warning), then the
/// clock check; the payload is <see cref="RenewPayload"/>.
/// </summary>
public sealed class RenewDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.Renew;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            return null;
        }

        var result = await IssueDialog.ShowAsync(ctx, devices, owner, IssueCertificateViewModel.ForRenew(ctx, devices)).ConfigureAwait(true);
        return result is { } chosen ? PkiJson.Serialize(new RenewPayload { Dot1xPolicyId = chosen.PolicyId, ClockConfirmed = chosen.ClockConfirmed }) : null;
    }
}

/// <summary>
/// The flow of Enable HTTPS, Enable IEEE 802.1X and Renew certificates: the dialog reads the policies (Cancel stays usable),
/// Enable / Renew closes it with the policy, then the camera clocks are read and the question asked when one is behind. The
/// payload: <see cref="IssuePayload"/> with the answer; null when the user cancelled anywhere.
/// </summary>
public static class IssueDialog
{
    public static async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner, string kind)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            return null;
        }

        return await ShowAsync(ctx, devices, owner, new IssueCertificateViewModel(ctx, devices, kind)).ConfigureAwait(true) is { } payload
            ? PkiJson.Serialize(payload)
            : null;
    }

    /// <summary>The dialog of <paramref name="vm"/>, then the clock check: the policy and the answer, or null.</summary>
    public static async Task<IssuePayload?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner, IssueCertificateViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var window = new IssueCertificateWindow();
        PkiDialogs.Attach(window, vm);
        using var cancel = new CancellationTokenSource();
        window.Closed += (_, _) => cancel.Cancel();
        var load = LoadAsync(vm, cancel.Token);
        await window.ShowDialog(owner).ConfigureAwait(true);
        await load.ConfigureAwait(true);
        if (!vm.Completed || vm.Payload is not { } payload)
        {
            return null;
        }

        var clock = await ClockCheck.RunAsync(ctx, devices, owner, vm.Title).ConfigureAwait(true);
        return clock is null ? null : payload with { ClockConfirmed = Oadm.Plugins.DeviceClocks.CameraClock.Confirmed(clock) };
    }

    private static async Task LoadAsync(IssueCertificateViewModel vm, CancellationToken ct)
    {
        try
        {
            await vm.LoadAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // the dialog closed while reading
        }
    }
}

/// <summary>"View certificates": read-only window, never starts a task.</summary>
public sealed class ViewCertificatesDialog : ITaskPluginDialog
{
    public string PluginId => PkiTaskIds.View;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count > CertificatesViewModel.MaxDevices)
        {
            await MessageWindow.ShowMessageAsync(owner, PkiTaskIds.ViewName, CertificatesViewModel.TooManyText).ConfigureAwait(true);
            return null;
        }

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
        if (devices.Count > CertificatesViewModel.MaxDevices)
        {
            await MessageWindow.ShowMessageAsync(owner, PkiTaskIds.DeleteName, CertificatesViewModel.TooManyText).ConfigureAwait(true);
            return null;
        }

        var window = new CertificatesWindow();
        var viewModel = new CertificatesViewModel(ctx, devices, deleteMode: true);
        window.Attach(viewModel);
        _ = viewModel.LoadAsync();
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }
}

/// <summary>"Install certificates": files, password, purpose; uploads and returns the payload.</summary>
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
