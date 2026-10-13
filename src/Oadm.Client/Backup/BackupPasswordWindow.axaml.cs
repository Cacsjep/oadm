using Avalonia.Controls;

namespace Oadm.Client.Backup;

/// <summary>The password of "Back up now" (see <see cref="BackupPasswordViewModel"/>). View only.</summary>
public partial class BackupPasswordWindow : Window
{
    public BackupPasswordWindow()
    {
        InitializeComponent();
    }

    public void Attach(BackupPasswordViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PasswordField.Focus();
    }
}
