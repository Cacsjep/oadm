using Avalonia.Controls;

namespace Oadm.Plugins.Users.Client;

/// <summary>The "Users" dialog. Closes with the payload the view model hands over (null = cancelled).</summary>
public partial class UsersWindow : Window
{
    public UsersWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (DataContext is UsersDialogViewModel vm)
        {
            vm.CloseRequested += (_, payload) => Close(payload);
        }

        base.OnDataContextChanged(e);
    }
}
