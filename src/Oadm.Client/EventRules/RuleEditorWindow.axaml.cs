using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Client.EventRules;

/// <summary>The event rule dialog (see <see cref="RuleEditorViewModel"/>). View only: wiring and focus.</summary>
public partial class RuleEditorWindow : Window
{
    public RuleEditorWindow()
    {
        InitializeComponent();
    }

    public void Attach(RuleEditorViewModel viewModel)
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
