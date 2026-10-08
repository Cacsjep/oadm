using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>"Install certificates" window. View only: wiring to the view model and the platform file picker.</summary>
public partial class InstallCertificatesWindow : Window, ICertificateFilePicker
{
    private static readonly FilePickerFileType Pkcs12Files = new("Certificates with key (*.pfx, *.p12)") { Patterns = ["*.pfx", "*.p12"], MimeTypes = ["application/x-pkcs12"] };

    public InstallCertificatesWindow()
    {
        InitializeComponent();
    }

    public void Attach(InstallCertificatesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Picker = this;
        viewModel.Confirm = (title, message, confirm) => MessageWindow.ConfirmAsync(this, title, message, confirm);
        viewModel.CloseRequested += (_, payload) => Close(payload);
    }

    public async Task<IReadOnlyList<string>> PickAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select certificate files",
            AllowMultiple = true,
            FileTypeFilter = [Pkcs12Files, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
