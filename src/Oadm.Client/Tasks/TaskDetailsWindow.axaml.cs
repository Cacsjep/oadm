using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oadm.Client.Tasks;

public partial class TaskDetailsWindow : Window
{
    public TaskDetailsWindow()
    {
        InitializeComponent();
        Footer.CancelButton.Click += OnClose;
        Footer.CancelButton.IsDefault = true;
    }

    /// <summary>Stops the view model from following the task once the window is gone.</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
