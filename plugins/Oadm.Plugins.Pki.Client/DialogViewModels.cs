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

/// <summary>"Generate new CA": common name, organization, validity in years; RSA 4096 is fixed. Then the replace confirmation.</summary>
public sealed partial class GenerateCaViewModel : PkiDialogViewModel
{
    private readonly Func<GenerateCaViewModel, GenerateRequest, Task<bool>> _submit;

    /// <param name="submit">The page: asks the replace confirmation, generates, applies the state; true = done.</param>
    /// <param name="commonName">Prefilled common name.</param>
    public GenerateCaViewModel(Func<GenerateCaViewModel, GenerateRequest, Task<bool>> submit, string commonName)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        CommonName = commonName;
        Validation.Rule(nameof(CommonName), () => PkiValidation.CommonName(CommonName));
        Validation.Rule(nameof(Organization), () => PkiValidation.Organization(Organization));
        Validation.Rule(nameof(ValidityYears), () => PkiValidation.ValidityYears(ValidityYears));
        Validation.Reset();
    }

    public static string Title => "Generate new CA";

    public static string KeyInfo => "RSA 4096 key, SHA-256 signatures";

    public static string BusyText => "Creating the certificate authority. This takes a few seconds.";

    [ObservableProperty]
    public partial string CommonName { get; set; }

    [ObservableProperty]
    public partial string Organization { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ValidityYears { get; set; } = PkiPluginInfo.DefaultValidityYears.ToString(CultureInfo.InvariantCulture);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        var request = new GenerateRequest(CommonName.Trim(), string.IsNullOrWhiteSpace(Organization) ? null : Organization.Trim(), PkiValidation.ParseInt(ValidityYears) ?? 0, Confirmed: true);
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

    private bool CanGenerate() => IsFormValid && !IsBusy;

    protected override void OnValidationChanged() => GenerateCommand.NotifyCanExecuteChanged();

    protected override void OnBusyChanged() => GenerateCommand.NotifyCanExecuteChanged();
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

    /// <param name="submit">The page: imports (confirmed false), asks the replace confirmation, imports again; true = done.</param>
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

    public static string Title => "Import CA";

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

    public string FileText => File ?? "No file chosen";

    public string KeyFileText => KeyFile ?? "No key file chosen";

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
            string.IsNullOrEmpty(KeyPassword) ? null : KeyPassword,
            Confirmed: false);
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

/// <summary>Opens the PKI dialogs (implemented by the page view; tests use a fake).</summary>
public interface IPkiDialogs
{
    Task ShowGenerateAsync(GenerateCaViewModel dialog);

    Task ShowImportAsync(ImportCaViewModel dialog);

    Task ShowBackupAsync(BackupViewModel dialog);

    /// <summary>File pickers of the page.</summary>
    IPkiFiles Files { get; }
}
