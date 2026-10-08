using Oadm.Client.Discovery;
using Oadm.Client.Tasks;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Dialogs;

/// <summary>Modal UI used by view models. The Avalonia implementation lives in <see cref="AvaloniaDialogService"/>.</summary>
public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Shows the add devices page; true when devices were added.</summary>
    Task<bool> ShowAddDevicesAsync(AddDevicesViewModel page);

    /// <summary>The window dialogs open on (active window, else the main window); null without a desktop.</summary>
    Avalonia.Controls.Window? Owner { get; }

    /// <summary>Shows a client plugin dialog; returns the payload JSON, or null when cancelled.</summary>
    Task<string?> ShowTaskPluginDialogAsync(ITaskPluginDialog dialog, IReadOnlyList<IDeviceInfo> devices);

    Task ShowTaskDetailsAsync(TaskDetailsViewModel details);
}

/// <summary>Puts text on the system clipboard (Avalonia clipboard of the current window).</summary>
public interface IClipboardService
{
    /// <returns>False without a window or clipboard.</returns>
    Task<bool> SetTextAsync(string text);
}

/// <summary>Opens URLs (device web UI) in the default browser.</summary>
public interface IUrlLauncher
{
    Task<bool> OpenAsync(Uri uri);
}
