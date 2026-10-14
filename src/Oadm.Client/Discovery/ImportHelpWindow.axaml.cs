using Avalonia.Controls;

namespace Oadm.Client.Discovery;

/// <summary>The "Import from file" dialog before the file picker (see <see cref="ImportHelpViewModel"/>). View only.</summary>
public partial class ImportHelpWindow : Window
{
    public ImportHelpWindow()
    {
        InitializeComponent();
    }

    public void Attach(ImportHelpViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, choose) => Close(choose);
    }
}
