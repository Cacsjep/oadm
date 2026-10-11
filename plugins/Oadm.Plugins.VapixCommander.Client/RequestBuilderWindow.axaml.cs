using Avalonia.Controls;
using Avalonia.Input;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>The request builder dialog. View only: wiring to the view model.</summary>
public partial class RequestBuilderWindow : Window
{
    public RequestBuilderWindow()
    {
        InitializeComponent();
    }

    public void Attach(RequestBuilderViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <summary>A double click on a command starts from it.</summary>
    private void OnCommandDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RequestBuilderViewModel vm && vm.SelectedCommand is not null && vm.NextCommand.CanExecute(null))
        {
            vm.NextCommand.Execute(null);
            e.Handled = true;
        }
    }
}
