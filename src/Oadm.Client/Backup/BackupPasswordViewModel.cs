using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Backup;

/// <summary>
/// "Back up now": the password the backup is encrypted with (and confirmed). A restore needs it; OADM cannot recover it.
/// </summary>
public sealed partial class BackupPasswordViewModel : ValidatingViewModel
{
    public const string Intro = "The backup is encrypted with this password. A restore needs it; keep it safe.";

    public BackupPasswordViewModel()
    {
        Validation.Rule(nameof(Password), () => PasswordProblem(Password));
        Validation.Rule(nameof(ConfirmPassword), () => ConfirmPassword == Password ? null : "The passwords do not match.");
        Validation.Validate();
    }

    /// <summary>Same rule as the server (<c>BackupFile.PasswordProblem</c>).</summary>
    public static string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) ? "Enter a password."
        : password.Length < 8 ? "Use at least 8 characters."
        : password.Length > 256 ? "Use at most 256 characters."
        : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string ConfirmPassword { get; set; } = "";

    partial void OnPasswordChanged(string value) => Validation.Validate();

    public string? OkBlockedReason => Validation.FirstErrorOf([nameof(Password), nameof(ConfirmPassword)]);

    /// <summary>True: OK with a valid password; false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(OkBlockedReason));
        OkCommand.NotifyCanExecuteChanged();
    }

    private bool CanOk() => Validation.IsValid;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Validation.ShowAll();
        if (Validation.IsValid)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
