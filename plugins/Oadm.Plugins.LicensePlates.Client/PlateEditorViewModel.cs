using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

using PlateRules = Oadm.Plugins.LicensePlates.Plates.PlateRules;
using PlateText = Oadm.Plugins.LicensePlates.Plates.PlateText;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// The Add plate and Edit plate dialog (docs/spec/license-plate-center.md "Plates", one form, no card): Plate, Name, Tags,
/// Allowed from, Allowed to and Delete when it ends (on by default for a new plate, only with an Allowed to). The checks of
/// the server's <see cref="PlateRules"/> run here too; every error is below its field, a refusal of the server as well.
/// </summary>
public sealed partial class PlateEditorViewModel : ValidatingViewModel
{
    private readonly LicensePlatesViewModel _page;
    private readonly Func<string, bool> _taken;

    public PlateEditorViewModel(LicensePlatesViewModel page, Plate? original, string? text, Func<string, bool> taken)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _taken = taken ?? throw new ArgumentNullException(nameof(taken));
        Original = original;
        Text = original?.Text ?? text ?? string.Empty;
        Name = original?.Name ?? string.Empty;
        (FromDate, FromTime) = Split(original?.AllowedFrom);
        (ToDate, ToTime) = Split(original?.AllowedTo);
        DeleteWhenEnds = original?.DeleteWhenEnds ?? true; // on by default for a new plate
        TagChoices = new CheckChoiceList(PageTexts.AllTags, PageTexts.NoTags);
        TagChoices.Set(page.Tags.All.Select(t => new CheckChoice(t.Id, t.Name)), false, (original?.Tags ?? []).Cast<object?>());
        TagChoices.Changed += (_, _) => Validation.Touch(nameof(TagChoices));
        Validation
            .Rule(nameof(Text), TextProblem)
            .Rule(nameof(Name), () => Problems().GetValueOrDefault("name"))
            .Rule(nameof(FromDate), () => FromDate is null && FromTime is not null ? "Choose a day." : null)
            .Rule(nameof(ToDate), () => ToDate is null && ToTime is not null ? "Choose a day." : Problems().GetValueOrDefault("allowedTo"))
            .Rule(nameof(TagChoices), () => null);
        Validation.Validate();
    }

    public const int MaxNameLength = LicensePlateLimits.MaxNameLength;

    public const int MaxTextLength = 20;

    public Plate? Original { get; }

    public bool IsNew => Original is null;

    public string Title => IsNew ? PageTexts.AddPlate : PageTexts.EditPlate;

    /// <summary>The plate as the server saved it; null until Save worked.</summary>
    public Plate? Saved { get; private set; }

    [ObservableProperty] public partial string Text { get; set; }

    [ObservableProperty] public partial string Name { get; set; }

    /// <summary>The plate tags (the multi-select box; "All tags" is not offered as a value of its own).</summary>
    public CheckChoiceList TagChoices { get; }

    [ObservableProperty] public partial DateTimeOffset? FromDate { get; set; }

    [ObservableProperty] public partial TimeSpan? FromTime { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAllowedTo))]
    public partial DateTimeOffset? ToDate { get; set; }

    [ObservableProperty] public partial TimeSpan? ToTime { get; set; }

    [ObservableProperty] public partial bool DeleteWhenEnds { get; set; }

    /// <summary>Delete when it ends only with an Allowed to.</summary>
    public bool HasAllowedTo => ToDate is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>The visible error below the Tags box.</summary>
    public string? TagsError => ErrorOf(nameof(TagChoices));

    /// <summary>Why Save is off (its tooltip); null when it is on.</summary>
    public string? SaveBlockedReason => IsFormValid ? null : FormError;

    /// <summary>Save (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public Task SaveTask { get; private set; } = Task.CompletedTask;

    /// <summary>The plate the form gives (times to the minute, the server's wall clock).</summary>
    public Plate ToPlate() => new()
    {
        Text = PlateText.Normalize(Text),
        Name = Name.Trim(),
        Tags = [.. TagChoices.Items.Where(c => c.Chosen).Select(c => (Guid)c.Value!)],
        AllowedFrom = Join(FromDate, FromTime, TimeSpan.Zero),
        AllowedTo = Join(ToDate, ToTime, new TimeSpan(23, 59, 0)),
        DeleteWhenEnds = DeleteWhenEnds && HasAllowedTo,
    };

    private bool CanSave() => IsFormValid && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveTask = SaveCoreAsync();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private async Task SaveCoreAsync()
    {
        Validation.ShowAll();
        if (!IsFormValid)
        {
            return;
        }

        IsSaving = true;
        ErrorText = null;
        try
        {
            SavePlateReply? reply = null;
            ErrorText = await GuardedRun.RunAsync(async () =>
                reply = await _page.Client.CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(ToPlate(), Original?.Text)).ConfigureAwait(true))
                .ConfigureAwait(true);
            if (reply?.Errors is { Count: > 0 } errors)
            {
                foreach (var (field, error) in errors)
                {
                    Validation.SetServerError(field switch
                    {
                        "text" => nameof(Text),
                        "name" => nameof(Name),
                        "tags" => nameof(TagChoices),
                        "allowedFrom" => nameof(FromDate),
                        _ => nameof(ToDate),
                    }, error);
                }

                OnPropertyChanged(nameof(TagsError));
                return;
            }

            if (reply?.Plate is { } plate)
            {
                Saved = plate;
                CloseRequested?.Invoke(this, true);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    protected override void OnValidationChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(TagsError));
    }

    partial void OnFromDateChanged(DateTimeOffset? value) => Validation.Validate();

    partial void OnFromTimeChanged(TimeSpan? value) => Validation.Validate();

    partial void OnToTimeChanged(TimeSpan? value) => Validation.Validate();

    partial void OnToDateChanged(DateTimeOffset? value)
    {
        if (value is null)
        {
            ToTime = null;
        }

        Validation.Validate();
    }

    private string? TextProblem()
    {
        var text = PlateText.Normalize(Text);
        if (text.Length == 0)
        {
            return PageTexts.EnterPlate;
        }

        return Problems().GetValueOrDefault("text") ?? (_taken(text) ? LicensePlateTexts.PlateExists : null);
    }

    private Dictionary<string, string> Problems() => PlateRules.Check(ToPlate(), _ => true).Errors;

    private static (DateTimeOffset? Day, TimeSpan? Time) Split(DateTime? time) =>
        time is { } t ? (new DateTimeOffset(t.Date, TimeSpan.Zero), t.TimeOfDay) : (null, null);

    private static DateTime? Join(DateTimeOffset? day, TimeSpan? time, TimeSpan fallback) =>
        day is { } d ? DateTime.SpecifyKind(d.Date + (time ?? fallback), DateTimeKind.Unspecified) : null;
}
