using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.EdgeStorage.Client;

/// <summary>Shows the page; the view model connects while the view is in the visual tree. Opens the page's dialogs.</summary>
public partial class EdgeStorageView : UserControl, IStorageDialogs
{
    public EdgeStorageView()
    {
        InitializeComponent();
        RowGrid.SelectionChanged += (_, _) => (DataContext as EdgeStorageViewModel)?.SetSelection(RowGrid.SelectedItems.OfType<StorageRowViewModel>());
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is EdgeStorageViewModel vm)
        {
            vm.Dialogs = this;
            vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner
                && await vm.DeviceChoice.PickAsync(owner, "Choose cameras").ConfigureAwait(true);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as EdgeStorageViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as EdgeStorageViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    public async Task<int?> AskRetentionAsync(int? current, int cameras)
    {
        var vm = new RetentionViewModel(current, cameras);
        return await ShowAsync(new RetentionWindow(), vm).ConfigureAwait(true) ? vm.Result : null;
    }

    public async Task<string?> AskPassphraseAsync(int cameras)
    {
        var vm = new PassphraseViewModel(cameras);
        return await ShowAsync(new PassphraseWindow(), vm).ConfigureAwait(true) ? vm.Passphrase : null;
    }

    /// <summary>Shows a dialog over the page's window; true when it was closed with OK.</summary>
    private async Task<bool> ShowAsync(Window window, StorageDialogViewModel vm)
    {
        var ok = false;
        window.DataContext = vm;
        vm.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }

        return ok;
    }
}
