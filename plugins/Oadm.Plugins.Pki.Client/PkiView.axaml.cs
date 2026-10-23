using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// Page view. View only: activates the view model while shown, opens the dialogs and the file pickers, hands the
/// Certificates grid's selection to its view model in one step and a double click to View certificate.
/// </summary>
public partial class PkiView : UserControl, IPkiDialogs, IIssuedCertificatesDialogs
{
    private IssuedCertificatesViewModel? _certificates;
    private bool _restoring;

    public PkiView()
    {
        InitializeComponent();
        Files = new PkiFilePicker(() => TopLevel.GetTopLevel(this));
        CertificatesGrid.SelectionChanged += (_, _) =>
        {
            if (!_restoring)
            {
                _certificates?.SetSelection(CertificatesGrid.SelectedItems.OfType<IssuedCertificateRow>());
            }
        };
        CertificatesGrid.DoubleTapped += (_, _) =>
        {
            if (_certificates?.ViewCertificateCommand.CanExecute(null) == true)
            {
                _certificates.ViewCertificateCommand.Execute(null);
            }
        };
    }

    public Task ShowCertificateAsync(CertificateDetails certificate) =>
        TopLevel.GetTopLevel(this) is Window owner ? CertificateWindow.ShowAsync(certificate, owner) : Task.CompletedTask;

    public async Task<RevocationReason?> ConfirmRevokeAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var reasons = IssuedCertificatesViewModel.Reasons;
        var chosen = await MessageWindow.ConfirmWithChoiceAsync(owner, title, message, "Revoke", "Reason",
            [.. reasons.Select(CertificateRevocation.ReasonText)], danger: true).ConfigureAwait(true);
        return chosen is { } index && index >= 0 && index < reasons.Count ? reasons[index] : null;
    }

    /// <summary>Selects the kept rows again after the page was read (the grid lost them with the rows).</summary>
    private void OnSelectionRestored(object? sender, EventArgs e)
    {
        if (_certificates is null)
        {
            return;
        }

        _restoring = true;
        try
        {
            CertificatesGrid.SelectedItems.Clear();
            foreach (var row in _certificates.Selected)
            {
                CertificatesGrid.SelectedItems.Add(row);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    public IPkiFiles Files { get; }

    public Task ShowGenerateAsync(GenerateCaViewModel dialog) => ShowAsync(new GenerateCaWindow(), dialog);

    public Task ShowImportAsync(ImportCaViewModel dialog) => ShowAsync(new ImportCaWindow(), dialog);

    public Task ShowBackupAsync(BackupViewModel dialog) => ShowAsync(new BackupWindow(), dialog);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_certificates is not null)
        {
            _certificates.SelectionRestored -= OnSelectionRestored;
            _certificates = null;
        }

        if (DataContext is PkiViewModel vm)
        {
            vm.Dialogs = this;
            _certificates = vm.Certificates;
            _certificates.Dialogs = this;
            _certificates.SelectionRestored += OnSelectionRestored;
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
