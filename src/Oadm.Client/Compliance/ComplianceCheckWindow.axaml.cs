using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Client.Compliance;

/// <summary>The compliance check dialog (see <see cref="ComplianceCheckEditorViewModel"/>). View only: wiring and focus.</summary>
public partial class ComplianceCheckWindow : Window
{
    public ComplianceCheckWindow()
    {
        InitializeComponent();
    }

    public void Attach(ComplianceCheckEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.ShowPicker = async picker =>
        {
            var window = new Oadm.Sdk.Client.Controls.DevicePickerWindow(picker);
            return await window.ShowDialog<bool>(this).ConfigureAwait(true);
        };
        viewModel.CloseRequested += (_, ok) => Close(ok);
        if (viewModel.IsNew)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus());
        }
    }
}
