using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Firmware.Client;

/// <summary>Client part of "Upgrade firmware...": shows <see cref="FirmwareDialogWindow"/> and returns the payload.</summary>
public sealed class FirmwareTaskDialog : ITaskPluginDialog
{
    public string PluginId => FirmwareTaskPluginIds.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);

        var window = new FirmwareDialogWindow();
        using var vm = new FirmwareDialogViewModel(ctx, devices, new StorageFirmwareFileSource(window));
        window.DataContext = vm;
        using var cts = new CancellationTokenSource();
        window.Closed += (_, _) => cts.Cancel();
        _ = LoadStatusSafeAsync(vm, cts.Token);
        return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
    }

    private static async Task LoadStatusSafeAsync(FirmwareDialogViewModel vm, CancellationToken ct)
    {
        try
        {
            await vm.LoadStatusAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // dialog closed
        }
    }
}

/// <summary>File picker and size reader backed by the window's storage provider and the local file system.</summary>
public sealed class StorageFirmwareFileSource(TopLevel topLevel) : IFirmwareFileSource
{
    public async Task<string?> PickAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose AXIS OS firmware",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("AXIS OS firmware (*.bin)") { Patterns = ["*.bin"] },
                FilePickerFileTypes.All,
            ],
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public Task<long> GetSizeAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The file does not exist.", path);
        }

        return Task.FromResult(info.Length);
    }
}
