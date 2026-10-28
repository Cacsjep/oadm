using Avalonia.Controls;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>The Install licenses window. View only: the file picker and closing.</summary>
public partial class InstallLicensesWindow : Window, IAppFilePicker
{
    public InstallLicensesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is InstallLicensesViewModel vm)
        {
            vm.Picker = this;
            vm.CloseRequested += (_, started) => Close(started);
        }
    }

    public Task<IReadOnlyList<string>> PickEapFilesAsync() => AppFilePicker.PickAsync(this, "Select ACAP packages, one per architecture", AppFilePicker.EapFiles);

    public Task<IReadOnlyList<string>> PickLicenseKeysAsync() => AppFilePicker.PickAsync(this, "Select license keys", AppFilePicker.KeyFiles);
}
