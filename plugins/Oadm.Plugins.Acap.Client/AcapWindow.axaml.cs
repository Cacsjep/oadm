using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oadm.Plugins.Acap.Client;

/// <summary>Applications (ACAP) dialog window. View only: wiring to the view model and the platform file picker.</summary>
public partial class AcapWindow : Window, IEapFilePicker
{
    private static readonly FilePickerFileType EapFiles = new("ACAP packages (*.eap)") { Patterns = ["*.eap"] };

    public AcapWindow()
    {
        InitializeComponent();
    }

    /// <summary>Attaches the view model; the window closes with the payload when the view model completes.</summary>
    public void Attach(AcapDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, payload) => Close(payload);
    }

    public async Task<string?> PickEapAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an ACAP package",
            AllowMultiple = false,
            FileTypeFilter = [EapFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
