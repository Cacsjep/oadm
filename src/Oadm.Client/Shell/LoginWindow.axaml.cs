using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oadm.Client.Shell;

/// <summary>The login window (see <see cref="LoginViewModel"/>). Close ends the app when nobody logged in.</summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Footer.CancelButton.Click += OnClose;
    }

    /// <summary>Checks the server (or resumes a remembered session) once the window is visible.</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is LoginViewModel vm)
        {
            await vm.InitializeAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(vm.UserName))
            {
                UserNameBox.Focus();
            }
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
