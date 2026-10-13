using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.EdgeStorage.Client;

/// <summary>A single-form dialog of the page: OK closes it with the value, Cancel or X without.</summary>
public abstract partial class StorageDialogViewModel : ValidatingViewModel
{
    /// <summary>The dialog closes; true when OK was clicked with a valid form.</summary>
    public event EventHandler<bool>? CloseRequested;

    protected StorageDialogViewModel()
    {
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand(CanExecute = nameof(IsFormValid))]
    private void Ok()
    {
        Validation.ShowAll();
        if (IsFormValid)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        OkCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>"Set retention": keep recordings for N days (1..7,000), or without an age limit (0).</summary>
public sealed partial class RetentionViewModel : StorageDialogViewModel
{
    public RetentionViewModel(int? current, int cameras)
    {
        NoLimit = current == 0;
        Days = current is > 0 ? current.Value : 7;
        Intro = cameras == 1
            ? "Recordings older than this are deleted from the SD card."
            : string.Create(CultureInfo.CurrentCulture, $"Recordings older than this are deleted from the SD cards of {cameras:N0} cameras.");
        Validation.Rule(nameof(Days), () => NoLimit || Days is >= 1 and <= StorageRequests.MaxRetentionDays && Days == decimal.Truncate(Days.Value)
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter 1 to {StorageRequests.MaxRetentionDays:N0} days."));
        Validation.Reset();
    }

    public static string Title => "Set retention";

    public string Intro { get; }

    public static decimal MaxDays => StorageRequests.MaxRetentionDays;

    [ObservableProperty]
    public partial decimal? Days { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DaysEnabled))]
    public partial bool NoLimit { get; set; }

    public bool DaysEnabled => !NoLimit;

    /// <summary>The chosen retention: 0 = no age limit.</summary>
    public int Result => NoLimit ? 0 : (int)(Days ?? 0);

    partial void OnNoLimitChanged(bool value) => Validation.Validate();
}

/// <summary>"Encrypt SD cards": the passphrase twice; the cards are formatted.</summary>
public sealed partial class PassphraseViewModel : StorageDialogViewModel
{
    public PassphraseViewModel(int cameras)
    {
        Intro = cameras == 1
            ? "The SD card is formatted: every recording on it is deleted. Keep the passphrase: the card cannot be read without it in another camera."
            : string.Create(CultureInfo.CurrentCulture, $"The SD cards of {cameras:N0} cameras are formatted: every recording on them is deleted. Keep the passphrase: a card cannot be read without it in another camera.");
        Validation.Rule(nameof(Passphrase), () => StorageTaskPlugin.PassphraseProblem(Passphrase));
        Validation.Rule(nameof(ConfirmPassphrase), () => ConfirmPassphrase == Passphrase ? null : "The passphrases do not match.");
        Validation.Reset();
    }

    public static string Title => "Encrypt SD cards";

    public string Intro { get; }

    [ObservableProperty]
    public partial string Passphrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassphrase { get; set; } = string.Empty;

    partial void OnPassphraseChanged(string value) => Validation.Validate();
}
