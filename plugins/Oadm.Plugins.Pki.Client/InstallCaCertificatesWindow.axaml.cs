using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>"Install CA certificates" window. View only: wiring to the view model and the platform file picker.</summary>
public partial class InstallCaCertificatesWindow : Window, ICertificateFilePicker
{
    private static readonly FilePickerFileType CaFiles = new("CA certificates (*.crt, *.pem, *.cer, *.der)")
    {
        Patterns = ["*.crt", "*.pem", "*.cer", "*.der"],
        MimeTypes = ["application/x-x509-ca-cert", "application/pkix-cert", "application/x-pem-file"],
    };

    public InstallCaCertificatesWindow()
    {
        InitializeComponent();
    }

    public void Attach(InstallCaCertificatesViewModel viewModel)
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
            Title = "Select CA certificate files",
            AllowMultiple = true,
            FileTypeFilter = [CaFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
