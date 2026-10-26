using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Pki.Client;

/// <summary>Shared plumbing of the PKI dialogs: busy state, close request, the confirmation popup over the dialog, server errors.</summary>
public abstract partial class PkiDialogViewModel : ValidatingViewModel
{
    /// <summary>The dialog closes; true when it finished its job.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The shared confirmation popup owned by the dialog window (title, message, confirm text); set by the window.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>File pickers owned by the dialog window; set by the window.</summary>
    public IPkiFiles? Files { get; set; }

    public bool Completed { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        Confirm?.Invoke(title, message, confirmText) ?? Task.FromResult(false);

    /// <summary>Field errors of the server reply, directly under their fields; a message without a field goes to <paramref name="fallbackField"/>.</summary>
    public void SetServerErrors(IReadOnlyDictionary<string, string>? errors, string? error, string fallbackField)
    {
        foreach (var (field, message) in errors ?? new Dictionary<string, string>())
        {
            Validation.SetServerError(field, message);
        }

        if (!string.IsNullOrEmpty(error))
        {
            Validation.SetServerError(fallbackField, error);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected void Close()
    {
        Completed = true;
        CloseRequested?.Invoke(this, true);
    }

    partial void OnIsBusyChanged(bool value) => OnBusyChanged();

    protected virtual void OnBusyChanged()
    {
    }

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without a Grpc reference.</summary>
    public static string Message(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }
}

/// <summary>
/// "Create CA" (decision 2026-10-26, replaces New OADM CA): a root CA (self-signed) or an intermediate CA signed by a CA with
/// its key; name, type, signed by (intermediate), key (ECC or RSA), valid (years). The server checks everything again; its
/// field errors go under the fields. Nothing is replaced: the new CA joins the list.
/// </summary>
public sealed partial class CreateCaViewModel : PkiDialogViewModel
{
    private readonly Func<CreateCaViewModel, CreateCaRequest, Task<bool>> _submit;

    /// <param name="submit">The page: creates the CA, applies the state; true = done.</param>
    /// <param name="issuers">The CAs with their key that can sign an intermediate (id and name).</param>
    public CreateCaViewModel(Func<CreateCaViewModel, CreateCaRequest, Task<bool>> submit, IReadOnlyList<Choice> issuers)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        Issuers = issuers ?? throw new ArgumentNullException(nameof(issuers));
        Type = TypeChoices[0];
        Key = KeyChoices.First(k => k.Value == CaKeys.Default);
        Issuer = Issuers.Count > 0 ? Issuers[0] : null;
        Validation.Rule(nameof(CommonName), () => PkiValidation.CommonName(CommonName));
        Validation.Rule(nameof(ValidityYears), () => PkiValidation.ValidityYears(ValidityYears));
        Validation.Rule(nameof(Issuer), () => IsIntermediate && Issuer is null ? (Issuers.Count == 0 ? NoIssuerText : "Choose a CA.") : null);
        Validation.Rule(nameof(Key), () => Key is null ? "Choose a key." : null);
        Validation.Reset();
    }

    public static string Title => "Create CA";

    public static string BusyText => "Creating the CA. This takes a few seconds.";

    /// <summary>No CA can sign an intermediate yet.</summary>
    public const string NoIssuerText = "No CA with its key yet. Create a root CA first.";

    /// <summary>Root (self-signed) and Intermediate.</summary>
    public static IReadOnlyList<Choice> TypeChoices { get; } = [new("root", "Root CA (self-signed)"), new("intermediate", "Intermediate CA")];

    /// <summary>ECC P-256, RSA 2048, RSA 3072, RSA 4096.</summary>
    public static IReadOnlyList<Choice> KeyChoices { get; } = [.. CaKeys.All.Select(k => new Choice(k, CaKeys.Name(k)))];

    /// <summary>The CAs that can sign an intermediate.</summary>
    public IReadOnlyList<Choice> Issuers { get; }

    [ObservableProperty]
    public partial string CommonName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntermediate))]
    public partial Choice Type { get; set; }

    /// <summary>An intermediate CA: Signed by shows.</summary>
    public bool IsIntermediate => Type.Value == "intermediate";

    [ObservableProperty]
    public partial Choice? Issuer { get; set; }

    [ObservableProperty]
    public partial Choice? Key { get; set; }

    [ObservableProperty]
    public partial string ValidityYears { get; set; } = PkiPluginInfo.DefaultValidityYears.ToString(CultureInfo.InvariantCulture);

    partial void OnTypeChanged(Choice value) => Validation.Validate(); // Signed by is needed only for an intermediate

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        var request = new CreateCaRequest(CommonName.Trim(), IsIntermediate, IsIntermediate ? Issuer?.Value : null, Key?.Value, PkiValidation.ParseInt(ValidityYears) ?? 0);
        IsBusy = true;
        try
        {
            if (await _submit(this, request).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown under the name field; the dialog stays open.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(CommonName), Message(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreate() => IsFormValid && !IsBusy;

    protected override void OnValidationChanged() => CreateCommand.NotifyCanExecuteChanged();

    protected override void OnBusyChanged() => CreateCommand.NotifyCanExecuteChanged();
}


/// <summary>
/// "Import CA": a PKCS#12 file with its password, or a PEM certificate; a certificate without a key asks for the key file
/// and its password. The server checks everything; its field errors go under the fields. Then the replace confirmation.
/// </summary>
public sealed partial class ImportCaViewModel : PkiDialogViewModel
{
    private readonly Func<ImportCaViewModel, ImportRequest, Task<bool>> _submit;
    private byte[]? _file;
    private byte[]? _keyFile;

    /// <param name="submit">The page: adds the CA to the list (decision 2026-10-25: it never replaces the OADM CA); true = done.</param>
    public ImportCaViewModel(Func<ImportCaViewModel, ImportRequest, Task<bool>> submit)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        Validation.Rule(nameof(File), () => _file is null ? "Choose a file." : _file.Length > PkiPluginInfo.MaxImportBytes ? "The file is larger than 1 MB." : null);
        Validation.Rule(nameof(KeyFile), () => !NeedsKeyFile ? null
            : _keyFile is null ? "Choose the private key of this certificate."
            : _keyFile.Length > PkiPluginInfo.MaxImportBytes ? "The file is larger than 1 MB." : null);
        Validation.Rule(nameof(Password), () => null);
        Validation.Rule(nameof(KeyPassword), () => null);
        Validation.Reset();
    }

    public static string Title => "Import CA with its key";

    /// <summary>Name of the chosen file (the property the file errors belong to).</summary>
    [ObservableProperty]
    public partial string? File { get; set; }

    [ObservableProperty]
    public partial string? FileDetails { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>The file is a certificate without its private key: the key file row is shown.</summary>
    [ObservableProperty]
    public partial bool NeedsKeyFile { get; set; }

    [ObservableProperty]
    public partial string? KeyFile { get; set; }

    [ObservableProperty]
    public partial string KeyPassword { get; set; } = string.Empty;

    public string? FileError => ErrorOf(nameof(File));

    public string? KeyFileError => ErrorOf(nameof(KeyFile));

    /// <summary>The chosen file's name for the file button; null until one is chosen.</summary>
    public string? FileText => File;

    public string? KeyFileText => KeyFile;

    /// <summary>Sets the chosen CA file (also used by tests).</summary>
    public void SetFile(PickedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file.Data;
        NeedsKeyFile = file.Data.Length <= PkiPluginInfo.MaxImportBytes && CaImporter.NeedsKeyFile(file.Data);
        if (!NeedsKeyFile)
        {
            _keyFile = null;
            KeyFile = null;
        }

        FileDetails = Describe(file.Data);
        File = file.Name;
        OnPropertyChanged(nameof(FileText));
    }

    /// <summary>Sets the chosen key file (also used by tests).</summary>
    public void SetKeyFile(PickedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _keyFile = file.Data;
        KeyFile = file.Name;
        OnPropertyChanged(nameof(KeyFileText));
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (Files is not null && await Files.OpenAsync("Choose the CA file", PkiFileKind.ImportCa).ConfigureAwait(true) is { } file)
        {
            SetFile(file);
        }
    }

    [RelayCommand]
    private async Task ChooseKeyFileAsync()
    {
        if (Files is not null && await Files.OpenAsync("Choose the private key", PkiFileKind.PrivateKey).ConfigureAwait(true) is { } file)
        {
            SetKeyFile(file);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        var request = new ImportRequest(
            Convert.ToBase64String(_file!),
            File,
            string.IsNullOrEmpty(Password) ? null : Password,
            NeedsKeyFile && _keyFile is not null ? Convert.ToBase64String(_keyFile) : null,
            string.IsNullOrEmpty(KeyPassword) ? null : KeyPassword);
        try
        {
            if (await _submit(this, request).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown under the file; the dialog stays open.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(File), Message(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImport() => IsFormValid && !IsBusy;

    protected override void OnValidationChanged()
    {
        ImportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FileError));
        OnPropertyChanged(nameof(KeyFileError));
    }

    protected override void OnBusyChanged() => ImportCommand.NotifyCanExecuteChanged();

    private static string Describe(byte[] data)
    {
        var size = data.Length < 1024 ? $"{data.Length} bytes" : string.Create(CultureInfo.InvariantCulture, $"{data.Length / 1024.0:0.0} KB");
        if (CaCertificates.LooksLikePem(data))
        {
            return CaImporter.NeedsKeyFile(data) ? $"{size} · certificate, choose its private key below" : $"{size} · certificate with private key";
        }

        return CaImporter.NeedsKeyFile(data) ? $"{size} · certificate, choose its private key below" : $"{size} · PKCS#12 file";
    }
}

/// <summary>"Back up": password + confirmation (at least 8 characters), the warning popup, then the save dialog.</summary>
public sealed partial class BackupViewModel : PkiDialogViewModel
{
    public const string Warning = "Whoever has this file can issue certificates that your devices and browsers trust. Keep it safe.";

    private readonly Func<BackupViewModel, string, Task<bool>> _submit;

    /// <param name="submit">The page: creates the backup and saves it; true = done.</param>
    public BackupViewModel(Func<BackupViewModel, string, Task<bool>> submit)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        Validation.Rule(nameof(Password), () => PkiValidation.BackupPassword(Password));
        Validation.Rule(nameof(ConfirmPassword), () => PkiValidation.ConfirmPassword(Password, ConfirmPassword));
        Validation.Reset();
    }

    public static string Title => "Back up the CA";

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task BackupAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        if (!await ConfirmAsync(Title, Warning, "Back up").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            IsBusy = true;
            if (await _submit(this, Password).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown under the password; the dialog stays open.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(Password), Message(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanBackup() => IsFormValid && !IsBusy;

    protected override void OnValidationChanged() => BackupCommand.NotifyCanExecuteChanged();

    protected override void OnBusyChanged() => BackupCommand.NotifyCanExecuteChanged();
}

/// <summary>"Import CA" of the Certificate authorities card: one CA certificate without key (PEM or DER).</summary>
public sealed partial class ImportAuthorityViewModel : PkiDialogViewModel
{
    private readonly Func<ImportAuthorityViewModel, ImportAuthorityRequest, Task<bool>> _submit;
    private byte[]? _file;

    /// <param name="submit">The page: imports, applies the state; true = done (errors go under the file).</param>
    public ImportAuthorityViewModel(Func<ImportAuthorityViewModel, ImportAuthorityRequest, Task<bool>> submit)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        Validation.Rule(nameof(File), () => _file is null ? "Choose a file." : _file.Length > PkiPluginInfo.MaxImportBytes ? "The file is larger than 1 MB." : null);
        Validation.Reset();
    }

    public static string Title => "Import CA certificate";

    public static string Intro => "A root or intermediate CA certificate, without its key. Policies can then trust it.";

    /// <summary>Name of the chosen file (the property the file errors belong to).</summary>
    [ObservableProperty]
    public partial string? File { get; set; }

    public string? FileError => ErrorOf(nameof(File));

    /// <summary>The chosen file's name for the file button; null until one is chosen.</summary>
    public string? FileText => File;

    /// <summary>Sets the chosen file (also used by tests).</summary>
    public void SetFile(PickedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file.Data;
        File = file.Name;
        OnPropertyChanged(nameof(FileText));
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (Files is not null && await Files.OpenAsync("Choose the CA certificate", PkiFileKind.AuthorityCertificate).ConfigureAwait(true) is { } file)
        {
            SetFile(file);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await _submit(this, new ImportAuthorityRequest(Convert.ToBase64String(_file!), File)).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown under the file; the dialog stays open.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(File), Message(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImport() => IsFormValid && !IsBusy;

    protected override void OnValidationChanged()
    {
        ImportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FileError));
    }

    protected override void OnBusyChanged() => ImportCommand.NotifyCanExecuteChanged();
}

/// <summary>Opens the PKI dialogs (implemented by the page view; tests use a fake).</summary>
public interface IPkiDialogs
{
    Task ShowImportAuthorityAsync(ImportAuthorityViewModel dialog);

    /// <summary>The shared certificate window (View of a CA in the list).</summary>
    Task ShowCertificateAsync(Oadm.Sdk.Client.Controls.CertificateDetails certificate);

    Task ShowCreateCaAsync(CreateCaViewModel dialog);

    Task ShowImportAsync(ImportCaViewModel dialog);

    Task ShowBackupAsync(BackupViewModel dialog);

    /// <summary>File pickers of the page.</summary>
    IPkiFiles Files { get; }
}
