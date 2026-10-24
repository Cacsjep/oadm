using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// Page view. View only: activates the view model while shown, opens the dialogs, the policy editor and the file pickers;
/// the shared certificate list (<see cref="IssuedCertificateList"/>) hands its grid's selection and double clicks to its
/// view model.
/// </summary>
public partial class PkiView : UserControl, IPkiDialogs, IIssuedCertificatesDialogs, IPolicyDialogs
{
    public PkiView()
    {
        InitializeComponent();
        Files = new PkiFilePicker(() => TopLevel.GetTopLevel(this));
        PoliciesGrid.DoubleTapped += (_, _) =>
        {
            if (DataContext is PkiViewModel vm && PoliciesGrid.SelectedItem is PolicyRowViewModel row)
            {
                vm.Policies.EditRowCommand.Execute(row);
            }
        };
    }

    /// <summary>
    /// A policy row's ⋯ button: its menu, like the certificate tables (Edit; Delete in red, not for a system policy). View
    /// only: the entries run the view model's commands.
    /// </summary>
    private void OnPolicyActions(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PolicyRowViewModel row } button || DataContext is not PkiViewModel vm)
        {
            return;
        }

        List<IssuedCertificateAction> actions = [new("Edit", vm.Policies.EditRowCommand, row)];
        if (row.CanDelete)
        {
            actions.Add(IssuedCertificateAction.Separator);
            actions.Add(new("Delete", vm.Policies.DeleteRowCommand, row, IsDanger: true));
        }

        RowActions.Show(button, actions);
    }

    /// <summary>The policy table grouped by what the policies do, the groups in their order (view only).</summary>
    private void GroupPolicies(PkiViewModel? vm)
    {
        if (vm is null)
        {
            PoliciesGrid.ItemsSource = null;
            return;
        }

        var view = new DataGridCollectionView(vm.Policies.Rows);
        var byGroup = new DataGridPathGroupDescription(nameof(PolicyRowViewModel.GroupLabel));
        foreach (var label in PoliciesViewModel.GroupLabels)
        {
            byGroup.GroupKeys.Add(label);
        }

        view.GroupDescriptions.Add(byGroup);
        PoliciesGrid.ItemsSource = view;
    }

    public async Task<bool> ShowPolicyAsync(PolicyEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var window = PolicyFormView.CreateWindow(editor);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        }

        window.Show();
        return false;
    }

    public Task ShowCertificateAsync(CertificateDetails certificate) =>
        TopLevel.GetTopLevel(this) is Window owner ? CertificateWindow.ShowAsync(certificate, owner) : Task.CompletedTask;

    public Task<RevocationReason?> ConfirmRevokeAsync(string title, string message) =>
        TopLevel.GetTopLevel(this) is Window owner ? IssuedCertificateList.ConfirmRevokeAsync(owner, title, message) : Task.FromResult<RevocationReason?>(null);

    public IPkiFiles Files { get; }

    public IPkiFiles? CertificateFiles => Files;

    public Task<bool> ConfirmDeleteAsync(string title, string message) =>
        TopLevel.GetTopLevel(this) is Window owner ? MessageWindow.ConfirmAsync(owner, title, message, "Delete") : Task.FromResult(false);

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
        }

        GroupPolicies(DataContext as PkiViewModel);
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
