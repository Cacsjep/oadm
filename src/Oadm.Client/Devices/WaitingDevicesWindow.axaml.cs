using System.ComponentModel;

using Avalonia.Controls;

namespace Oadm.Client.Devices;

/// <summary>The "Waiting devices" dialog (see <see cref="WaitingDevicesViewModel"/>). View only: wiring.</summary>
public partial class WaitingDevicesWindow : Window
{
    public WaitingDevicesWindow()
    {
        InitializeComponent();
    }

    public void Attach(WaitingDevicesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        // A grid column is not in the tree: no binding to the view model.
        foreach (DataGridColumn column in WaitingGrid.Columns.Where(c => Equals(c.Header, "Site")))
        {
            column.IsVisible = viewModel.ShowSiteColumn;
        }

        viewModel.CloseRequested += (_, _) => Close();
        EventHandler roundEnded = (_, _) => viewModel.OnRoundEnded();
        EventHandler roundStarted = (_, _) => viewModel.OnRoundStarted();
        viewModel.State.RoundStarted += roundStarted;
        PropertyChangedEventHandler stateChanged = (_, _) => viewModel.OnStateChanged();
        viewModel.State.RoundEnded += roundEnded;
        viewModel.State.PropertyChanged += stateChanged;
        Opened += (_, _) => _ = viewModel.LoadAsync();
        Closed += (_, _) =>
        {
            viewModel.State.RoundEnded -= roundEnded;
            viewModel.State.RoundStarted -= roundStarted;
            viewModel.State.PropertyChanged -= stateChanged;
        };
    }
}
