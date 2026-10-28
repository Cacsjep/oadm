using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>The Install applications window. View only: the file picker and closing.</summary>
public partial class InstallAppsWindow : Window, IAppFilePicker
{
    public InstallAppsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is InstallAppsViewModel vm)
        {
            vm.Picker = this;
            vm.CloseRequested += (_, started) => Close(started);
        }
    }

    public Task<IReadOnlyList<string>> PickEapFilesAsync() => AppFilePicker.PickAsync(this, "Select ACAP packages, one per architecture", AppFilePicker.EapFiles);

    public Task<IReadOnlyList<string>> PickLicenseKeysAsync() => AppFilePicker.PickAsync(this, "Select license keys", AppFilePicker.KeyFiles);
}

/// <summary>The platform file picker of the page's dialogs (several files at once).</summary>
internal static class AppFilePicker
{
    public static readonly FilePickerFileType EapFiles = new("ACAP packages (*.eap)") { Patterns = ["*.eap"] };
    public static readonly FilePickerFileType KeyFiles = new("License keys (*.key, *.xml, *.zip)") { Patterns = ["*.key", "*.xml", "*.zip"] };

    public static async Task<IReadOnlyList<string>> PickAsync(TopLevel owner, string title, FilePickerFileType type)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [type, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
