using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Client;

/// <summary>An issuing policy of the Create and Sign request dialogs: "HTTPS (365 days, ECC)".</summary>
public sealed record IssuingPolicyChoice(PkiPolicy Policy, string Label)
{
    public override string ToString() => Label;

    /// <summary>The choices of a use (HTTPS or IEEE 802.1X; null: both), in the order of the Policies card.</summary>
    public static IReadOnlyList<IssuingPolicyChoice> Of(IEnumerable<PkiPolicy> policies, string? use) =>
    [
        .. policies.Where(p => CertificateRules.UseOf(p.Kind) is { } of && (use is null || of == use))
            .Select(p => new IssuingPolicyChoice(p, $"{p.Name} ({PolicyTexts.DaysText(p.ValidDays)}, {PolicyKeys.Name(p.Key)}" + (use is null ? ", " + DeviceCertificatePurposes.Text(CertificateRules.UseOf(p.Kind)) : string.Empty) + ")")),
    ];
}

/// <summary>
/// "Create certificate" (one form, no card): use, issuing policy, name, addresses and the password of the PKCS#12 file the
/// user saves. OADM makes the key and keeps it encrypted. The server checks again; its errors go below their fields.
/// </summary>
public sealed partial class CreateCertificateViewModel : PkiDialogViewModel
{
    private readonly Func<CreateCertificateViewModel, CreateCertificateRequest, string, Task<bool>> _submit;
    private readonly IReadOnlyList<PkiPolicy> _policies;

    /// <param name="submit">The page: creates, then downloads and saves the PKCS#12 file; true = done.</param>
    /// <param name="policies">Every policy (the issuing ones of HTTPS and IEEE 802.1X are offered).</param>
    public CreateCertificateViewModel(Func<CreateCertificateViewModel, CreateCertificateRequest, string, Task<bool>> submit, IReadOnlyList<PkiPolicy> policies)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        Use = UseChoices[0];
        Validation.Rule(nameof(Use), () => Use is null ? CertificateRules.ChooseUse : null);
        Validation.Rule(nameof(Policy), () => Policy is null ? CertificateRules.ChoosePolicy : null);
        Validation.Rule(nameof(Name), () => CertificateRules.Name(Name));
        Validation.Rule(nameof(Addresses), () => CertificateRules.Addresses(Addresses, Use?.Value));
        Validation.Rule(nameof(Password), () => PkiValidation.BackupPassword(Password));
        Validation.Rule(nameof(ConfirmPassword), () => PkiValidation.ConfirmPassword(Password, ConfirmPassword));
        Validation.Reset();
    }

    public static string Title => "Create certificate";

    public const string Warning = "Whoever has this file can use the certificate. Keep it safe.";

    /// <summary>HTTPS (a server certificate) and IEEE 802.1X (a client certificate).</summary>
    public static IReadOnlyList<Choice> UseChoices { get; } = [.. CertificateRules.Uses.Select(u => new Choice(u, DeviceCertificatePurposes.Text(u)))];

    [ObservableProperty]
    public partial Choice? Use { get; set; }

    /// <summary>The issuing policies of the chosen use.</summary>
    public ObservableCollection<IssuingPolicyChoice> Policies { get; } = [];

    [ObservableProperty]
    public partial IssuingPolicyChoice? Policy { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Addresses { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    partial void OnUseChanged(Choice? value)
    {
        Policies.Clear();
        foreach (var choice in IssuingPolicyChoice.Of(_policies, value?.Value))
        {
            Policies.Add(choice);
        }

        Policy = Policies.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await _submit(this, new CreateCertificateRequest(Use!.Value, Policy!.Policy.Id, Name.Trim(), Addresses), Password).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown below the name; the dialog stays open.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(Name), Message(ex));
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

/// <summary>"Sign request" (one form): the request file of another system (PKCS#10, PEM or DER) and the issuing policy.</summary>
public sealed partial class SignRequestViewModel : PkiDialogViewModel
{
    private readonly Func<SignRequestViewModel, SignCertificateRequest, Task<bool>> _submit;
    private byte[]? _file;

    /// <param name="submit">The page: signs, then offers to save the certificate; true = done.</param>
    /// <param name="policies">Every policy (the issuing ones of HTTPS and IEEE 802.1X are offered).</param>
    public SignRequestViewModel(Func<SignRequestViewModel, SignCertificateRequest, Task<bool>> submit, IReadOnlyList<PkiPolicy> policies)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        ArgumentNullException.ThrowIfNull(policies);
        foreach (var choice in IssuingPolicyChoice.Of(policies, null))
        {
            Policies.Add(choice);
        }

        Policy = Policies.FirstOrDefault();
        Validation.Rule(nameof(File), () => _file is null ? "Choose a file." : _file.Length > PkiPluginInfo.MaxImportBytes ? "The file is larger than 1 MB." : null);
        Validation.Rule(nameof(Policy), () => Policy is null ? CertificateRules.ChoosePolicy : null);
        Validation.Reset();
    }

    public static string Title => "Sign request";

    public static string Intro => "A certificate request of another system. Its key stays there.";

    public ObservableCollection<IssuingPolicyChoice> Policies { get; } = [];

    [ObservableProperty]
    public partial IssuingPolicyChoice? Policy { get; set; }

    /// <summary>Name of the chosen file (the property the file errors belong to).</summary>
    [ObservableProperty]
    public partial string? File { get; set; }

    public string? FileError => ErrorOf(nameof(File));

    public string FileText => File ?? "No file chosen";

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
        if (Files is not null && await Files.OpenAsync("Choose the certificate request", PkiFileKind.CertificateRequest).ConfigureAwait(true) is { } file)
        {
            SetFile(file);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSign))]
    private async Task SignAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await _submit(this, new SignCertificateRequest(Policy!.Policy.Id, Convert.ToBase64String(_file!), File)).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown below the file; the dialog stays open.
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

    private bool CanSign() => IsFormValid && !IsBusy;

    protected override void OnValidationChanged()
    {
        SignCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FileError));
    }

    protected override void OnBusyChanged() => SignCommand.NotifyCanExecuteChanged();
}

