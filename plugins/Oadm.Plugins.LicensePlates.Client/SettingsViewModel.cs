using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>A choice of the Allowed character deviation.</summary>
public sealed record DeviationChoice(int Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Settings dialog of License Plate Center (docs/spec/license-plate-center.md "Settings", Administrators): Matching
/// (Allowed character deviation, Wait for the best read) and Log (Keep reads for, Maximum size, Folder on the server with the
/// shared server folder picker, Keep plate pictures, Keep unknown plates in the log). Save stays off while a field has an
/// error or the picker's check of a typed folder found a problem; a refusal of the server shows below its field.
/// </summary>
public sealed partial class SettingsViewModel : ValidatingViewModel
{
    public const decimal MinWait = 1;
    public const decimal MaxWait = 10;
    public const decimal MinDays = 1;
    public const decimal MaxDays = 3650;
    public const decimal MinGigabytes = 1;
    public const decimal MaxGigabytes = 10_000;

    private readonly LicensePlatesViewModel _page;

    public SettingsViewModel(LicensePlatesViewModel page)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        SelectedDeviation = DeviationChoices[1];
        Validation
            .Rule(nameof(WaitSeconds), () => WaitSeconds is null or < MinWait or > MaxWait ? $"Enter {MinWait} to {MaxWait} seconds." : null)
            .Rule(nameof(KeepDays), () => KeepDays is null or < MinDays or > MaxDays ? $"Enter {MinDays} to {MaxDays:N0} days." : null)
            .Rule(nameof(MaxSize), () => MaxSize is null or < MinGigabytes or > MaxGigabytes ? $"Enter {MinGigabytes} to {MaxGigabytes:N0} GB." : null)
            .Rule(nameof(Folder), () => null) // optional: without a folder nothing is stored, events still fire
            .Rule(nameof(SelectedDeviation), () => null);
        Validation.Validate();
    }

    public static IReadOnlyList<DeviationChoice> DeviationChoices { get; } =
        [new(0, PageTexts.Exact), new(1, PageTexts.OneCharacter), new(2, PageTexts.TwoCharacters)];

    /// <summary>The server's folders for the picker (null on older hosts: Choose folder is off).</summary>
    public IServerFolders? ServerFolders => _page.Client.Context.ServerFolders;

    [ObservableProperty] public partial DeviationChoice SelectedDeviation { get; set; }

    [ObservableProperty] public partial decimal? WaitSeconds { get; set; } = 3;

    [ObservableProperty] public partial decimal? KeepDays { get; set; } = 30;

    [ObservableProperty] public partial decimal? MaxSize { get; set; } = 20;

    [ObservableProperty] public partial string? Folder { get; set; }

    /// <summary>The picker's check of a typed folder (one-way from the picker); Save is off while it is set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? FolderProblem { get; set; }

    [ObservableProperty] public partial bool KeepPictures { get; set; } = true;

    [ObservableProperty] public partial bool KeepUnknown { get; set; } = true;

    /// <summary>The read of the settings (the dialog's loading overlay).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Why Save is off (its tooltip); null when it is on.</summary>
    public string? SaveBlockedReason => !IsFormValid ? FormError : FolderProblem;

    public event EventHandler<bool>? CloseRequested;

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public Task SaveTask { get; private set; } = Task.CompletedTask;

    public Task LoadAsync() => LoadTask = LoadCoreAsync();

    /// <summary>The settings the form gives.</summary>
    public LicensePlateSettings ToSettings() => new()
    {
        Deviation = SelectedDeviation.Value,
        WaitSeconds = (int)(WaitSeconds ?? 0),
        KeepDays = (int)(KeepDays ?? 0),
        MaxGigabytes = (int)(MaxSize ?? 0),
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        KeepPictures = KeepPictures,
        KeepUnknown = KeepUnknown,
    };

    private bool CanSave() => !IsLoading && !IsSaving && IsFormValid && string.IsNullOrEmpty(FolderProblem);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveTask = SaveCoreAsync();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveBlockedReason));
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            ErrorText = await GuardedRun.RunAsync(async () =>
            {
                var s = await _page.Client.CallAsync<LicensePlateSettings>(LicensePlatesMethods.Settings, null).ConfigureAwait(true);
                SelectedDeviation = DeviationChoices.FirstOrDefault(d => d.Value == s.Deviation) ?? DeviationChoices[1];
                WaitSeconds = s.WaitSeconds;
                KeepDays = s.KeepDays;
                MaxSize = s.MaxGigabytes;
                Folder = s.Folder;
                KeepPictures = s.KeepPictures;
                KeepUnknown = s.KeepUnknown;
                Validation.Reset();
            }).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SaveCoreAsync()
    {
        Validation.ShowAll();
        if (!CanSave())
        {
            return;
        }

        IsSaving = true;
        ErrorText = null;
        try
        {
            SaveSettingsReply? reply = null;
            ErrorText = await GuardedRun.RunAsync(async () =>
                reply = await _page.Client.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, ToSettings()).ConfigureAwait(true)).ConfigureAwait(true);
            if (reply?.Errors is { Count: > 0 } errors)
            {
                foreach (var (field, error) in errors)
                {
                    Validation.SetServerError(field switch
                    {
                        "waitSeconds" => nameof(WaitSeconds),
                        "keepDays" => nameof(KeepDays),
                        "maxGigabytes" => nameof(MaxSize),
                        "deviation" => nameof(SelectedDeviation),
                        _ => nameof(Folder),
                    }, error);
                }

                return;
            }

            if (reply?.Settings is not null)
            {
                CloseRequested?.Invoke(this, true);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }
}
