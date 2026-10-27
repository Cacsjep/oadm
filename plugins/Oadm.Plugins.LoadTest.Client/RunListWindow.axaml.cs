using Avalonia.Controls;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>The run list dialog.</summary>
public partial class RunListWindow : Window
{
    public RunListWindow()
    {
        InitializeComponent();
    }

    public static async Task ShowAsync(Window owner, RunListViewModel runs)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(runs);
        var window = new RunListWindow { DataContext = runs };
        runs.Close = window.Close;
        try
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        finally
        {
            runs.Close = null;
        }
    }
}
