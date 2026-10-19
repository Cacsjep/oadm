using Avalonia.Controls;

namespace Oadm.Client.Sites;

/// <summary>The dialogs of the Sites page (see <see cref="SiteFormViewModel"/>). View only: wiring to the view model.</summary>
public partial class SiteFormWindow : Window
{
    public SiteFormWindow()
    {
        InitializeComponent();
    }

    public void Attach(SiteFormViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
    }

    /// <summary>Focuses the first field of the form.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not SiteFormViewModel vm)
        {
            return;
        }

        TextBox? first = vm.Mode switch
        {
            SiteFormMode.Bind => CodeBox,
            SiteFormMode.Rename => NameBox,
            SiteFormMode.ChangeAddress or SiteFormMode.AddManually => AddressBox,
            _ => null,
        };
        first?.Focus();
        first?.SelectAll();
    }
}
