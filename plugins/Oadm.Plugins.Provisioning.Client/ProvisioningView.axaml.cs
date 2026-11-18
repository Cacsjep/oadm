using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>Page view. View only: activates the view model while shown, hands the grid's selection over, opens the profile editor.</summary>
public partial class ProvisioningView : UserControl, IProvisioningPageUi
{
    public ProvisioningView()
    {
        InitializeComponent();
        CameraGrid.SelectionChanged += (_, _) =>
        {
            if (!GridSort.IsMovingSelection(CameraGrid))
            {
                (DataContext as ProvisioningViewModel)?.SetSelection(CameraGrid.SelectedItems.OfType<CameraRowViewModel>());
            }
        };
    }

    public async Task ShowProfileEditorAsync(ProfileEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var window = new ProfileEditorWindow();
        window.Attach(editor);
        _ = editor.LoadAsync();
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ProvisioningViewModel vm)
        {
            vm.Ui = this;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ProvisioningViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as ProvisioningViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
