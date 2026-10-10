using Avalonia.Controls;

namespace Oadm.Plugins.DeviceConfig.Client;

/// <summary>The export progress window. View only: wiring to the view model.</summary>
public partial class ExportConfigWindow : Window
{
    public ExportConfigWindow()
    {
        InitializeComponent();
    }

    public void Attach(ExportConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        // Closing with the caption button while the export runs cancels it.
        Closing += (_, _) =>
        {
            if (viewModel.IsBusy)
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }
}
