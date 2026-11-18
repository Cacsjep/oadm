using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>The profile editor window. View only: wiring to the view model and the Add node menu of the node kinds.</summary>
public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow()
    {
        InitializeComponent();
        AddNodeButton.Click += (_, _) =>
        {
            if (DataContext is ProfileEditorViewModel vm && vm.AddableKinds.Count > 0)
            {
                RowActions.Show(AddNodeButton, [.. vm.AddableKinds.Select(k => new IssuedCertificateAction(k.Name, vm.AddNodeCommand, k))]);
            }
        };
    }

    public void Attach(ProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
