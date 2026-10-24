using Avalonia.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>The Advanced dialog window. View only: wiring to the view model.</summary>
public partial class AdvancedWindow : Window
{
    public AdvancedWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="viewModel"/>; <paramref name="closed"/> gets whether it saved.</summary>
    public void Attach(AdvancedViewModel viewModel, Action<bool> closed)
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
