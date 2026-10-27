using Avalonia.Controls;

namespace Oadm.Client.Updates;

/// <summary>The new version dialog (see <see cref="UpdateViewModel"/>).</summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is UpdateViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
    }
}
