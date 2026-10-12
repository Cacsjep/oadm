using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.EdgeRecordingExport.Client;

/// <summary>The page view: hands the view model the device picker and the folder picker of its window.</summary>
public partial class EdgeRecordingView : UserControl
{
    public EdgeRecordingView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is EdgeRecordingViewModel vm)
        {
            vm.PickCameras = async (devices, initial) => TopLevel.GetTopLevel(this) is Window owner
                ? await DevicePickerWindow.ShowChoiceAsync(owner, devices, initial, new DevicePickerOptions { AllowAll = true, AllowTags = true }, "Choose cameras").ConfigureAwait(true)
                : null;
            vm.PickFolder = PickFolderAsync;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the folder for the recordings", AllowMultiple = false }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
