using Avalonia.Controls;
using Avalonia.Input;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>Large preview of a tile. View only.</summary>
public partial class SnapshotPreviewWindow : Window
{
    public SnapshotPreviewWindow()
    {
        InitializeComponent();
    }

    public void Attach(SnapshotPreviewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.Dispose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
