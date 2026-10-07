using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oadm.Client.Dialogs;

public sealed record MessageDialogModel(string Title, string Message, string ConfirmText, string CancelText, bool ShowCancel);

public partial class MessageWindow : Window
{
    public MessageWindow()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
