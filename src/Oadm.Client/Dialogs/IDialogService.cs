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

    /// <summary>Shows the "Set password" dialog of factory-default devices; true when all took the password.</summary>
    Task<bool> ShowDeviceSetPasswordAsync(Oadm.Client.Devices.DeviceSetPasswordViewModel setPassword);
    /// <summary>The Add user, Edit user or Change password dialog of the Users page (<see cref="Oadm.Client.Settings.UsersViewModel.Form"/>).</summary>
    Task ShowUserFormAsync(Oadm.Client.Settings.UsersViewModel users);

    /// <summary>The dialog for a new role, a copy or a rename (<see cref="Oadm.Client.Settings.RolesViewModel.IsEditorOpen"/>).</summary>
    Task ShowRoleFormAsync(Oadm.Client.Settings.RolesViewModel roles);

    /// <summary>The "RTSP port in OADM" dialog of the context menu's OADM submenu; true when saved.</summary>
    Task<bool> ShowRtspPortAsync(Oadm.Client.Devices.RtspPortViewModel rtspPort);

    /// <summary>"License of X" of the Plugins page; true when a license was stored.</summary>
    Task<bool> ShowPluginLicenseAsync(Oadm.Client.Settings.PluginLicenseViewModel license);

    /// <summary>The "Waiting devices" dialog of the Devices page (the import's waiting list).</summary>
    Task ShowWaitingDevicesAsync(Oadm.Client.Devices.WaitingDevicesViewModel waiting);

    /// <summary>Shows the Tags dialog of the selected devices; true when tags were applied.</summary>
    Task<bool> ShowDeviceTagsAsync(Oadm.Client.Tags.DeviceTagsViewModel tags);

    /// <summary>The event rule dialog; true when the rule was saved (<see cref="Oadm.Client.EventRules.RuleEditorViewModel.Saved"/>).</summary>
    Task<bool> ShowRuleEditorAsync(Oadm.Client.EventRules.RuleEditorViewModel editor);

    /// <summary>The compliance check dialog; true when saved.</summary>
    Task<bool> ShowComplianceCheckAsync(Oadm.Client.Compliance.ComplianceCheckEditorViewModel editor) => Task.FromResult(false);

    /// <summary>A dialog of the Sites page (Bind, Rename, Replace with, Remove site); true when done.</summary>
    Task<bool> ShowSiteFormAsync(Oadm.Client.Sites.SiteFormViewModel form) => Task.FromResult(false);

    /// <summary>The window dialogs open on (active window, else the main window); null without a desktop.</summary>
    Avalonia.Controls.Window? Owner { get; }

    /// <summary>Shows a client plugin dialog; returns the payload JSON, or null when cancelled.</summary>
    Task<string?> ShowTaskPluginDialogAsync(ITaskPluginDialog dialog, IReadOnlyList<IDeviceInfo> devices);

    Task ShowTaskDetailsAsync(TaskDetailsViewModel details);

    /// <summary>Save file picker (overwrite asked); writes <paramref name="content"/>. Returns the file name, null when cancelled.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedFileName, FileType type, byte[] content);

    /// <summary>"Import from file" before its file picker: the columns and an example; true when the user chose to pick a file.</summary>
    Task<bool> ShowImportHelpAsync(Oadm.Client.Discovery.ImportHelpViewModel help);

    /// <summary>Open file picker; reads at most <paramref name="maxBytes"/> + 1 bytes (more = <see cref="PickedFile.IsTooLarge"/>). Null when cancelled.</summary>
    Task<PickedFile?> OpenFileAsync(string title, FileType type, int maxBytes);

    /// <summary>Save file picker (overwrite asked) for a large file: an empty stream to write (the caller disposes it). Null when cancelled.</summary>
    Task<SavedFile?> CreateFileAsync(string title, string suggestedFileName, FileType type);

    /// <summary>Open file picker that returns the local path (the file is uploaded from there). Null when cancelled.</summary>
    Task<string?> PickFilePathAsync(string title, FileType type);

    /// <summary>The password of "Back up now"; true when confirmed.</summary>
    Task<bool> ShowBackupPasswordAsync(Oadm.Client.Backup.BackupPasswordViewModel password);

    /// <summary>The restore dialog; true when the restore started (the server restarts).</summary>
    Task<bool> ShowRestoreAsync(Oadm.Client.Backup.RestoreViewModel restore);
}

/// <summary>A file chosen in the save picker: its name and an empty stream to write; <see cref="Delete"/> removes it again (a failed save).</summary>
public sealed record SavedFile(string Name, Stream Content)
{
    public Func<Task>? Delete { get; init; }
}

/// <summary>A file type of the pickers: "CSV file", extension "csv".</summary>
public sealed record FileType(string Name, string Extension, string MimeType);

/// <summary>A file chosen in the open file picker.</summary>
/// <param name="Name">File name without folder.</param>
/// <param name="Content">The whole file; empty when it is too large.</param>
/// <param name="IsTooLarge">The file is larger than the limit; nothing was kept.</param>
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
