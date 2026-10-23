using Avalonia.Controls;
using Avalonia.Data;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Settings;

/// <summary>Name and description of the device group editor (on top of the shared <see cref="DevicePickerWindow"/>).</summary>
public partial class DeviceGroupFormView : UserControl
{
    public DeviceGroupFormView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The group editor: the shared device picker in its group mode with this form on top; Save runs
    /// <see cref="DeviceGroupEditorViewModel.SaveAsync"/> and closes the window only when the server stored the group.
    /// </summary>
    public static DevicePickerWindow CreateWindow(DeviceGroupEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var form = new DeviceGroupFormView { DataContext = editor };
        var window = new DevicePickerWindow(editor.Picker, form) { Heading = editor.Title, Accept = editor.SaveAsync };
        window.AcceptButton.Content = "Save";
        window.AcceptButton.Bind(IsEnabledProperty, new Binding(nameof(DeviceGroupEditorViewModel.CanSave)) { Source = editor });
        window.AcceptButton.Bind(ToolTip.TipProperty, new Binding(nameof(DeviceGroupEditorViewModel.SaveBlockedReason)) { Source = editor });
        ToolTip.SetShowOnDisabled(window.AcceptButton, true);
        window.Opened += (_, _) => form.NameBox.Focus();
        return window;
    }
}
