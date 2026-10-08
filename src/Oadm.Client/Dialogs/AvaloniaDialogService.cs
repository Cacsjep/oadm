using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

using Oadm.Client.Api;
using Oadm.Client.Discovery;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Dialogs;

public sealed class AvaloniaDialogService(IOadmApi api) : IDialogService, IUrlLauncher, IClipboardService
{
    public async Task ShowMessageAsync(string title, string message)
    {
        if (CurrentOwner() is not { } owner)
        {
            return;
        }

        await MessageWindow.ShowMessageAsync(owner, title, message).ConfigureAwait(true);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        return await MessageWindow.ConfirmAsync(owner, title, message, confirmText).ConfigureAwait(true);
    }

    public Window? Owner => CurrentOwner();

    public async Task<bool> ShowAddDevicesAsync(AddDevicesViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new AddDevicesWindow { DataContext = page };
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowDeviceLoginAsync(Devices.DeviceLoginViewModel login)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Devices.DeviceLoginWindow();
        window.Attach(login);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowDeviceSetPasswordAsync(Devices.DeviceSetPasswordViewModel setPassword)
    {
        ArgumentNullException.ThrowIfNull(setPassword);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Devices.DeviceSetPasswordWindow();
        window.Attach(setPassword);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowDeviceTagsAsync(Tags.DeviceTagsViewModel tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Tags.DeviceTagsWindow();
        window.Attach(tags);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<string?> ShowTaskPluginDialogAsync(ITaskPluginDialog dialog, IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (CurrentOwner() is not { } owner)
        {
            return null;
        }

        return await dialog.ShowAsync(new TaskDialogContext(api, dialog.PluginId), devices, owner).ConfigureAwait(true);
    }

    public async Task ShowTaskDetailsAsync(TaskDetailsViewModel details)
    {
        if (CurrentOwner() is not { } owner)
        {
            return;
        }

        var window = new TaskDetailsWindow { DataContext = details };
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    public async Task<bool> OpenAsync(Uri uri)
    {
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        return await owner.Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
    }

    public async Task<bool> SetTextAsync(string text)
    {
        if (CurrentOwner()?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(true);
        return true;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedFileName, FileType type, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(content);
        if (CurrentOwner()?.StorageProvider is not { } storage)
        {
            return null;
        }

        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = type.Extension,
            FileTypeChoices = [Filter(type)],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        if (file is null)
        {
            return null;
        }

        await using (Stream stream = await file.OpenWriteAsync().ConfigureAwait(true))
        {
            stream.SetLength(0);
            await stream.WriteAsync(content).ConfigureAwait(true);
        }

        return file.Name;
    }

    public async Task<PickedFile?> OpenFileAsync(string title, FileType type, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (CurrentOwner()?.StorageProvider is not { } storage)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [Filter(type), FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }

        await using Stream stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        byte[] buffer = new byte[maxBytes + 1];
        int length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(true)) > 0)
        {
            length += read;
        }

        return length > maxBytes ? new PickedFile(files[0].Name, buffer[..maxBytes], true) : new PickedFile(files[0].Name, buffer[..length], false);
    }

    private static FilePickerFileType Filter(FileType type) =>
        new($"{type.Name} (*.{type.Extension})") { Patterns = ["*." + type.Extension], MimeTypes = [type.MimeType] };

    /// <summary>The active window (so dialogs opened from dialogs stack correctly), else the main window.</summary>
    private static Window? CurrentOwner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        }

        return null;
    }
}
