using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>The Show log window. View only: the clipboard and closing.</summary>
public partial class AppLogWindow : Window
{
    public AppLogWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AppLogViewModel vm)
        {
            vm.CopyText = async text =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text).ConfigureAwait(true);
                }
            };
            vm.CloseRequested += (_, _) => Close();
        }
    }
}
