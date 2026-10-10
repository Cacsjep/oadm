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
        viewModel.CloseRequested += (_, ok) => Close(ok);
        if (viewModel.IsNew)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus());
        }
    }
}
