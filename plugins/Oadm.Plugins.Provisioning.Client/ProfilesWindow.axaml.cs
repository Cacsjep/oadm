using Avalonia.Controls;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>The Manage profiles window. View only: wiring to the view model, a double click edits, the editor over it.</summary>
public partial class ProfilesWindow : Window, IProfilesUi
{
    public ProfilesWindow()
    {
        InitializeComponent();
        ProfileGrid.DoubleTapped += async (_, _) =>
        {
            if (DataContext is ProfilesDialogViewModel vm && vm.EditCommand.CanExecute(null))
            {
                await vm.EditCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        };
    }

    public void Attach(ProfilesDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Ui = this;
        viewModel.CloseRequested += (_, _) => Close();
    }

    public async Task ShowProfileEditorAsync(ProfileEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var window = new ProfileEditorWindow();
        window.Attach(editor);
        _ = editor.LoadAsync();
        await window.ShowDialog(this).ConfigureAwait(true);
    }
}
