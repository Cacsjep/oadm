using Avalonia.Controls;

using Oadm.SiteNode.App.ViewModels;

namespace Oadm.SiteNode.App.Views;

/// <summary>The Add port dialog (see <see cref="AddPortViewModel"/>). View only: wiring to the view model.</summary>
public partial class AddPortWindow : Window
{
    public AddPortWindow()
    {
        InitializeComponent();
    }

    public void Attach(AddPortViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, added) => Close(added);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PortBox.Focus();
    }
}
