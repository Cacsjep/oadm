using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>Export dialog window. View only: wiring to the view model and the platform save dialog.</summary>
public partial class ExportReportWindow : Window, IReportSavePicker
{
    private static readonly FilePickerFileType PdfFiles = new("PDF documents (*.pdf)") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] };

    public ExportReportWindow()
    {
        InitializeComponent();
    }

    public void Attach(ExportReportViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.SavePicker = this;
        viewModel.CloseRequested += (_, path) => Close(path);
    }

    public async Task<ReportTarget?> PickAsync(string suggestedFileName, string? folder)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(folder))
        {
            start = await StorageProvider.TryGetFolderFromPathAsync(folder).ConfigureAwait(true);
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save maintenance report",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "pdf",
            FileTypeChoices = [PdfFiles],
            SuggestedStartLocation = start,
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        if (file is null)
        {
            return null;
        }

        var path = file.TryGetLocalPath();
        var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        return new ReportTarget(stream, path ?? file.Name, path is null ? null : Path.GetDirectoryName(path));
    }
}
