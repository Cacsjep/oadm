using Avalonia.Controls;

namespace Oadm.Plugins.DhcpServer.Client;

/// <summary>Static lease dialog window. View only: wiring to the view model.</summary>
public partial class StaticLeaseWindow : Window
{
    public StaticLeaseWindow()
    {
        InitializeComponent();
    }

    public void Attach(StaticLeaseDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
