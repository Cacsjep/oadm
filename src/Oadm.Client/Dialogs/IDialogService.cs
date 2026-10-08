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

    /// <summary>Shows the "Log in" dialog of devices that reject their stored credentials; true when all accepted the login.</summary>
    Task<bool> ShowDeviceLoginAsync(Oadm.Client.Devices.DeviceLoginViewModel login);

    /// <summary>The window dialogs open on (active window, else the main window); null without a desktop.</summary>
    Avalonia.Controls.Window? Owner { get; }

    /// <summary>Shows a client plugin dialog; returns the payload JSON, or null when cancelled.</summary>
    Task<string?> ShowTaskPluginDialogAsync(ITaskPluginDialog dialog, IReadOnlyList<IDeviceInfo> devices);

    Task ShowTaskDetailsAsync(TaskDetailsViewModel details);

    /// <summary>Save file picker (overwrite asked); writes <paramref name="content"/>. Returns the file name, null when cancelled.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedFileName, FileType type, byte[] content);

    /// <summary>Open file picker; reads at most <paramref name="maxBytes"/> + 1 bytes (more = <see cref="PickedFile.IsTooLarge"/>). Null when cancelled.</summary>
    Task<PickedFile?> OpenFileAsync(string title, FileType type, int maxBytes);
}

/// <summary>A file type of the pickers: "CSV file", extension "csv".</summary>
public sealed record FileType(string Name, string Extension, string MimeType);

/// <summary>A file chosen in the open file picker.</summary>
/// <param name="Name">File name without folder.</param>
/// <param name="Content">The first bytes, at most the limit asked for.</param>
/// <param name="IsTooLarge">The file is larger than the limit; <paramref name="Content"/> is cut.</param>
public sealed record PickedFile(string Name, byte[] Content, bool IsTooLarge);

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
