using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// The License Plate Center page. Layout only: the tabs, the dialogs and pickers over the main window (<see cref="IPageDialogs"/>);
/// the view model follows the server while the view is in the visual tree.
/// </summary>
public partial class LicensePlatesView : UserControl, IPageDialogs
{
    private LicensePlatesViewModel? _viewModel;

    public LicensePlatesView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var old = _viewModel;
        _viewModel = DataContext as LicensePlatesViewModel;
        if (ReferenceEquals(old, _viewModel))
        {
            return;
        }

        // The view owns its page's view model (LicensePlatesPage makes both): one that leaves the view is done for good.
        old?.Dispose();
        if (_viewModel is { } vm)
        {
            vm.Dialogs = this;
            if (TopLevel.GetTopLevel(this) is not null)
            {
                vm.Activate();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewModel?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    public Task<bool> EditPlateAsync(PlateEditorViewModel editor) => ShowAsync(new PlateEditorWindow { DataContext = editor }, h => editor.CloseRequested += h);

    public Task<bool> ImportAsync(ImportViewModel import) => ShowAsync(new ImportWindow { DataContext = import }, h => import.CloseRequested += h);

    public Task<bool> TagsAsync(TagsDialogViewModel tags) => ShowAsync(new TagsWindow { DataContext = tags }, h => tags.CloseRequested += h);

    public async Task<bool> SetupAsync(SetupViewModel setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        try
        {
            return await ShowAsync(new SetupWindow { DataContext = setup }, h => setup.CloseRequested += h).ConfigureAwait(true);
        }
        finally
        {
            setup.Dispose();
        }
    }

    public Task<bool> CopySettingsAsync(CopySettingsViewModel copy) => ShowAsync(new CopySettingsWindow { DataContext = copy }, h => copy.CloseRequested += h);

    public Task<bool> SettingsAsync(SettingsViewModel settings) => ShowAsync(new SettingsWindow { DataContext = settings }, h => settings.CloseRequested += h);

    public async Task<IReadOnlyList<Guid>?> PickCamerasAsync(IReadOnlyList<IDeviceInfo> devices, IReadOnlyList<Guid> selected, string heading) =>
        TopLevel.GetTopLevel(this) is Window owner ? await DevicePickerWindow.ShowAsync(owner, devices, selected, heading: heading).ConfigureAwait(true) : null;

    public async Task<bool> PickCameraChoiceAsync(PageDeviceChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return TopLevel.GetTopLevel(this) is Window owner && await choice.PickAsync(owner, PageTexts.ChooseCameras).ConfigureAwait(true);
    }

    public async Task<string?> OpenCsvAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = PageTexts.ImportTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.txt"] }],
        }).ConfigureAwait(true);
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<Stream?> SaveFileAsync(string title, string suggestedName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var extension = Path.GetExtension(suggestedName).TrimStart('.');
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }],
        }).ConfigureAwait(true);
        return file is null ? null : await file.OpenWriteAsync().ConfigureAwait(true);
    }

    private async Task<bool> ShowAsync(Window window, Action<EventHandler<bool>> subscribe)
    {
        var result = false;
        subscribe((_, ok) =>
        {
            result = ok;
            window.Close();
        });
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }

        return result;
    }
}
