using System.Globalization;

using Avalonia;
using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// The Set up dialog of one camera (docs/spec/license-plate-center.md "Set up"): the live picture of the first sensor with the
/// two areas (3 to 8 corners each, a direction arrow; area 2 added and removed), Recognition (only the fields the camera
/// returned), the camera's allowed tags, whether the events are on and the last read, a link to ALPV's own page. Save starts
/// the task "Set up License Plate Verifier" with only what changed. Read fresh from the camera when the dialog opens.
/// </summary>
public sealed partial class SetupViewModel : ValidatingViewModel, IDisposable
{
    /// <summary>The live video of the dialog: the picture only (no channel switch: ALPV reads the first sensor).</summary>
    internal static readonly LiveVideoPlace VideoPlace = new()
    {
        ShowSourceSwitch = false,
        AllowFullscreen = false,
        AllowQualitySwitch = false,
        ShowControlsButton = false,
    };

    private readonly LicensePlatesViewModel _page;
    private readonly ILiveVideo? _video;
    private SetupRead? _read;
    private List<AreaShape> _areas = [];
    private IReadOnlyList<AreaShape> _readAreas = [];
    private int _selected = 1;

    public SetupViewModel(LicensePlatesViewModel page, CameraRowViewModel camera)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        Name = camera.Camera;
        TagChoices = new CheckChoiceList(PageTexts.AllTags, PageTexts.NoTags);
        TagChoices.Set(page.Tags.All.Select(t => new CheckChoice(t.Id, t.Name)), false, camera.Row.AllowedTags.Cast<object?>());
        _video = page.Client.Context.LiveVideo?.Create(VideoPlace);
        _video?.Start(camera.DeviceId, 1);
        Validation
            .Rule(nameof(PlateWidthMin), () => Range(PlateWidthMin, PlateWidthMax, 1, 2000))
            .Rule(nameof(PlateWidthMax), () => RangeMax(PlateWidthMin, PlateWidthMax, 1, 2000))
            .Rule(nameof(CharactersMin), () => Range(CharactersMin, CharactersMax, 1, 20))
            .Rule(nameof(CharactersMax), () => RangeMax(CharactersMin, CharactersMax, 1, 20))
            .Rule(nameof(ThresholdText), () => HasThreshold && ThresholdPercent is null ? PageTexts.ThresholdRange : null)
            .Rule(nameof(Name), NameProblem)
            .Rule(nameof(Areas), () => null);
        Validation.Validate();
    }

    public CameraRowViewModel Camera { get; }

    public string Subtitle => Camera.Camera;

    /// <summary>The live picture of the first sensor (null without the host's live video).</summary>
    public Control? VideoView => _video?.View;

    public bool HasVideo => _video is not null;

    /// <summary>The areas on the picture (a new list per change).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArea2), nameof(CanAddArea2))]
    public partial IReadOnlyList<AreaShape> Areas { get; private set; } = [];

    public bool HasArea2 => Areas.Any(a => a.Id == 2);

    public bool CanAddArea2 => !HasArea2 && MayEditAreas;

    /// <summary>A rotated picture: areas are set up on the ALPV page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MayEditAreas), nameof(CanAddArea2))]
    public partial bool Rotated { get; private set; }

    public bool MayEditAreas => !Rotated && !IsLoading;

    // ---- recognition: only the fields the camera returned show ----

    /// <summary>ALPV's regions (<c>lprUseCase</c>); a code the list does not know shows as itself.</summary>
    [ObservableProperty] public partial IReadOnlyList<CodeChoice> RegionChoices { get; private set; } = [];

    [ObservableProperty] public partial CodeChoice? SelectedRegion { get; set; }

    /// <summary>None, then the countries of the chosen region (<c>CountryPriority</c>).</summary>
    [ObservableProperty] public partial IReadOnlyList<CodeChoice> LocationChoices { get; private set; } = [];

    [ObservableProperty] public partial CodeChoice? SelectedLocation { get; set; }

    /// <summary>None, then the states of the chosen country (<c>StatePriority</c>), for the regions with states.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowState))]
    public partial IReadOnlyList<CodeChoice> StateChoices { get; private set; } = [];

    [ObservableProperty] public partial CodeChoice? SelectedState { get; set; }

    /// <summary>The values Save sends (the camera's until the user picks another).</summary>
    public string? Region { get; private set; }

    public string? CameraLocation { get; private set; }

    public string? State { get; private set; }

    /// <summary>The State select shows: the camera has the field and the region and country have states (or the camera has one).</summary>
    public bool ShowState => _read?.Recognition.State is not null && StateChoices.Count > 1;

    [ObservableProperty] public partial decimal? PlateWidthMin { get; set; }

    [ObservableProperty] public partial decimal? PlateWidthMax { get; set; }

    [ObservableProperty] public partial decimal? CharactersMin { get; set; }

    [ObservableProperty] public partial decimal? CharactersMax { get; set; }

    /// <summary>The confidence threshold in percent as typed or slid ("25"); ALPV keeps 0.2 to 1.0.</summary>
    [ObservableProperty] public partial string ThresholdText { get; set; } = string.Empty;

    public const int MinThreshold = 20;

    /// <summary>The slider's bounds and step for XAML (doubles).</summary>
    public const double SliderMinimum = MinThreshold;
    public const double SliderMaximum = MaxThreshold;
    public const double SliderStep = ThresholdStep;
    public const int MaxThreshold = 100;
    public const int ThresholdStep = 5;

    /// <summary>The threshold in whole percent, 20 to 100; null when the text is not one.</summary>
    public int? ThresholdPercent => int.TryParse(ThresholdText.Trim().TrimEnd('%').Trim(), System.Globalization.NumberStyles.Integer, CultureInfo.CurrentCulture, out var p) && p is >= MinThreshold and <= MaxThreshold ? p : null;

    /// <summary>ALPV's value (0.25) as the percent the dialog shows ("25").</summary>
    public static string PercentOf(double threshold) => Math.Round(threshold * 100).ToString("0", CultureInfo.CurrentCulture);

    /// <summary>The camera's name in License Plate Center (first field; required, unique, at most 64 characters).</summary>
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    public const int MaxNameLength = LicensePlateLimits.MaxCameraNameLength;

    /// <summary>The names of the plugin's other cameras (the uniqueness check).</summary>
    private HashSet<string> OtherNames => _page.Cameras.AllRows.Where(r => r.DeviceId != Camera.DeviceId).Select(r => r.Camera.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

    private string? NameProblem() => Name.Trim() switch
    {
        { Length: 0 } => PageTexts.EnterName,
        { Length: > MaxNameLength } => PageTexts.NameTooLong,
        var name when OtherNames.Contains(name) => LicensePlateTexts.CameraNameTaken,
        _ => null,
    };

    public bool HasRegion => _read?.Recognition.Region is not null;

    public bool HasCameraLocation => _read?.Recognition.CameraLocation is not null;

    public bool HasPlateWidth => _read?.Recognition.PlateWidthMin is not null || _read?.Recognition.PlateWidthMax is not null;

    public bool HasCharacters => _read?.Recognition.CharactersMin is not null || _read?.Recognition.CharactersMax is not null;

    public bool HasThreshold => _read?.Recognition.ConfidenceThreshold is not null;

    public CheckChoiceList TagChoices { get; }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MayEditAreas), nameof(CanAddArea2))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>A refusal of the read or the save that belongs to no field.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>A refusal of the server for the areas, directly below the picture.</summary>
    public string? AreasError => ErrorOf(nameof(Areas));

    public string? SaveBlockedReason => IsLoading ? PageTexts.ReadingCamera : IsFormValid ? null : FormError;

    public event EventHandler<bool>? CloseRequested;

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public Task SaveTask { get; private set; } = Task.CompletedTask;

    public Task LoadAsync() => LoadTask = LoadCoreAsync();

    public void Dispose() => _video?.Dispose();

    /// <summary>The picture selected an area.</summary>
    public void Select(int id)
    {
        _selected = id;
        Publish();
    }

    /// <summary>A drag on the picture ended: new corners or a new direction.</summary>
    public void Apply(AreaChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var index = _areas.FindIndex(a => a.Id == change.Id);
        if (index < 0 || !MayEditAreas)
        {
            return;
        }

        var area = _areas[index];
        _areas[index] = area with
        {
            Points = change.Points is { Count: >= AreaShape.MinPoints and <= AreaShape.MaxPoints } points ? points : area.Points,
            Angle = change.Angle ?? area.Angle,
        };
        _selected = change.Id;
        Publish();
    }

    /// <summary>The arrow keys move the selected area.</summary>
    public void Nudge(Vector by)
    {
        var index = _areas.FindIndex(a => a.Id == _selected);
        if (index >= 0 && MayEditAreas)
        {
            _areas[index] = _areas[index] with { Points = ShapeGeometry.MovePoints(_areas[index].Points, by.X, by.Y) };
            Publish();
        }
    }

    [RelayCommand]
    private void AddArea2()
    {
        if (HasArea2 || !MayEditAreas)
        {
            return;
        }

        // Area 2 exists on the camera always (inactive): its corners come back, else a box in the right half.
        var kept = _readAreas.FirstOrDefault(a => a.Id == 2);
        _areas.Add(kept ?? new AreaShape(2, [new Point(0.55, 0.3), new Point(0.9, 0.3), new Point(0.9, 0.8), new Point(0.55, 0.8)], 180, false));
        _selected = 2;
        Publish();
    }

    [RelayCommand]
    private void RemoveArea2()
    {
        _areas.RemoveAll(a => a.Id == 2);
        _selected = 1;
        Publish();
    }

    /// <summary>ALPV's own page for barrier, relay and storage, in Web View.</summary>
    [RelayCommand]
    private void OpenAlpvPage() => _page.Client.Context.OpenDevicePage(Camera.DeviceId, "/" + (_read?.AlpvPage ?? LicensePlatesInfo.AlpvPage).TrimStart('/'));

    private bool CanSave() => !IsLoading && !IsSaving && IsFormValid;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveTask = SaveCoreAsync();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(AreasError));
    }

    partial void OnPlateWidthMinChanged(decimal? value) => Validation.Validate();

    partial void OnPlateWidthMaxChanged(decimal? value) => Validation.Validate();

    partial void OnCharactersMinChanged(decimal? value) => Validation.Validate();

    partial void OnCharactersMaxChanged(decimal? value) => Validation.Validate();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(SaveBlockedReason));

    /// <summary>The task's payload: the areas when they changed, the recognition values that changed, nothing else.</summary>
    public SaveSetupRequest ToRequest()
    {
        var read = _read?.Recognition ?? new RecognitionSettings();
        var recognition = new RecognitionSettings
        {
            Region = Changed(read.Region, Region),
            CameraLocation = Changed(read.CameraLocation, CameraLocation),
            State = Changed(read.State, State),
            PlateWidthMin = Changed(read.PlateWidthMin, Whole(PlateWidthMin)),
            PlateWidthMax = Changed(read.PlateWidthMax, Whole(PlateWidthMax)),
            CharactersMin = Changed(read.CharactersMin, Whole(CharactersMin)),
            CharactersMax = Changed(read.CharactersMax, Whole(CharactersMax)),
            ConfidenceThreshold = read.ConfidenceThreshold is { } t && ThresholdPercent is { } v && Math.Abs(t - (v / 100.0)) > 0.0001 ? v / 100.0 : null,
        };
        var anyRecognition = recognition != new RecognitionSettings();
        var areas = Rotated || SameAreas(_areas, _readAreas.Where(a => a.Id == 1 || ReadArea2Active).ToList()) ? null : _areas.Select(ToAlpv).ToList();
        var tags = TagChoices.Items.Where(c => c.Chosen).Select(c => (Guid)c.Value!).ToList();
        var tagsChanged = !tags.ToHashSet().SetEquals(Camera.Row.AllowedTags);
        return new SaveSetupRequest(Camera.DeviceId, new SetupPayload { Areas = areas, Recognition = anyRecognition ? recognition : null }, tagsChanged ? tags : null);
    }

    private bool ReadArea2Active => _read?.Areas.Any(a => a.Id == 2 && a.Active) == true;

    private async Task LoadCoreAsync()
    {
        try
        {
            await _page.ListsTask.ConfigureAwait(true); // the lists of the selects (read once per page)
            SetupRead? read = null;
            ErrorText = await GuardedRun.RunAsync(async () =>
                read = await _page.Client.CallAsync<SetupRead>(LicensePlatesMethods.ReadSetup, new DeviceRequest(Camera.DeviceId)).ConfigureAwait(true)).ConfigureAwait(true);
            if (read is not null)
            {
                Use(read);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The camera's answer into the form (tests call it with a sample).</summary>
    internal void Use(SetupRead read)
    {
        _read = read;
        _readAreas = [.. read.Areas.OrderBy(a => a.Id).Select(a => new AreaShape(a.Id, [.. a.Corners.Select(c => new Point(c.X / 1000.0, c.Y / 1000.0))], a.Angle / 1000.0, false))];
        _areas = [.. _readAreas.Where(a => a.Id == 1 || read.Areas.Any(r => r.Id == a.Id && r.Active))];
        if (_areas.All(a => a.Id != 1))
        {
            _areas.Insert(0, new AreaShape(1, [new Point(0.1, 0.3), new Point(0.9, 0.3), new Point(0.9, 0.8), new Point(0.1, 0.8)], 180, false));
        }

        Rotated = read.Rotated;
        var r = read.Recognition;
        Region = r.Region;
        CameraLocation = r.CameraLocation;
        State = r.State;
        BuildRegions();
        PlateWidthMin = r.PlateWidthMin;
        PlateWidthMax = r.PlateWidthMax;
        CharactersMin = r.CharactersMin;
        CharactersMax = r.CharactersMax;
        ThresholdText = r.ConfidenceThreshold is { } t ? PercentOf(t) : string.Empty;
        TagChoices.Set(_page.Tags.All.Select(t => new CheckChoice(t.Id, t.Name)), false, read.AllowedTags.Cast<object?>());
        OnPropertyChanged(nameof(HasRegion));
        OnPropertyChanged(nameof(HasCameraLocation));
        OnPropertyChanged(nameof(ShowState));
        OnPropertyChanged(nameof(HasPlateWidth));
        OnPropertyChanged(nameof(HasCharacters));
        OnPropertyChanged(nameof(HasThreshold));
        Validation.Reset();
        Validation.Validate();
        Publish();
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
            var error = await GuardedRun.RunAsync(async () =>
            {
                // A new name first (the plugin's records, audited, the device in OADM keeps its own): a refusal shows below Name.
                if (!string.Equals(Name.Trim(), Camera.Camera, StringComparison.Ordinal))
                {
                    var renamed = await _page.Client.CallAsync<RenameCameraReply>(LicensePlatesMethods.RenameCamera, new RenameCameraRequest(Camera.DeviceId, Name.Trim())).ConfigureAwait(true);
                    if (renamed.Error is { } nameError)
                    {
                        Validation.SetServerError(nameof(Name), nameError);
                        return;
                    }
                }

                await _page.Client.CallAsync<TasksReply>(LicensePlatesMethods.SaveSetup, ToRequest()).ConfigureAwait(true);
                CloseRequested?.Invoke(this, true);
            }).ConfigureAwait(true);
            if (error is not null)
            {
                ShowServerError(error);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>A refusal names its field like "recognition.plateWidthMax: ..." only in field errors; the rest belongs to no field.</summary>
    private void ShowServerError(string error) => ErrorText = error;

    private bool _building;

    partial void OnSelectedRegionChanged(CodeChoice? value)
    {
        if (_building || value?.Value is not { } region)
        {
            return;
        }

        Region = region;
        BuildLocations(keepUnknown: false);
    }

    partial void OnSelectedLocationChanged(CodeChoice? value)
    {
        if (_building || value?.Value is not { } location)
        {
            return;
        }

        CameraLocation = location;
        BuildStates(keepUnknown: false);
    }

    partial void OnSelectedStateChanged(CodeChoice? value)
    {
        if (!_building && value?.Value is { } state)
        {
            State = state;
        }
    }

    private AlpvLists Lists => _page.Lists ?? new AlpvLists();

    private int RegionCode => int.TryParse(Region, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : -1;

    /// <summary>The regions of ALPV's list; the camera's region shows as its code when the list does not know it.</summary>
    private void BuildRegions()
    {
        var choices = Lists.Regions.Select(r => new CodeChoice(r.Code.ToString(CultureInfo.InvariantCulture), r.Name)).ToList();
        if (Region is { Length: > 0 } region && choices.All(c => c.Value != region))
        {
            choices.Insert(0, new CodeChoice(region, region));
        }

        _building = true;
        RegionChoices = choices;
        SelectedRegion = choices.FirstOrDefault(c => c.Value == Region);
        _building = false;
        BuildLocations(keepUnknown: true);
    }

    /// <summary>
    /// None and the countries of the region. The camera's own location stays shown (as its code when the list does not know
    /// it) until the user changes the region; a new region clears a location it does not offer, and the state with it.
    /// </summary>
    private void BuildLocations(bool keepUnknown)
    {
        var choices = new List<CodeChoice> { new(AlpvLists.NoCountry, PageTexts.None) };
        choices.AddRange(Lists.CountriesOf(RegionCode).Select(c => new CodeChoice(c.Code, c.Name)));
        if (CameraLocation is { Length: > 0 } location && choices.All(c => !string.Equals(c.Value, location, StringComparison.OrdinalIgnoreCase)))
        {
            if (keepUnknown)
            {
                choices.Insert(1, new CodeChoice(location, Lists.Country(location)?.Name ?? location));
            }
            else
            {
                CameraLocation = AlpvLists.NoCountry;
            }
        }

        _building = true;
        LocationChoices = choices;
        SelectedLocation = choices.FirstOrDefault(c => string.Equals(c.Value, CameraLocation, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        _building = false;
        BuildStates(keepUnknown);
    }

    /// <summary>None and the states of the country, for the regions with states; elsewhere a state no longer offered is cleared.</summary>
    private void BuildStates(bool keepUnknown)
    {
        var choices = new List<CodeChoice> { new(string.Empty, PageTexts.None) };
        choices.AddRange(Lists.StatesOf(RegionCode, CameraLocation).Select(s => new CodeChoice(s.Code, s.Name)));
        if (State is { Length: > 0 } state && choices.All(c => !string.Equals(c.Value, state, StringComparison.OrdinalIgnoreCase)))
        {
            if (keepUnknown)
            {
                choices.Add(new CodeChoice(state, state));
            }
            else
            {
                State = string.Empty;
            }
        }

        _building = true;
        StateChoices = choices;
        SelectedState = choices.FirstOrDefault(c => string.Equals(c.Value, State, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        _building = false;
    }

    private void Publish() => Areas = [.. _areas.OrderBy(a => a.Id).Select(a => a with { IsSelected = a.Id == _selected })];

    private static AlpvArea ToAlpv(AreaShape area) => new()
    {
        Id = area.Id,
        Active = true,
        Angle = (int)Math.Round(((area.Angle % 360) + 360) % 360 * 1000),
        Corners = [.. area.Points.Select(p => new AreaPoint((int)Math.Round(Math.Clamp(p.X, 0, 1) * 1000), (int)Math.Round(Math.Clamp(p.Y, 0, 1) * 1000)))],
    };

    private static bool SameAreas(List<AreaShape> a, List<AreaShape> b) =>
        a.Count == b.Count && a.OrderBy(x => x.Id).Zip(b.OrderBy(x => x.Id)).All(p => p.First.Id == p.Second.Id
            && Math.Abs(p.First.Angle - p.Second.Angle) < 0.5 && ToAlpv(p.First).Corners.SequenceEqual(ToAlpv(p.Second).Corners));

    private static int? Whole(decimal? value) => value is { } v ? (int)Math.Round(v) : null;

    private static T? Changed<T>(T? read, T? now) where T : struct => read is null || now is null || EqualityComparer<T>.Default.Equals(read.Value, now.Value) ? null : now;

    private static string? Changed(string? read, string? now) => read is null || now is null || string.Equals(read, now, StringComparison.Ordinal) ? null : now;

    private static string? Range(decimal? min, decimal? max, int lowest, int highest) =>
        min is null && max is null ? null
        : min is null or < 0 || min < lowest || min > highest ? $"Enter {lowest} to {highest:N0}."
        : null;

    private static string? RangeMax(decimal? min, decimal? max, int lowest, int highest) =>
        min is null && max is null ? null
        : max is null || max < lowest || max > highest ? $"Enter {lowest} to {highest:N0}."
        : min is { } low && max < low ? "Enter at least the smallest value."
        : null;
}
