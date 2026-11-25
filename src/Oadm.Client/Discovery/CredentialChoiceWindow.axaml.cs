using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Client.Discovery;

/// <summary>The Choose credentials dialog before the add devices page (see <see cref="CredentialChoiceViewModel"/>). View only.</summary>
public partial class CredentialChoiceWindow : Window
{
    public CredentialChoiceWindow()
    {
        InitializeComponent();
    }

    public void Attach(CredentialChoiceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.Forget(); // the X of the title bar: a shown password does not outlive the dialog
    }

    /// <summary>Add credential puts the cursor into the user name.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CredentialChoiceViewModel.IsAdding) && sender is CredentialChoiceViewModel { IsAdding: true })
        {
            Dispatcher.UIThread.Post(() => NewUserNameBox.Focus());
        }
    }
}
