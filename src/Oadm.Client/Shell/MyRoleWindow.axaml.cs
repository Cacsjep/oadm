using System.Windows.Input;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.Input;

namespace Oadm.Client.Shell;

/// <summary>The "Your role" window (see <see cref="MyRoleViewModel"/>). View only: Close.</summary>
public partial class MyRoleWindow : Window
{
    public MyRoleWindow()
    {
        CloseCommand = new RelayCommand(Close);
        InitializeComponent();
    }

    /// <summary>The footer's Close.</summary>
    public ICommand CloseCommand { get; }
}
