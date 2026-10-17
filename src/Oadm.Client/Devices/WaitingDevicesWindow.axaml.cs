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
        viewModel.CloseRequested += (_, _) => Close();
        EventHandler roundEnded = (_, _) => viewModel.OnRoundEnded();
        PropertyChangedEventHandler stateChanged = (_, _) => viewModel.OnStateChanged();
        viewModel.State.RoundEnded += roundEnded;
        viewModel.State.PropertyChanged += stateChanged;
        Opened += (_, _) => _ = viewModel.LoadAsync();
        Closed += (_, _) =>
        {
            viewModel.State.RoundEnded -= roundEnded;
            viewModel.State.PropertyChanged -= stateChanged;
        };
    }
}
