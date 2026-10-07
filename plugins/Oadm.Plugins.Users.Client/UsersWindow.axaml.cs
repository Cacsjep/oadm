using System.ComponentModel;

using Avalonia.Controls;

namespace Oadm.Plugins.Users.Client;

/// <summary>
/// The "Users" dialog. Closes with the payload the view model hands over (null = cancelled). Forwards the
/// selection of the Existing users grid to the view model (a DataGrid selection with several rows cannot be
/// bound) and switches the grid to multi-select in Remove mode.
/// </summary>
public partial class UsersWindow : Window
{
    private UsersDialogViewModel? _vm;

    public UsersWindow()
    {
        InitializeComponent();
        UsersGrid.SelectionChanged += (_, _) => ForwardSelection();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as UsersDialogViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += (_, payload) => Close(payload);
            _vm.PropertyChanged += OnViewModelChanged;
            ApplyMode();
        }

        base.OnDataContextChanged(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsersDialogViewModel.Mode))
        {
            ApplyMode();
        }
    }

    private void ApplyMode()
    {
        if (_vm is null)
        {
            return;
        }

        UsersGrid.SelectionMode = _vm.MultiSelect ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
        if (!_vm.MultiSelect && UsersGrid.SelectedItems.Count > 1)
        {
            var last = UsersGrid.SelectedItems[UsersGrid.SelectedItems.Count - 1];
            UsersGrid.SelectedItems.Clear();
            UsersGrid.SelectedItem = last;
        }

        ForwardSelection();
    }

    private void ForwardSelection()
    {
        if (_vm is null)
        {
            return;
        }

        // Protected rows are never part of a Remove selection (select all included).
        foreach (var row in UsersGrid.SelectedItems.OfType<ExistingUserRow>().Where(r => !r.IsSelectable).ToList())
        {
            UsersGrid.SelectedItems.Remove(row);
        }

        _vm.SetSelectedUsers(UsersGrid.SelectedItems.OfType<ExistingUserRow>());
    }
}
