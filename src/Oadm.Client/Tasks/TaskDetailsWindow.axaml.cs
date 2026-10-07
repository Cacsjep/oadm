using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oadm.Client.Tasks;

public partial class TaskDetailsWindow : Window
{
    public TaskDetailsWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
