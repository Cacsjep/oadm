using System.Globalization;

using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Overlays.Client;

/// <summary>
/// The dialog of Add, Replace and Remove matching overlays: <see cref="OverlaysWindow"/> with the entry's mode fixed; returns the
/// list payload, or null when cancelled.
/// </summary>
public abstract class OverlaysListDialog(OverlaysMode mode, string pluginId) : ITaskPluginDialog
{
    private static readonly OverlaysTaskPlugin Reasons = new AddOverlaysTask();

    public string PluginId { get; } = pluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);

        var window = new OverlaysWindow();
        var all = ctx.AllDevices.Count > 0 ? ctx.AllDevices : devices;
        var vm = new OverlaysDialogViewModel(
            mode,
            devices,
            all,
            (id, method, ct) => ctx.QueryAsync(id, method, null, ct),
            async cameras =>
            {
                var id = await DevicePickerWindow.ShowOneAsync(window, cameras, null,
                    d => OverlaysTaskPlugin.CanRunOn(d) ? null : Reasons.NotSupportedReason(d), "Choose a camera").ConfigureAwait(true);
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

/// <summary>"Add overlays".</summary>
public sealed class AddOverlaysDialog() : OverlaysListDialog(OverlaysMode.Add, AddOverlaysTask.PluginId);

/// <summary>"Replace overlays".</summary>
public sealed class ReplaceOverlaysDialog() : OverlaysListDialog(OverlaysMode.Replace, ReplaceOverlaysTask.PluginId);

/// <summary>"Remove matching overlays".</summary>
public sealed class RemoveMatchingOverlaysDialog() : OverlaysListDialog(OverlaysMode.RemoveMatching, RemoveMatchingOverlaysTask.PluginId);

/// <summary>"Remove all overlays": no form, only a confirmation with the red Remove button; returns "{}" or null.</summary>
public sealed class RemoveAllOverlaysDialog : ITaskPluginDialog
{
    public const string Title = "Remove all overlays";

    public string PluginId => RemoveAllOverlaysTask.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);
        return await MessageWindow.ConfirmAsync(owner, Title, Question(devices), "Remove").ConfigureAwait(true) ? "{}" : null;
    }

    /// <summary>"Remove all text overlays of 243 cameras? Image overlays stay." and, when some are not, the unsupported line.</summary>
    public static string Question(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var compatible = 0;
        foreach (var device in devices)
        {
            if (OverlaysTaskPlugin.CanRunOn(device))
            {
                compatible++;
            }
        }

        var question = string.Create(CultureInfo.CurrentCulture,
            $"Remove all text overlays of {compatible:N0} {(compatible == 1 ? "camera" : "cameras")}? Image overlays stay.");
        return OverlayTexts.Unsupported(devices.Count, compatible) is { } unsupported ? question + Environment.NewLine + unsupported : question;
    }
}

/// <summary>The texts the dialogs share.</summary>
public static class OverlayTexts
{
    /// <summary>"7 of 250 cameras are not supported."; null when every selected device is.</summary>
    public static string? Unsupported(int total, int compatible)
    {
        var not = total - compatible;
        return not <= 0 ? null
            : string.Create(CultureInfo.CurrentCulture, $"{not:N0} of {total:N0} {(total == 1 ? "camera" : "cameras")} {(not == 1 ? "is" : "are")} not supported.");
    }
}
