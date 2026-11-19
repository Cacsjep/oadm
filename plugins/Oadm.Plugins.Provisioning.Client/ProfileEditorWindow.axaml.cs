using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>The profile editor window. View only: wiring to the view model, the file picker and the device choice.</summary>
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

    public Task<Guid?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, Guid? selected) =>
        DevicePickerWindow.ShowOneAsync(this, devices, selected, heading: ProvisioningTexts.ChooseTemplateHeading);
}
