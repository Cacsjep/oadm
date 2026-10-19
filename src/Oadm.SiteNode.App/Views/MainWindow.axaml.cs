using Avalonia.Controls;

using Oadm.SiteNode.App.ViewModels;

namespace Oadm.SiteNode.App.Views;

/// <summary>The app's window. View only: it starts the view model's refresh loop and ends it when it closes.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void Attach(NodeAppViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        var closing = new CancellationTokenSource();
        Closed += (_, _) =>
        {
            closing.Cancel();
            closing.Dispose();
        };
        _ = viewModel.RunAsync(closing.Token);
    }
}
