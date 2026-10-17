using Avalonia.Controls;

namespace Oadm.Client.Settings;

/// <summary>The "License of X" dialog of the Plugins page (see <see cref="PluginLicenseViewModel"/>). View only: wiring to the view model.</summary>
public partial class PluginLicenseWindow : Window
{
    public PluginLicenseWindow()
    {
        InitializeComponent();
    }

    public void Attach(PluginLicenseViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        LicenseBox.Focus();
    }
}
