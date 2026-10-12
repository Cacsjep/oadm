using System.ComponentModel;

using Avalonia.Controls;

namespace Oadm.Client.Settings;

/// <summary>
/// The dialog of <see cref="UsersViewModel.Form"/> (Add user, Edit user, Change password). View only: it closes when the
/// form is done and tells the view model when the user closes it.
/// </summary>
public partial class UserFormWindow : Window
{
    private UsersViewModel? _vm;

    public UserFormWindow()
    {
        InitializeComponent();
    }

    public void Attach(UsersViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        switch (_vm?.Form)
        {
            case UserForm.Add:
                UserNameBox.Focus();
                break;
            case UserForm.Edit:
                EditRoleBox.Focus();
                break;
            case UserForm.Password:
                ResetBox.Focus();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is { } vm)
        {
            vm.PropertyChanged -= OnViewModelChanged;
            if (vm.Form != UserForm.None)
            {
                vm.CloseForm();
            }
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsersViewModel.Form) && _vm?.Form == UserForm.None)
        {
            Close();
        }
    }
}
