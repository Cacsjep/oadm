using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// Page view. View only: activates the view model while shown, opens the dialogs, the device picker of a policy and the file pickers;
/// the shared certificate list (<see cref="IssuedCertificateList"/>) hands its grid's selection and double clicks to its
/// view model.
/// </summary>
public partial class PkiView : UserControl, IPkiDialogs, IIssuedCertificatesDialogs, IPolicyDialogs
{
    public PkiView()
    {
        InitializeComponent();
        Files = new PkiFilePicker(() => TopLevel.GetTopLevel(this));
    }

    /// <summary>
    /// The Certificate authorities card and its splitter only for users with all devices (the CAs are server-wide; a user
    /// limited to a device group never gets CA rows): without them the certificates take the whole tab. Layout only.
    /// </summary>
    private void ShowAuthorities(bool shown)
    {
        var row = CertificatesPage.RowDefinitions[0];
        row.MinHeight = shown ? 250 : 0;
        row.Height = shown ? new GridLength(300) : new GridLength(0);
        AuthoritiesCard.IsVisible = shown;
        CertificatesSplitter.IsVisible = shown;
    }

    /// <summary>
    /// The policy table grouped like the Plugins page: one collapsible header per group that has rows (an empty group shows no
    /// header); the rows come in the groups' order, so the groups do too. Layout only.
    /// </summary>
    private void GroupPolicies(PoliciesViewModel policies)
    {
        var view = new Avalonia.Collections.DataGridCollectionView(policies.Rows);
        view.GroupDescriptions.Add(new Avalonia.Collections.DataGridPathGroupDescription(nameof(PolicyRowViewModel.GroupKey)));
        PoliciesGrid.ItemsSource = view;
    }

    /// <summary>
    /// The highlight of a jump from a server tab (<see cref="PoliciesViewModel.Highlight"/>): the rows' class <c>highlight</c>
    /// follows <see cref="PolicyRowViewModel.IsHighlighted"/> (also on rows the virtualized grid realizes or reuses later),
    /// the first highlighted row is scrolled into view once the tab has its layout. Layout only.
    /// </summary>
    private void FollowHighlight(PoliciesViewModel policies)
    {
        PoliciesGrid.LoadingRow += (_, e) => e.Row.Classes.Set("highlight", e.Row.DataContext is PolicyRowViewModel { IsHighlighted: true });
        policies.HighlightChanged += (_, _) =>
        {
            foreach (var row in PoliciesGrid.GetVisualDescendants().OfType<DataGridRow>())
            {
                row.Classes.Set("highlight", row.DataContext is PolicyRowViewModel { IsHighlighted: true });
            }
        };
        policies.HighlightShown += (_, row) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PoliciesGrid.ScrollIntoView(row, null), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>A policy's devices: the shared picker in its group mode.</summary>
    public async Task<bool> PickDevicesAsync(DevicePickerViewModel picker, string heading)
    {
        ArgumentNullException.ThrowIfNull(picker);
        return TopLevel.GetTopLevel(this) is Window owner && await new DevicePickerWindow(picker) { Heading = heading }.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public Task ShowCertificateAsync(CertificateDetails certificate) =>
        TopLevel.GetTopLevel(this) is Window owner ? CertificateWindow.ShowAsync(certificate, owner) : Task.CompletedTask;

    public Task<RevocationReason?> ConfirmRevokeAsync(string title, string message) =>
        TopLevel.GetTopLevel(this) is Window owner ? IssuedCertificateList.ConfirmRevokeAsync(owner, title, message) : Task.FromResult<RevocationReason?>(null);

    public IPkiFiles Files { get; }

    public IPkiFiles? CertificateFiles => Files;

    public Task<bool> ConfirmDeleteAsync(string title, string message) => ConfirmDeleteAsync(title, message, "Delete");

    /// <summary>Delete with a red confirm button (also "Revoke and delete").</summary>
    public Task<bool> ConfirmDeleteAsync(string title, string message, string confirmText) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? new MessageWindow { Heading = title, Message = message, ConfirmText = confirmText, CancelText = "Cancel", IsDanger = true }.ShowDialog<bool>(owner)
            : Task.FromResult(false);

    public Task ShowCreateAsync(CreateCertificateViewModel dialog) => ShowAsync(new CreateCertificateWindow(), dialog);

    public Task ShowSignRequestAsync(SignRequestViewModel dialog) => ShowAsync(new SignRequestWindow(), dialog);

    public Task ShowImportCertificateAsync(ImportCertificateViewModel dialog) => ShowAsync(new ImportCertificateWindow(), dialog);

    public Task ShowExportKeyAsync(ExportKeyViewModel dialog) => ShowAsync(new ExportKeyWindow(), dialog);

    public Task ShowGenerateAsync(GenerateCaViewModel dialog) => ShowAsync(new GenerateCaWindow(), dialog);


    public Task ShowImportAsync(ImportCaViewModel dialog) => ShowAsync(new ImportCaWindow(), dialog);

    public Task ShowBackupAsync(BackupViewModel dialog) => ShowAsync(new BackupWindow(), dialog);

    public Task ShowImportAuthorityAsync(ImportAuthorityViewModel dialog) => ShowAsync(new ImportAuthorityWindow(), dialog);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PkiViewModel vm)
        {
            vm.Dialogs = this;
            vm.Certificates.Dialogs = this;
            vm.Authorities.Dialogs = this;
            vm.Policies.Dialogs = this;
            vm.Policies.Files = Files;
            GroupPolicies(vm.Policies);
            FollowHighlight(vm.Policies);
            vm.Policies.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PoliciesViewModel.IsShown))
                {
                    ShowAuthorities(vm.Policies.IsShown);
                }
            };
            ShowAuthorities(vm.Policies.IsShown);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as PkiViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as PkiViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task ShowAsync(Window window, PkiDialogViewModel dialog)
    {
        PkiDialogs.Attach(window, dialog);
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

/// <summary>Wires a PKI dialog window to its view model: close request, confirmation popup and file pickers owned by the window.</summary>
public static class PkiDialogs
{
    public static void Attach(Window window, PkiDialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(dialog);
        window.DataContext = dialog;
        dialog.CloseRequested += (_, _) => window.Close();
        dialog.Confirm = (title, message, confirm) => Oadm.Sdk.Client.Controls.MessageWindow.ConfirmAsync(window, title, message, confirm);
        dialog.Files = new PkiFilePicker(() => window);
    }
}
