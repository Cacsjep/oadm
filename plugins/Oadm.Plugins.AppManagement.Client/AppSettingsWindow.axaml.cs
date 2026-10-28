using Avalonia.Controls;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>The Settings window. View only: closing.</summary>
public partial class AppSettingsWindow : Window
{
    public AppSettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AppSettingsViewModel vm)
        {
            vm.CloseRequested += (_, _) => Close();
        }
    }
}
