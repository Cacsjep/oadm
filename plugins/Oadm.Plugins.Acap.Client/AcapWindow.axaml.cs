using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oadm.Plugins.Acap.Client;

/// <summary>Applications (ACAP) dialog window. View only: wiring to the view model and the platform file picker.</summary>
public partial class AcapWindow : Window, IEapFilePicker, ILicenseKeyPicker
{
    private static readonly FilePickerFileType EapFiles = new(EapPackageFiles.PickerFilter) { Patterns = EapPackageFiles.PickerPatterns };
    private static readonly FilePickerFileType KeyFiles = new("License keys (*.key, *.xml, *.zip)") { Patterns = ["*.key", "*.xml", "*.zip"] };

    public AcapWindow()
    {
        InitializeComponent();
    }

    /// <summary>Attaches the view model; the window closes with the payload when the view model completes.</summary>
    public void Attach(AcapDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.UseKeyPicker(this);
        viewModel.CloseRequested += (_, payload) => Close(payload);
        Closed += (_, _) => viewModel.Dispose();
    }

    public async Task<string?> PickEapAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an ACAP package or a ZIP file",
            AllowMultiple = false,
            FileTypeFilter = [EapFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <summary>One .eap, one per architecture of the same application, ZIPs of them, or both.</summary>
    public async Task<IReadOnlyList<string>> PickEapFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EapPackageFiles.PickerTitle,
            AllowMultiple = true,
            FileTypeFilter = [EapFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<IReadOnlyList<string>> PickLicenseKeysAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select license keys",
            AllowMultiple = true,
            FileTypeFilter = [KeyFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
