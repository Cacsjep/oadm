using System.Text;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig.Client.Configure;

/// <summary>"Configure devices": the dialog returns the payload; the host starts one task per selected device.</summary>
public sealed class ConfigureDevicesDialog : ITaskPluginDialog
{
    public string PluginId => ConfigureIds.Configure;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            return null;
        }

        var window = new ConfigureDevicesWindow();
        var all = ctx.AllDevices.Count > 0 ? ctx.AllDevices : devices;
        var viewModel = new ConfigureDevicesViewModel(
            devices,
            all,
            (id, method, ct) => ctx.QueryAsync(id, method, null, ct),
            new WindowConfigureUi(window),
            ctx.Has(PermissionKey(ConfigurePermissions.Dangerous)));
        window.Attach(viewModel);
        window.Opened += async (_, _) => await viewModel.InitializeAsync().ConfigureAwait(true); // never throws: errors show in the dialog
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }

    /// <summary>"task:oadm.device-config.configure:dangerous".</summary>
    public static string PermissionKey(string key) => $"task:{ConfigureIds.Configure}:{key}";
}

/// <summary>"Create configuration file": reads one device and saves an ADM file. Never starts a task.</summary>
public sealed class CreateConfigFileDialog : ITaskPluginDialog
{
    public string PluginId => ConfigureIds.Create;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count != 1)
        {
            await MessageWindow.ShowMessageAsync(owner, ConfigureIds.CreateName, "Select one device to create the file from.").ConfigureAwait(true);
            return null;
        }

        var device = devices[0];
        var window = new CreateConfigFileWindow();
        var viewModel = new CreateConfigFileViewModel(device, (method, ct) => ctx.QueryAsync(device.Id, method, null, ct), new WindowConfigureUi(window));
        window.Attach(viewModel);
        window.Opened += async (_, _) => await viewModel.InitializeAsync().ConfigureAwait(true); // never throws: errors show in the dialog
        await window.ShowDialog(owner).ConfigureAwait(true);
        return null;
    }
}

/// <summary>The pickers, confirmation and save of the configure dialogs, over their window.</summary>
public sealed class WindowConfigureUi(Window window) : IConfigureUi
{
    private static readonly FilePickerFileType AdmFiles = new("Configuration files (*.cfg)") { Patterns = ["*.cfg"] };

    public async Task<IDeviceInfo?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, IDeviceInfo? current)
    {
        var id = await DevicePickerWindow.ShowOneAsync(window, devices, current?.Id,
            d => d.Status == DeviceStatus.Ok ? null : DeviceMessages.StatusText(d.Status), "Use configuration from").ConfigureAwait(true);
        return id is { } chosen ? devices.FirstOrDefault(d => d.Id == chosen) : null;
    }

    public async Task<PickedFile?> PickFileAsync()
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a configuration file",
            AllowMultiple = false,
            FileTypeFilter = [AdmFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }

        var file = files[0];
        var properties = await file.GetBasicPropertiesAsync().ConfigureAwait(true);
        if (properties.Size > (ulong)AdmConfigFile.MaxFileBytes)
        {
            return new PickedFile(file.Name, string.Empty) { TooLarge = true };
        }

        // The size may be unknown: never read more than the limit.
        await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[AdmConfigFile.MaxFileBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await reader.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(true)) > 0)
        {
            read += n;
        }

        return read > AdmConfigFile.MaxFileBytes
            ? new PickedFile(file.Name, string.Empty) { TooLarge = true }
            : new PickedFile(file.Name, new string(buffer, 0, read));
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirm) => MessageWindow.ConfirmAsync(window, title, message, confirm);

    public async Task<bool> SaveFileAsync(string suggestedName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = ConfigureIds.CreateName,
            SuggestedFileName = suggestedName,
            DefaultExtension = AdmConfigFile.Extension,
            FileTypeChoices = [AdmFiles],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        await stream.WriteAsync(content).ConfigureAwait(true);
        return true;
    }
}
