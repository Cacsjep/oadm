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

public sealed class AvaloniaDialogService(IOadmApi api, Shell.UserSession? session = null, Devices.DeviceStore? store = null, Tasks.TaskStore? tasks = null) : IDialogService, IUrlLauncher, IClipboardService
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

    public async Task<bool> ShowImportHelpAsync(Discovery.ImportHelpViewModel help)
    {
        ArgumentNullException.ThrowIfNull(help);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Discovery.ImportHelpWindow();
        window.Attach(help);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowPluginLicenseAsync(Settings.PluginLicenseViewModel license)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Settings.PluginLicenseWindow();
        window.Attach(license);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowSiteFormAsync(Sites.SiteFormViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Sites.SiteFormWindow();
        window.Attach(form);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowRtspPortAsync(Devices.RtspPortViewModel rtspPort)
    {
        ArgumentNullException.ThrowIfNull(rtspPort);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Devices.RtspPortWindow();
        window.Attach(rtspPort);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task ShowUserFormAsync(Settings.UsersViewModel users)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (CurrentOwner() is not { } owner)
        {
            users.CloseForm();
            return;
        }

        var window = new Settings.UserFormWindow();
        window.Attach(users);
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowDeviceGroupEditorAsync(Settings.DeviceGroupEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        return await Settings.DeviceGroupFormView.CreateWindow(editor).ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task ShowRoleFormAsync(Settings.RolesViewModel roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (CurrentOwner() is not { } owner)
        {
            roles.CancelEditorCommand.Execute(null);
            return;
        }

        var window = new Settings.RoleFormWindow();
        window.Attach(roles);
        await window.ShowDialog(owner).ConfigureAwait(true);
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

    public async Task ShowWaitingDevicesAsync(Devices.WaitingDevicesViewModel waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        if (CurrentOwner() is not { } owner)
        {
            return;
        }

        var window = new Devices.WaitingDevicesWindow();
        window.Attach(waiting);
        await window.ShowDialog(owner).ConfigureAwait(true);
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

    public async Task<bool> ShowRuleEditorAsync(EventRules.RuleEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new EventRules.RuleEditorWindow();
        window.Attach(editor);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowComplianceCheckAsync(Compliance.ComplianceCheckEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Compliance.ComplianceCheckWindow();
        window.Attach(editor);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<TaskDialogResult?> ShowTaskPluginDialogAsync(ITaskPluginDialog dialog, IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (CurrentOwner() is not { } owner)
        {
            return null;
        }

        var context = new TaskDialogContext(api, dialog.PluginId, session is null ? null : session.Has, store is null ? null : () => [.. store.Devices], tasks,
            session is null ? null : () => session.HasAllDevices);
        return await dialog.ShowForDevicesAsync(context, devices, owner).ConfigureAwait(true);
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

    public async Task<SavedFile?> CreateFileAsync(string title, string suggestedFileName, FileType type)
    {
        ArgumentNullException.ThrowIfNull(type);
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

        Stream stream = await file.OpenWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        return new SavedFile(file.Name, stream)
        {
            Delete = async () =>
            {
                await stream.DisposeAsync().ConfigureAwait(true);
                await file.DeleteAsync().ConfigureAwait(true);
            },
        };
    }

    public async Task<string?> PickFilePathAsync(string title, FileType type)
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
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<bool> ShowBackupPasswordAsync(Backup.BackupPasswordViewModel password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Backup.BackupPasswordWindow();
        window.Attach(password);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ShowRestoreAsync(Backup.RestoreViewModel restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        if (CurrentOwner() is not { } owner)
        {
            return false;
        }

        var window = new Backup.RestoreWindow();
        window.Attach(restore);
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
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
        // Grows with the file instead of reserving the whole limit; reading stops one byte past it.
        using var content = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while (content.Length <= maxBytes && (read = await stream.ReadAsync(chunk).ConfigureAwait(true)) > 0)
        {
            content.Write(chunk, 0, read);
        }

        return content.Length > maxBytes ? new PickedFile(files[0].Name, [], true) : new PickedFile(files[0].Name, content.ToArray(), false);
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
