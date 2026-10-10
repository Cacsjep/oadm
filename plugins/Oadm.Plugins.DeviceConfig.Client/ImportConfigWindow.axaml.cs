using Avalonia.Controls;

namespace Oadm.Plugins.DeviceConfig.Client;

/// <summary>The import dialog. View only: wiring to the view model.</summary>
public partial class ImportConfigWindow : Window
{
    public ImportConfigWindow()
    {
        InitializeComponent();
    }

    public void Attach(ImportConfigViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, payload) => Close(payload);
    }
}
