using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// Page view. View only: activates the view model while shown, opens the switch dialog, the certificate choice, the
/// revoke confirmation and the certificate window, and passes double clicks (switch: Edit, log row: View certificate).
/// </summary>
public partial class RadiusView : UserControl, ISwitchDialogs, ILogDialogs, IRevokeDialogs
{
    private RadiusViewModel? _vm;

    public RadiusView()
    {
        InitializeComponent();
        SwitchesGrid.DoubleTapped += (_, _) =>
        {
            if (_vm?.Switches.EditCommand.CanExecute(null) == true)
            {
                _vm.Switches.EditCommand.Execute(null);
            }
        };
        LogGrid.DoubleTapped += (_, _) =>
        {
            if (_vm?.Log.ViewCertificateCommand.CanExecute(null) == true)
            {
                _vm.Log.ViewCertificateCommand.Execute(null);
            }
        };
    }

    public async Task<bool> ShowSwitchAsync(SwitchDialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var window = new SwitchWindow();
        var saved = false;
        window.Attach(dialog, result => saved = result);
        await ShowAsync(window).ConfigureAwait(true);
        return saved;
    }

    public async Task<IReadOnlyList<IssuedCertificateRow>?> ChooseCertificatesAsync(RevokePickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        var window = new RevokePickerWindow();
        IReadOnlyList<IssuedCertificateRow>? chosen = null;
        window.Attach(picker, rows => chosen = rows);
        window.Opened += (_, _) => _ = picker.LoadAsync();
        await ShowAsync(window).ConfigureAwait(true);
        return chosen;
    }

    public Task<RevocationReason?> ConfirmRevokeAsync(string title, string message) =>
        TopLevel.GetTopLevel(this) is Window owner ? IssuedCertificateList.ConfirmRevokeAsync(owner, title, message) : Task.FromResult<RevocationReason?>(null);

    public Task ShowCertificateAsync(CertificateDetails certificate) =>
        TopLevel.GetTopLevel(this) is Window owner ? CertificateWindow.ShowAsync(certificate, owner) : Task.CompletedTask;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as RadiusViewModel;
        if (_vm is not null)
        {
            _vm.Switches.Dialogs = this;
            _vm.Log.Dialogs = this;
            _vm.Revoked.Dialogs = this;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _vm?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task ShowAsync(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }
}
