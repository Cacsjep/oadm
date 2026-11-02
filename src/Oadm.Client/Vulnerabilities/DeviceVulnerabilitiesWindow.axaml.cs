using Avalonia.Controls;

namespace Oadm.Client.Vulnerabilities;

/// <summary>The "Vulnerabilities" window of one device (see <see cref="DeviceVulnerabilitiesViewModel"/>). View only: wiring.</summary>
public partial class DeviceVulnerabilitiesWindow : Window
{
    public DeviceVulnerabilitiesWindow()
    {
        InitializeComponent();
    }

    public void Attach(DeviceVulnerabilitiesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        // A grid column is not in the tree: no binding to the view model.
        foreach (DataGridColumn column in VulnerabilityGrid.Columns.Where(c => Equals(c.Tag, "devices")))
        {
            column.IsVisible = viewModel.ShowDevices;
        }

        viewModel.CloseRequested += (_, _) => Close();
    }
}
