using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Client.EventRules;

/// <summary>The recipient dialog (see <see cref="RecipientEditorViewModel"/>). View only: wiring and focus.</summary>
public partial class RecipientEditorWindow : Window
{
    public RecipientEditorWindow()
    {
        InitializeComponent();
    }

    public void Attach(RecipientEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
        var open = new CancellationTokenSource();
        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => NameBox.Focus());
            _ = viewModel.LoadCertificatesAsync(open.Token);
        };
        Closed += (_, _) =>
        {
            open.Cancel();
            open.Dispose();
        };
    }
}
