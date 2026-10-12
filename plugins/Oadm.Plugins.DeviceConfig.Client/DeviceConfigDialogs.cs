using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig.Client;

/// <summary>Platform parts of the export (save dialog, progress window); tests replace them.</summary>
public interface IExportConfigUi
{
    /// <summary>Asks where to save; null = cancelled.</summary>
    Task<ExportTarget?> PickTargetAsync(string suggestedFileName, bool zip);

    /// <summary>Shows the progress window, runs <paramref name="run"/> and returns when the window closed.</summary>
    Task ShowAsync(ExportConfigViewModel dialog, Func<Task> run);
}

/// <summary>"Export configuration": save dialog first, then the progress window. Never starts a task.</summary>
public sealed class ExportConfigDialog : ITaskPluginDialog
{
    public string PluginId => DeviceConfigIds.Export;

    /// <summary>Replaces the save dialog and the window (tests).</summary>
    public Func<Window, IExportConfigUi>? Ui { get; init; }

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            return null;
        }

        var dialog = new ExportConfigViewModel((id, ct) => ctx.QueryAsync(id, DeviceConfigIds.ExportQuery, null, ct), devices);
        var ui = Ui?.Invoke(owner) ?? new AvaloniaUi(owner);
        var target = await ui.PickTargetAsync(dialog.SuggestedFileName, dialog.IsZip).ConfigureAwait(true);
        if (target is not null)
        {
            await ui.ShowAsync(dialog, () => dialog.RunAsync(target)).ConfigureAwait(true);
        }

        return null;
    }

    private sealed class AvaloniaUi(Window owner) : IExportConfigUi
    {
        private static readonly FilePickerFileType ZipFiles = new("ZIP archives (*.zip)") { Patterns = ["*.zip"], MimeTypes = ["application/zip"] };
        private static readonly FilePickerFileType JsonFiles = new("Configuration files (*.json)") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

        public async Task<ExportTarget?> PickTargetAsync(string suggestedFileName, bool zip)
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = DeviceConfigIds.ExportName,
                SuggestedFileName = suggestedFileName,
                DefaultExtension = zip ? "zip" : "json",
                FileTypeChoices = [zip ? ZipFiles : JsonFiles],
                ShowOverwritePrompt = true,
            }).ConfigureAwait(true);
            return file is null ? null : new ExportTarget(() => file.OpenWriteAsync(), file.TryGetLocalPath() ?? file.Name);
        }

        public async Task ShowAsync(ExportConfigViewModel dialog, Func<Task> run)
        {
            var window = new ExportConfigWindow();
            window.Attach(dialog);
            var closed = window.ShowDialog(owner);
            await run().ConfigureAwait(true);
            await closed.ConfigureAwait(true);
        }
    }
}

/// <summary>"Import configuration": file, settings areas, devices; uploads the file and returns the payload.</summary>
public sealed class ImportConfigDialog : ITaskPluginDialog
{
    public string PluginId => DeviceConfigIds.Import;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        var window = new ImportConfigWindow();
        window.Attach(new ImportConfigViewModel(
            devices,
            new StorageConfigFileSource(window),
            ctx.UploadAsync,
            (title, message, confirm) => MessageWindow.ConfirmAsync(window, title, message, confirm),
            ctx.Has));
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }
}

/// <summary>Open dialog of the window: a configuration file or the ZIP of an export.</summary>
public sealed class StorageConfigFileSource(TopLevel topLevel) : IConfigFileSource
{
    public async Task<string?> PickAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a configuration file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Configuration files (*.json, *.zip)") { Patterns = ["*.json", "*.zip"] },
                FilePickerFileTypes.All,
            ],
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
