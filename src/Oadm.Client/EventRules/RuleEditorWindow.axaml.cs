using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Oadm.Client.EventRules;

/// <summary>The event rule dialog (see <see cref="RuleEditorViewModel"/>). View only: wiring and focus.</summary>
public partial class RuleEditorWindow : Window
{
    public RuleEditorWindow()
    {
        InitializeComponent();
    }

    /// <summary>The copy button of a placeholder: "{objectId}" to the clipboard (view only, like the code views).</summary>
    private async void OnCopyPlaceholder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PlaceholderItem item } && Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(item.Text).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The clipboard is busy (another program holds it); the user clicks again.
            }
        }
    }

    public void Attach(RuleEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.ShowPicker = async picker =>
        {
            var window = new Oadm.Sdk.Client.Controls.DevicePickerWindow(picker);
            return await window.ShowDialog<bool>(this).ConfigureAwait(true);
        };
        viewModel.CloseRequested += (_, ok) => Close(ok);
        if (viewModel.IsNew)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus());
        }
    }
}