/// <summary>"Import certificate" (one form): a certificate (PEM, DER), or a PKCS#12 file with its password (its key is kept).</summary>
public sealed partial class ImportCertificateViewModel : PkiDialogViewModel
{
    private readonly Func<ImportCertificateViewModel, ImportCertificateRequest, Task<bool>> _submit;
    private byte[]? _file;

    /// <param name="submit">The page: imports; true = done (errors go below their fields).</param>
    public ImportCertificateViewModel(Func<ImportCertificateViewModel, ImportCertificateRequest, Task<bool>> submit)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        Validation.Rule(nameof(File), () => _file is null ? "Choose a file." : _file.Length > PkiPluginInfo.MaxImportBytes ? "The file is larger than 1 MB." : null);
        Validation.Rule(nameof(Password), () => null);
        Validation.Reset();
    }

    public static string Title => "Import certificate";

    public static string Intro => "A certificate (.crt, .cer, .pem), or a .pfx or .p12 file with its key.";

    [ObservableProperty]
    public partial string? File { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>The file is a PKCS#12 file (not PEM, not a single DER certificate): the password row shows.</summary>
    [ObservableProperty]
    public partial bool NeedsPassword { get; set; }

    public string? FileError => ErrorOf(nameof(File));

    public string FileText => File ?? "No file chosen";

    /// <summary>Sets the chosen file (also used by tests).</summary>
    public void SetFile(PickedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file.Data;
        NeedsPassword = !Ca.CaCertificates.LooksLikePem(file.Data) && !IsDerCertificate(file.Data);
        File = file.Name;
        OnPropertyChanged(nameof(FileText));
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (Files is not null && await Files.OpenAsync("Choose the certificate", PkiFileKind.CertificateImport).ConfigureAwait(true) is { } file)
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
            if (await _submit(this, new ImportCertificateRequest(Convert.ToBase64String(_file!), File, NeedsPassword && Password.Length > 0 ? Password : null)).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown below the file; the dialog stays open.
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

    private static bool IsDerCertificate(byte[] data)
    {
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(data);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>"Export with key" (one form): the password of the PKCS#12 file, twice; then the save dialog.</summary>
public sealed partial class ExportKeyViewModel : PkiDialogViewModel
{
    private readonly Func<ExportKeyViewModel, string, Task<bool>> _submit;

    /// <param name="name">The certificate's name (the title).</param>
    /// <param name="submit">The page: downloads the PKCS#12 file and saves it; true = done.</param>
    public ExportKeyViewModel(string name, Func<ExportKeyViewModel, string, Task<bool>> submit)
    {
        CertificateName = name ?? throw new ArgumentNullException(nameof(name));
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        Validation.Rule(nameof(Password), () => PkiValidation.BackupPassword(Password));
        Validation.Rule(nameof(ConfirmPassword), () => PkiValidation.ConfirmPassword(Password, ConfirmPassword));
        Validation.Reset();
    }

    public static string Title => "Export with key";

    public string CertificateName { get; }

    public string Heading => "Export " + CertificateName + " with its key";

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await _submit(this, Password).ConfigureAwait(true))
            {
                Close();
            }
        }
#pragma warning disable CA1031 // Shown below the password; the dialog stays open.
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

    private bool CanExport() => IsFormValid && !IsBusy;

    protected override void OnValidationChanged() => ExportCommand.NotifyCanExecuteChanged();

    protected override void OnBusyChanged() => ExportCommand.NotifyCanExecuteChanged();
}
