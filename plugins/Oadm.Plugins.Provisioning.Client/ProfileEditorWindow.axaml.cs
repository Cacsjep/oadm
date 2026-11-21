using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>The profile editor window. View only: wiring to the view model and the file picker.</summary>
public partial class ProfileEditorWindow : Window, IProfileEditorUi
{
    public ProfileEditorWindow()
    {
        InitializeComponent();
    }

    public void Attach(ProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Ui = this;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.ForgetRevealedPasswords(); // the title bar's close too
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, IReadOnlyList<string> patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = [.. patterns] }, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
