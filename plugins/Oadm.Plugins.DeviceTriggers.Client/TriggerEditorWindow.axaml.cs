using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Plugins.DeviceTriggers.Client;

/// <summary>The trigger dialog (see <see cref="TriggerEditorViewModel"/>). View only: wiring and focus.</summary>
public partial class TriggerEditorWindow : Window
{
    public TriggerEditorWindow()
    {
        InitializeComponent();
    }

    public void Attach(TriggerEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.ShowPicker = async picker =>
        {
            var window = new Oadm.Sdk.Client.Controls.DevicePickerWindow(picker) { Heading = "Devices of the trigger" };
            return await window.ShowDialog<bool>(this).ConfigureAwait(true);
        };
        viewModel.CloseRequested += (_, ok) => Close(ok);
        Opened += (_, _) =>
        {
            if (viewModel.IsNew)
            {
                Dispatcher.UIThread.Post(() => NameBox.Focus());
            }

            _ = viewModel.StartAsync(); // editing: read the devices' events live
        };
    }
}
