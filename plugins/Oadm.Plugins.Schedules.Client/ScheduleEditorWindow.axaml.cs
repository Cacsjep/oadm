using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Schedules.Client;

/// <summary>The schedule wizard window. View only: wiring to the view model, the device picker, the file picker, focus.</summary>
public partial class ScheduleEditorWindow : Window, IScheduleEditorUi
{
    public ScheduleEditorWindow()
    {
        InitializeComponent();
    }

    public void Attach(ScheduleEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Ui = this;
        viewModel.CloseRequested += (_, saved) => Close(saved);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus());
    }

    public async Task<bool> ChooseDevicesAsync(DevicePickerViewModel picker)
    {
        var window = new DevicePickerWindow(picker) { Heading = SchedulesTexts.PickerHeading };
        return await window.ShowDialog<bool>(this).ConfigureAwait(true);
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
