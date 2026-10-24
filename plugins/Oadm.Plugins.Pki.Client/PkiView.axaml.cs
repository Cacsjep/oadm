using Avalonia;
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

    /// <summary>Check a device: the shared picker for one device.</summary>
    public async Task<Guid?> PickDeviceAsync(IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> devices, Guid? selected) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowOneAsync(owner, devices, selected, heading: "Choose a device").ConfigureAwait(true)
            : null;

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
            vm.Policies.Files = Files;
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
