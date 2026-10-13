using Avalonia.Controls;

namespace Oadm.Client.Backup;

/// <summary>The restore dialog (see <see cref="RestoreViewModel"/>). View only.</summary>
public partial class RestoreWindow : Window
{
    public RestoreWindow()
    {
        InitializeComponent();
    }

    public void Attach(RestoreViewModel viewModel)
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
