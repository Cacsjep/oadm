using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Data;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>The form of the policy editor (on top of the shared <see cref="DevicePickerWindow"/>).</summary>
public partial class PolicyFormView : UserControl
{
    public PolicyFormView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The policy editor: the shared device picker in its group mode with this form on top (the device part only for kinds
    /// with devices); Save runs <see cref="PolicyEditorViewModel.SaveAsync"/> and closes the window only when the server stored
    /// the policy. A revoke policy asks first, owned by the editor window (red Revoke).
    /// </summary>
    public static DevicePickerWindow CreateWindow(PolicyEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var form = new PolicyFormView { DataContext = editor };
        var window = new DevicePickerWindow(editor.Picker, form) { Heading = editor.Title, Accept = editor.SaveAsync };
        window.AcceptButton.Content = "Save";
        window.AcceptButton.Bind(IsEnabledProperty, new Binding(nameof(PolicyEditorViewModel.CanSave)) { Source = editor });
        window.AcceptButton.Bind(ToolTip.TipProperty, new Binding(nameof(PolicyEditorViewModel.SaveBlockedReason)) { Source = editor });
        ToolTip.SetShowOnDisabled(window.AcceptButton, true);
        window.IsPickerVisible = editor.HasDevices;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PolicyEditorViewModel.HasDevices))
            {
                window.IsPickerVisible = editor.HasDevices;
            }
        }

        editor.PropertyChanged += OnChanged;
        window.Closed += (_, _) => editor.PropertyChanged -= OnChanged;
        editor.ConfirmRevoke = (title, message) =>
            new MessageWindow { Heading = title, Message = message, ConfirmText = "Revoke", CancelText = "Cancel", IsDanger = true }.ShowDialog<bool>(window);
        window.Opened += (_, _) => form.NameBox.Focus();
        return window;
    }
}
