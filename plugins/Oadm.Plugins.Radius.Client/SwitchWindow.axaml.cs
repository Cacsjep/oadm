using Avalonia.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>The switch dialog window. View only: wiring to the view model.</summary>
public partial class SwitchWindow : Window
{
    public SwitchWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="viewModel"/>; <paramref name="closed"/> gets whether it saved.</summary>
    public void Attach(SwitchDialogViewModel viewModel, Action<bool> closed)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(closed);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) =>
        {
            closed(saved);
            Close();
        };
    }
}
