using Avalonia.Controls;

namespace Oadm.Client.Updates;

/// <summary>
/// The window of the client's update helper (see <see cref="UpdateHelperViewModel"/>). It has no X and cannot be closed
/// while the helper runs: closing it would end the helper before the new client starts.
/// </summary>
public partial class UpdateHelperWindow : Window
{
    public UpdateHelperWindow()
    {
        InitializeComponent();
        Band.CloseButton.IsVisible = false;
        Closing += (_, e) => e.Cancel = KeepsOpen && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
    }

    /// <summary>True while the helper runs (tests close it).</summary>
    public bool KeepsOpen { get; set; } = true;
}
