using Avalonia.Controls;

namespace Oadm.Plugins.SystemReport.Client;

/// <summary>The progress dialog. View only: wiring to the view model.</summary>
public partial class SystemReportWindow : Window
{
    public SystemReportWindow()
    {
        InitializeComponent();
    }

    public void Attach(SystemReportViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        // Closing with the caption button while the job runs cancels it (the server deletes its files).
        Closing += (_, _) =>
        {
            if (viewModel.IsBusy)
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }
}
