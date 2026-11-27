using System.Globalization;
using System.Text.Json;

using Avalonia.Media;
using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>A choice of a single-value filter of the log (Area, Direction, View): its value and its text.</summary>
public sealed record FilterChoice<T>(T? Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One row of the log: a read, or a gap of the connection ("Connection lost from 13:58 to 14:06").</summary>
public sealed partial class LogRow : ObservableObject
{
    /// <summary>Below this Confidence the number is in the warning colour.</summary>
    public const int WarningBelow = ConfidenceLevels.Warning;

    /// <summary>Below this Confidence the number is in the error colour.</summary>
    public const int ErrorBelow = ConfidenceLevels.Error;

    internal LogRow(PlateRead read, PlateTagStore tags, TimeProvider time)
    {
        Read = read;
        var local = TimeZoneInfo.ConvertTime(read.Time, time.LocalTimeZone);
        TimeText = TimeOf(local); // always with the date (decision 2026-11-27)
        Camera = read.Camera;
        if (read.Gap)
        {
            PlateText = string.Empty;
            GapText = LicensePlateTexts.Gap(local, TimeZoneInfo.ConvertTime(read.Until ?? read.Time, time.LocalTimeZone));
            return;
        }

        PlateText = read.Plate;
        ReadAsTip = read.ReadAs is { Length: > 0 } readAs ? LicensePlateTexts.ReadAs(readAs) : null;
        Name = read.Name;
        Tags = tags.ChipsOfNames(read.Tags);
        ResultTip = LicensePlateTexts.Reason(read.Result, read.Reason, read.ReasonTime);
        DirectionText = LicensePlateTexts.Direction(read.Direction);
        ConfidenceText = read.Confidence.ToString(CultureInfo.CurrentCulture);
        ReadAsText = LogColumns.Text(read, LogColumns.ReadAs);
        ListEntryText = LogColumns.Text(read, LogColumns.ListEntry);
        PlateTip = Plates.PlateText.IsWildcard(read.ListEntry) ? LicensePlateTexts.Matches(read.ListEntry!) : null;
        AreaText = LogColumns.Text(read, LogColumns.Area);
        CountryText = LogColumns.Text(read, LogColumns.Country);
        StateText = LogColumns.Text(read, LogColumns.State);
        VehicleTypeText = LogColumns.Text(read, LogColumns.VehicleType);
        VehicleColorText = LogColumns.Text(read, LogColumns.VehicleColor);
        VehicleMakeText = LogColumns.Text(read, LogColumns.VehicleMake);
        VehicleModelText = LogColumns.Text(read, LogColumns.VehicleModel);
        ViewText = LogColumns.Text(read, LogColumns.View);
    }

    public string ReadAsText { get; } = string.Empty;

    /// <summary>The plate or wildcard plate of the list that decided the read; empty for an unknown plate.</summary>
    public string ListEntryText { get; } = string.Empty;

    /// <summary>"Matches AXIS12**" on the plate a wildcard plate decided; null otherwise.</summary>
    public string? PlateTip { get; }

    public string AreaText { get; } = string.Empty;

    public string CountryText { get; } = string.Empty;

    public string StateText { get; } = string.Empty;

    public string VehicleTypeText { get; } = string.Empty;

    public string VehicleColorText { get; } = string.Empty;

    public string VehicleMakeText { get; } = string.Empty;

    public string VehicleModelText { get; } = string.Empty;

    public string ViewText { get; } = string.Empty;

    /// <summary>"27 Nov 2026 14:04:14": the day and the time in this computer's zone, on every row.</summary>
    public static string TimeOf(DateTimeOffset local) => LogColumns.TimeText(local, CultureInfo.CurrentCulture);

    public PlateRead Read { get; }

    public long Id => Read.Id;

    public LogCursor Cursor => new(Read.Time.ToUnixTimeMilliseconds(), Read.Id);

    public string TimeText { get; }

    public string Camera { get; }

    /// <summary>The plate of the list, or the text the camera read when no plate matched.</summary>
    public string PlateText { get; }

    /// <summary>"Read as BN4I7HA" when the read matched with a deviation (the warning triangle's tooltip); null otherwise.</summary>
    public string? ReadAsTip { get; }

    public bool IsDeviated => ReadAsTip is not null;

    public string Name { get; } = string.Empty;

    public IReadOnlyList<TagChip> Tags { get; } = [];

    /// <summary>The result and its reason: "Not allowed, ended 31 Oct 2026".</summary>
    public string? ResultTip { get; }

    public bool IsAllowed => !IsGap && Read.Result == ReadResult.Allowed;

    public bool IsNotAllowed => !IsGap && Read.Result == ReadResult.NotAllowed;

    public bool IsUnknown => !IsGap && Read.Result == ReadResult.Unknown;

    public string DirectionText { get; } = string.Empty;

    public string ConfidenceText { get; } = string.Empty;

    /// <summary>75 and above: plain; 60 to 74: the warning colour; below 60: the error colour.</summary>
    public bool IsLowConfidence => !IsGap && Read.Confidence is >= ErrorBelow and < WarningBelow;

    public bool IsVeryLowConfidence => !IsGap && Read.Confidence < ErrorBelow;

    public bool IsPlainConfidence => !IsLowConfidence && !IsVeryLowConfidence;

    public bool IsGap => Read.Gap;

    /// <summary>"Connection lost from 13:58 to 14:06".</summary>
    public string? GapText { get; }

    public bool HasPictureToLoad => !IsGap && Read.HasPicture;

    /// <summary>The plate crop, once loaded (only for rows the grid shows).</summary>
    [ObservableProperty] public partial IImage? Picture { get; internal set; }

    internal bool PictureRequested { get; set; }
}

/// <summary>
/// The Log tab (docs/spec/license-plate-center.md "Log"): live, newest first, paged from the server (200 rows a page, older
/// pages as the grid scrolls down, newer rows on every <c>reads</c> event while not paused); the camera choice, Pause, Add to
/// plates, Export (CSV, PDF) and the filters like ALPV's Search (time, result, tags, area, direction, country, vehicle) with
/// Clear filters; the search by plate or name. Plate crops are read only for the rows the grid shows, four at a time.
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    /// <summary>The local setting of the chosen columns (this computer, like the device grid's).</summary>
    public const string ColumnsSetting = "logColumns";

    /// <summary>The columns the user chose (Choose columns), kept on this computer; the export takes them in their order.</summary>
    public GridColumnLayout Columns { get; }

    /// <summary>The saved columns; a broken or missing setting: the defaults.</summary>
    internal static IReadOnlyList<GridColumnState> LoadColumns(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<GridColumnState>>(json, LicensePlatesJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Rows of one page.</summary>
    public const int PageSize = 200;

    /// <summary>Plate crops read at the same time.</summary>
    public const int PictureParallelism = 4;

    /// <summary>The wait after the last key of a typed filter before the log is read.</summary>
    public static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(300);

    private readonly LicensePlatesViewModel _page;
    private readonly HashSet<long> _ids = [];
    private readonly Queue<LogRow> _pictureQueue = new();
    private int _picturesRunning;
    private LogCursor? _next;
    private int _version;
    private int _typingVersion;
    private bool _started;
    private bool _readingNewer;
    private bool _newerAgain;
    private DateTimeOffset _statusRead = DateTimeOffset.MinValue;

    internal LogViewModel(LicensePlatesViewModel page)
    {
        _page = page;
        Columns = new GridColumnLayout(
            [.. LogColumns.All.Select(c => new GridColumnDefinition(c.Key, c.Header, IsDefault: c.IsDefault))],
            LoadColumns(page.Client.Context.GetLocalSetting(ColumnsSetting)),
            state => page.Client.Context.SetLocalSetting(ColumnsSetting, JsonSerializer.Serialize(state.ToList(), LicensePlatesJson.Options)));
        Range = new DateRangeChoice(page.Time);
        Range.Changed += (_, _) => Reload();
        ResultChoices = new CheckChoiceList(PageTexts.AllResults, PageTexts.AllResults);
        ResultChoices.Set(
            [new CheckChoice(ReadResult.Allowed, LicensePlateTexts.Allowed), new CheckChoice(ReadResult.NotAllowed, LicensePlateTexts.NotAllowed), new CheckChoice(ReadResult.Unknown, LicensePlateTexts.Unknown)],
            true, []);
        ResultChoices.Changed += (_, _) => Reload();
        TagChoices = new CheckChoiceList(PageTexts.AllTags, PageTexts.AllTags);
        TagChoices.Set([], true, []);
        TagChoices.Changed += (_, _) => Reload();
        page.Tags.Changed += (_, _) => OnTagsChanged();
        page.CameraChoice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageDeviceChoice.Choice))
            {
                OnPropertyChanged(nameof(CameraChoiceText));
                Reload();
            }
        };
        SelectedArea = AreaChoices[0];
        SelectedDirection = DirectionChoices[0];
        SelectedView = ViewChoices[0];
        SelectedCountry = AllCountries;
        SelectedVehicleType = AnyChoice;
        SelectedVehicleColor = AnyChoice;
        SelectedMake = AnyChoice;
        SelectedModel = AnyChoice;
        page.ListsChanged += (_, _) => OnListsChanged();
        Filters = BuildFilters();
        Decode = bytes =>
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        };
    }

    public RangeObservableCollection<LogRow> Rows { get; } = [];

    public LogRow? SelectedRow { get => Selected is [var row] ? row : null; }

    public IReadOnlyList<LogRow> Selected { get; private set; } = [];

    // ---- filters ----

    public DateRangeChoice Range { get; }

    public CheckChoiceList ResultChoices { get; }

    public CheckChoiceList TagChoices { get; }

    public static IReadOnlyList<FilterChoice<int?>> AreaChoices { get; } =
        [new(null, PageTexts.AllAreas), new(1, PageTexts.Area1), new(2, PageTexts.Area2)];

    public static IReadOnlyList<FilterChoice<PlateDirection?>> DirectionChoices { get; } =
    [
        new(null, PageTexts.AllDirections), new(PlateDirection.In, LicensePlateTexts.In), new(PlateDirection.Out, LicensePlateTexts.Out),
        new(PlateDirection.Unknown, LicensePlateTexts.UnknownDirection),
    ];

    public static IReadOnlyList<FilterChoice<string>> ViewChoices { get; } =
        [new(null, PageTexts.Any), new("front", PageTexts.Front), new("rear", PageTexts.Rear)];

    [ObservableProperty] public partial FilterChoice<int?>? SelectedArea { get; set; }

    [ObservableProperty] public partial FilterChoice<PlateDirection?>? SelectedDirection { get; set; }

    [ObservableProperty] public partial FilterChoice<string>? SelectedView { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>All countries, then ALPV's countries by name (the value is the code the events carry); type to jump.</summary>
    [ObservableProperty] public partial IReadOnlyList<CodeChoice> CountryChoices { get; private set; } = [AllCountries];

    [ObservableProperty] public partial CodeChoice? SelectedCountry { get; set; }

    /// <summary>Any, then ALPV's vehicle types ("Car"; the value is ALPV's "CAR").</summary>
    [ObservableProperty] public partial IReadOnlyList<CodeChoice> VehicleTypeChoices { get; private set; } = [AnyChoice];

    [ObservableProperty] public partial CodeChoice? SelectedVehicleType { get; set; }

    [ObservableProperty] public partial IReadOnlyList<CodeChoice> VehicleColorChoices { get; private set; } = [AnyChoice];

    [ObservableProperty] public partial CodeChoice? SelectedVehicleColor { get; set; }

    /// <summary>Any, then the makes ALPV lists, read from a connected camera when the Vehicle flyout opens.</summary>
    [ObservableProperty] public partial IReadOnlyList<CodeChoice> MakeChoices { get; private set; } = [AnyChoice];

    [ObservableProperty] public partial CodeChoice? SelectedMake { get; set; }

    [ObservableProperty] public partial IReadOnlyList<CodeChoice> ModelChoices { get; private set; } = [AnyChoice];

    [ObservableProperty] public partial CodeChoice? SelectedModel { get; set; }

    /// <summary>The makes or models are read (a ring beside Make; the page never waits for it).</summary>
    [ObservableProperty] public partial bool IsLoadingNames { get; private set; }

    /// <summary>Why the makes could not be read ("No camera is connected."), below Make.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNamesProblem))]
    public partial string? NamesProblem { get; private set; }

    public bool HasNamesProblem => !string.IsNullOrEmpty(NamesProblem);

    /// <summary>A model is chosen only after a make.</summary>
    public bool CanChooseModel => SelectedMake?.Value is not null;

    /// <summary>The read of the makes or models that runs now (tests wait for it).</summary>
    public Task NamesTask { get; private set; } = Task.CompletedTask;

    private static readonly CodeChoice AllCountries = new(null, PageTexts.AllCountries);

    private static readonly CodeChoice AnyChoice = new(null, PageTexts.Any);

    /// <summary>"Vehicle", "Vehicle: rear, Car" on the flyout's button.</summary>
    public string VehicleText
    {
        get
        {
            var parts = new[] { SelectedView?.Value is null ? null : SelectedView.Name.ToLowerInvariant(), Named(SelectedVehicleType), Named(SelectedVehicleColor), Named(SelectedMake), Named(SelectedModel) }
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
            return parts.Count == 0 ? PageTexts.Vehicle : PageTexts.Vehicle + ": " + string.Join(", ", parts);
        }
    }

    /// <summary>A filter other than the time range is set (Clear filters is on).</summary>
    public bool HasFilters => !Range.IsAll || !ResultChoices.All.Chosen || !TagChoices.All.Chosen || SelectedArea?.Value is not null
        || SelectedDirection?.Value is not null || SelectedView?.Value is not null || SelectedCountry?.Value is not null
        || SelectedVehicleType?.Value is not null || SelectedVehicleColor?.Value is not null || SelectedMake?.Value is not null
        || SelectedModel?.Value is not null || !string.IsNullOrWhiteSpace(SearchText);

    private static string? Named(CodeChoice? choice) => choice?.Value is null ? null : choice.Name;

    public string CameraChoiceText => _page.CameraChoice.ButtonText;

    // ---- state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText), nameof(PauseIcon), nameof(Footer))]
    public partial bool IsPaused { get; private set; }

    public string PauseText => IsPaused ? PageTexts.Resume : PageTexts.Pause;

    public string PauseIcon => IsPaused ? "start" : "pause";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(EmptyText))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty] public partial bool IsLoadingOlder { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Footer), nameof(HasProblem), nameof(Problem))]
    public partial LogStatus? Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand), nameof(ExportPdfCommand))]
    public partial bool IsExporting { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Why reads are not stored now ("No folder for the log. Choose one in Settings.").</summary>
    public string? Problem => Status?.Problem;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    /// <summary>"Live · 1,204 reads today · Kept for 30 days, 3.1 of 20 GB used".</summary>
    public string Footer => PageTexts.LogFooter(IsPaused, Status);

    public bool ShowEmpty => !IsLoading && Rows.Count == 0;

    public string EmptyText => _page.CameraChoice.IsEmpty ? PageTexts.ChooseCamerasFirst : HasFilters ? PageTexts.NoMatchingReads : PageTexts.NoReads;

    public bool MayExport => _page.MayExport;

    public bool MayAddToPlates => _page.MayManagePlates;

    /// <summary>The reads of the page (tests wait for it).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public Task StatusTask { get; private set; } = Task.CompletedTask;

    public Task ExportTask { get; private set; } = Task.CompletedTask;

    /// <summary>Makes an image of a JPEG plate crop (tests replace it).</summary>
    public Func<byte[], IImage?> Decode { get; set; }

    /// <summary>The page is shown: the newest page and the status.</summary>
    public void Start()
    {
        _started = true;
        Reload();
        RequestStatus();
    }

    public void Stop() => _started = false;

    public void SetSelection(IEnumerable<LogRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Selected = [.. rows];
        OnPropertyChanged(nameof(SelectedRow));
        AddToPlatesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The query the filters, the search and the camera choice give now.</summary>
    public LogQuery Query()
    {
        var (from, to) = Range.Resolve();
        var choice = _page.CameraChoice;
        return new LogQuery
        {
            CameraIds = choice.Choice.Kind == DeviceChoiceKind.All ? null : [.. choice.Resolve().Select(d => d.Id)],
            From = from,
            To = to,
            Results = ResultChoices.All.Chosen ? null : [.. ResultChoices.ChosenValues.OfType<ReadResult>()],
            Tags = TagChoices.All.Chosen ? null : [.. TagChoices.ChosenValues.OfType<string>()],
            Area = SelectedArea?.Value,
            Direction = SelectedDirection?.Value,
            Country = SelectedCountry?.Value,
            View = SelectedView?.Value,
            VehicleType = SelectedVehicleType?.Value,
            VehicleColor = SelectedVehicleColor?.Value,
            VehicleMake = SelectedMake?.Value,
            VehicleModel = SelectedModel?.Value,
            Search = Text(SearchText),
        };
    }

    /// <summary>Reads the newest page again (a filter, the search or the camera choice changed).</summary>
    public void Reload()
    {
        foreach (var kind in Filters?.Kinds ?? [])
        {
            kind.Refresh();
        }

        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(EmptyText));
        ClearFiltersCommand.NotifyCanExecuteChanged();
        if (!_started)
        {
            return;
        }

        LoadTask = LoadNewestAsync(++_version);
    }

    /// <summary>The grid shows its last rows: the next older page.</summary>
    public void LoadOlder()
    {
        if (_next is null || IsLoading || IsLoadingOlder || !_started)
        {
            return;
        }

        LoadTask = LoadOlderAsync(_version, _next);
    }

    /// <summary>A <c>reads</c> event: new rows of these cameras; read them unless paused or not shown here.</summary>
    internal void OnReads(IReadOnlyList<Guid> cameras)
    {
        if (!_started)
        {
            return;
        }

        if (_page.Time.GetUtcNow() - _statusRead > TimeSpan.FromSeconds(5))
        {
            RequestStatus(); // the reads of today
        }

        var choice = _page.CameraChoice;
        if (choice.Choice.Kind != DeviceChoiceKind.All && cameras.Count > 0)
        {
            var shown = choice.Resolve().Select(d => d.Id).ToHashSet();
            if (!cameras.Any(shown.Contains))
            {
                return;
            }
        }

        ReadNewer();
    }

    /// <summary>The rows newer than the newest one shown (paused: nothing moves).</summary>
    internal void ReadNewer()
    {
        if (!_started || IsPaused || IsLoading)
        {
            return;
        }

        if (_readingNewer)
        {
            _newerAgain = true;
            return;
        }

        LoadTask = ReadNewerLoopAsync(_version);
    }

    public void RequestStatus()
    {
        if (_page.MayViewLog)
        {
            StatusTask = LoadStatusAsync();
        }
    }

    /// <summary>The grid created the row (it shows now): read its plate crop.</summary>
    public void EnsurePicture(LogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.HasPictureToLoad || row.PictureRequested)
        {
            return;
        }

        row.PictureRequested = true;
        _pictureQueue.Enqueue(row);
        PumpPictures();
    }

    /// <summary>Plate crops being read or waiting (tests).</summary>
    internal int PicturesPending => _pictureQueue.Count + _picturesRunning;

    internal void OnPermissionsChanged()
    {
        OnPropertyChanged(nameof(MayExport));
        OnPropertyChanged(nameof(MayAddToPlates));
        AddToPlatesCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportPdfCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value) => ReloadAfterTyping();

    partial void OnSelectedCountryChanged(CodeChoice? value) => Reload();

    partial void OnSelectedVehicleTypeChanged(CodeChoice? value) => VehicleChanged();

    partial void OnSelectedVehicleColorChanged(CodeChoice? value) => VehicleChanged();

    partial void OnSelectedModelChanged(CodeChoice? value) => VehicleChanged();

    partial void OnSelectedMakeChanged(CodeChoice? value)
    {
        OnPropertyChanged(nameof(CanChooseModel));
        ModelChoices = [AnyChoice];
        SelectedModel = AnyChoice;
        if (value?.Value is { } make)
        {
            NamesTask = LoadNamesAsync(make);
        }

        VehicleChanged();
    }

    private void VehicleChanged()
    {
        OnPropertyChanged(nameof(VehicleText));
        Reload();
    }

    /// <summary>The Vehicle flyout opens: the makes are read once (never blocking the page).</summary>
    public void EnsureMakes()
    {
        if (MakeChoices.Count <= 1 && !IsLoadingNames && _page.MayViewLog)
        {
            NamesTask = LoadNamesAsync(null);
        }
    }

    /// <summary>ALPV's lists arrived: the countries, vehicle types and colours of the filters.</summary>
    private void OnListsChanged()
    {
        if (_page.Lists is not { } lists)
        {
            return;
        }

        CountryChoices = [AllCountries, .. lists.Countries.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => new CodeChoice(c.Code, c.Name))];
        VehicleTypeChoices = [AnyChoice, .. lists.VehicleTypes.Select(t => new CodeChoice(t.Value, t.Name))];
        VehicleColorChoices = [AnyChoice, .. lists.VehicleColors.Select(c => new CodeChoice(c.Value, c.Name))];
        SelectedCountry = CountryChoices.FirstOrDefault(c => c.Value == SelectedCountry?.Value) ?? AllCountries;
        SelectedVehicleType = VehicleTypeChoices.FirstOrDefault(c => c.Value == SelectedVehicleType?.Value) ?? AnyChoice;
        SelectedVehicleColor = VehicleColorChoices.FirstOrDefault(c => c.Value == SelectedVehicleColor?.Value) ?? AnyChoice;
    }

    /// <summary>The makes (no <paramref name="make"/>) or the models of a make, from a connected camera of the camera choice.</summary>
    private async Task LoadNamesAsync(string? make)
    {
        IsLoadingNames = true;
        NamesProblem = null;
        try
        {
            var choice = _page.CameraChoice;
            var cameras = choice.Choice.Kind == DeviceChoiceKind.All ? null : choice.Resolve().Select(d => d.Id).ToList();
            VehicleNamesReply? reply = null;
            var error = await GuardedRun.RunAsync(async () => reply = await _page.Client.CallAsync<VehicleNamesReply>(
                make is null ? LicensePlatesMethods.VehicleMakes : LicensePlatesMethods.VehicleModels, new VehicleNamesRequest(cameras, make)).ConfigureAwait(true)).ConfigureAwait(true);
            NamesProblem = error ?? reply?.Problem;
            var names = reply?.Names ?? [];
            if (make is null)
            {
                MakeChoices = [AnyChoice, .. names.Select(n => new CodeChoice(n, n))];
                SelectedMake = MakeChoices.FirstOrDefault(c => c.Value == SelectedMake?.Value) ?? AnyChoice;
            }
            else if (SelectedMake?.Value == make)
            {
                ModelChoices = [AnyChoice, .. names.Select(n => new CodeChoice(n, n))];
                SelectedModel = AnyChoice;
            }
        }
        finally
        {
            IsLoadingNames = false;
        }
    }

    partial void OnSelectedAreaChanged(FilterChoice<int?>? value) => Reload();

    partial void OnSelectedDirectionChanged(FilterChoice<PlateDirection?>? value) => Reload();

    partial void OnSelectedViewChanged(FilterChoice<string>? value)
    {
        OnPropertyChanged(nameof(VehicleText));
        Reload();
    }

    [RelayCommand]
    private async Task ChooseCamerasAsync()
    {
        if (_page.Dialogs is { } dialogs)
        {
            await dialogs.PickCameraChoiceAsync(_page.CameraChoice).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
        {
            ReadNewer();
        }
    }

    private bool CanAddToPlates() => MayAddToPlates && SelectedRow is { IsUnknown: true };

    [RelayCommand(CanExecute = nameof(CanAddToPlates))]
    private Task AddToPlatesAsync() => SelectedRow is { } row ? _page.Plates.AddFromReadAsync(row.PlateText) : Task.CompletedTask;

    private bool CanClearFilters() => HasFilters;

    [RelayCommand(CanExecute = nameof(CanClearFilters))]
    private void ClearFilters()
    {
        var started = _started;
        _started = false; // one read at the end, not one per filter
        try
        {
            Range.Reset();
            ResultChoices.Set(ResultChoices.Items, true, []);
            TagChoices.Set(TagChoices.Items, true, []);
            SelectedArea = AreaChoices[0];
            SelectedDirection = DirectionChoices[0];
            SelectedView = ViewChoices[0];
            SelectedCountry = AllCountries;
            SelectedVehicleType = AnyChoice;
            SelectedVehicleColor = AnyChoice;
            SelectedMake = AnyChoice;
            SelectedModel = AnyChoice;
        }
        finally
        {
            _started = started;
        }

        _typingVersion++;
        OnPropertyChanged(nameof(VehicleText));
        Reload();
    }

    private bool CanExport() => MayExport && !IsExporting;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCsvAsync() => ExportTask = ExportAsync(ExportFormat.Csv);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportPdfAsync() => ExportTask = ExportAsync(ExportFormat.Pdf);

    /// <summary>
    /// The server makes the file of exactly what the filters show (newest first, at most 10,000) and streams it to the save
    /// dialog's file; the notice names the count ("The newest 10,000 of 48,213 reads" when there were more).
    /// </summary>
    private async Task ExportAsync(ExportFormat format)
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        var query = Query();
        var name = "plate-log-" + _page.Time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (format == ExportFormat.Pdf ? ".pdf" : ".csv");
        if (await dialogs.SaveFileAsync(PageTexts.ExportTitle, name).ConfigureAwait(true) is not { } target)
        {
            return;
        }

        IsExporting = true;
        ErrorText = null;
        try
        {
            ErrorText = await GuardedRun.RunAsync(async () =>
            {
                await using (target.ConfigureAwait(true))
                {
                    var zone = _page.Time.LocalTimeZone;
                    var request = new LogExportRequest
                    {
                        Query = query,
                        Format = format,
                        TimeZone = zone.Id,
                        UtcOffsetMinutes = (int)zone.GetUtcOffset(_page.Time.GetUtcNow()).TotalMinutes,
                        RangeName = Range.Preset is DateRangePreset.All or DateRangePreset.Custom ? null : Range.Text,
                        Columns = Columns.VisibleKeys,
                    };
                    var download = await _page.Client.DownloadAsync(LicensePlatesMethods.ExportLog, request).ConfigureAwait(true);
                    await using (download.ConfigureAwait(true))
                    {
                        await download.Content.CopyToAsync(target).ConfigureAwait(true);
                    }

                    // No status line after a saved export (decision 2026-11-27): the save dialog is enough; the file's head says
                    // "The newest 10,000 of 48,213 reads" when there were more.
                }
            }).ConfigureAwait(true);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private void ReloadAfterTyping(bool vehicle = false)
    {
        if (vehicle)
        {
            OnPropertyChanged(nameof(VehicleText));
        }

        OnPropertyChanged(nameof(HasFilters));
        ClearFiltersCommand.NotifyCanExecuteChanged();
        if (!_started)
        {
            return;
        }

        var version = ++_typingVersion;
        _ = WaitThenReloadAsync(version);
    }

    private async Task WaitThenReloadAsync(int version)
    {
        await Task.Delay(TypingDelay, _page.Time).ConfigureAwait(true);
        if (version == _typingVersion)
        {
            Reload();
        }
    }

    private async Task LoadNewestAsync(int version)
    {
        if (_page.CameraChoice.IsEmpty)
        {
            Set([], null);
            return;
        }

        IsLoading = true;
        ErrorText = null;
        try
        {
            var error = await GuardedRun.RunAsync(async () =>
            {
                var page = await _page.Client.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest { Query = Query(), Limit = PageSize }).ConfigureAwait(true);
                if (version == _version)
                {
                    Set(page.Rows, page.Next);
                }
            }).ConfigureAwait(true);
            if (version == _version)
            {
                ErrorText = error;
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    private async Task LoadOlderAsync(int version, LogCursor before)
    {
        IsLoadingOlder = true;
        try
        {
            var error = await GuardedRun.RunAsync(async () =>
            {
                var page = await _page.Client.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest { Query = Query(), Before = before, Limit = PageSize }).ConfigureAwait(true);
                if (version != _version)
                {
                    return;
                }

                var rows = page.Rows.Where(r => _ids.Add(r.Id)).Select(Row).ToList();
                Rows.AddRange(rows);
                _next = page.Next;
            }).ConfigureAwait(true);
            if (error is not null && version == _version)
            {
                ErrorText = error;
            }
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    private async Task ReadNewerLoopAsync(int version)
    {
        _readingNewer = true;
        try
        {
            do
            {
                _newerAgain = false;
                if (Rows.Count == 0)
                {
                    await LoadNewestAsync(version).ConfigureAwait(true);
                    continue;
                }

                var newest = Rows[0].Cursor;
                var error = await GuardedRun.RunAsync(async () =>
                {
                    var page = await _page.Client.CallAsync<LogPage>(LicensePlatesMethods.Log,
                        new LogPageRequest { Query = Query(), After = newest, Limit = LicensePlateLimits.MaxLogPage }).ConfigureAwait(true);
                    if (version != _version || IsPaused)
                    {
                        return;
                    }

                    if (page.Rows.Count >= LicensePlateLimits.MaxLogPage)
                    {
                        // A full page means more: the newest page again.
                        await LoadNewestAsync(version).ConfigureAwait(true);
                        return;
                    }

                    var rows = page.Rows.Where(r => _ids.Add(r.Id)).Select(Row).ToList();
                    if (rows.Count > 0)
                    {
                        Rows.InsertRange(0, rows);
                        OnPropertyChanged(nameof(ShowEmpty));
                    }
                }).ConfigureAwait(true);
                if (error is not null && version == _version)
                {
                    ErrorText = error;
                }
            }
            while (_newerAgain && version == _version && !IsPaused);
        }
        finally
        {
            _readingNewer = false;
        }
    }

    private async Task LoadStatusAsync()
    {
        _statusRead = _page.Time.GetUtcNow();
        var error = await GuardedRun.RunAsync(async () =>
            Status = await _page.Client.CallAsync<LogStatus>(LicensePlatesMethods.LogStatus, null).ConfigureAwait(true)).ConfigureAwait(true);
        if (error is not null)
        {
            ErrorText = error;
        }
    }

    private void Set(IReadOnlyList<PlateRead> reads, LogCursor? next)
    {
        _ids.Clear();
        _pictureQueue.Clear();
        var rows = new List<LogRow>(reads.Count);
        foreach (var read in reads)
        {
            if (_ids.Add(read.Id))
            {
                rows.Add(Row(read));
            }
        }

        Rows.ReplaceAll(rows);
        _next = next;
        var shown = rows.ToHashSet();
        SetSelection(Selected.Where(shown.Contains)); // what the grid shows selected after the new rows, never the old ones
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private LogRow Row(PlateRead read) => new(read, _page.Tags, _page.Time);

    private void PumpPictures()
    {
        while (_picturesRunning < PictureParallelism && _pictureQueue.TryDequeue(out var row))
        {
            _picturesRunning++;
            _ = LoadPictureAsync(row);
        }
    }

    private async Task LoadPictureAsync(LogRow row)
    {
        try
        {
            await GuardedRun.RunAsync(async () =>
            {
                var reply = await _page.Client.CallAsync<PictureReply>(LicensePlatesMethods.Picture, new ReadIdRequest(row.Id)).ConfigureAwait(true);
                if (reply.Jpeg is { Length: > 0 } jpeg)
                {
                    row.Picture = Decode(Convert.FromBase64String(jpeg));
                }
            }).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A picture that cannot be shown leaves its cell empty.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _picturesRunning--;
            PumpPictures();
        }
    }

    private void OnTagsChanged()
    {
        var chosen = TagChoices.All.Chosen ? [] : TagChoices.ChosenValues.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        TagChoices.Set(_page.Tags.All.Select(t => new CheckChoice(t.Name, t.Name)), TagChoices.All.Chosen || chosen.Count == 0, chosen.Cast<object?>());
    }

    private static string? Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The filters behind the Filter button and their chips (decision 2026-11-27): Time, Result, Tags, Area, Direction, Country, Vehicle.</summary>
    public FilterSet Filters { get; }

    private FilterSet BuildFilters()
    {
        var time = new FilterKind(PageTexts.Time, Range, () => !Range.IsAll, () => Range.Text, Range.Reset);
        time.Opening += (_, _) => Range.OnOpening();
        Range.Changed += (_, _) => time.RequestClose(); // a preset or Apply closes the editor
        var area = new ChoiceFilter(() => AreaChoices, () => SelectedArea, v => SelectedArea = v as FilterChoice<int?>);
        var direction = new ChoiceFilter(() => DirectionChoices, () => SelectedDirection, v => SelectedDirection = v as FilterChoice<PlateDirection?>);
        var country = new ChoiceFilter(() => CountryChoices, () => SelectedCountry, v => SelectedCountry = v as CodeChoice);
        var kinds = new List<FilterKind>
        {
            time,
            new(PageTexts.Result, ResultChoices, () => !ResultChoices.All.Chosen, () => PageTexts.Result + ": " + ResultChoices.Summary, () => ResultChoices.Set(ResultChoices.Items, true, [])),
            new(PageTexts.Tags, TagChoices, () => !TagChoices.All.Chosen, () => PageTexts.Tags + ": " + TagChoices.Summary, () => TagChoices.Set(TagChoices.Items, true, [])),
            Chosen(PageTexts.Area, area, () => SelectedArea?.Value is not null, () => SelectedArea?.Name, () => SelectedArea = AreaChoices[0]),
            Chosen(PageTexts.Direction, direction, () => SelectedDirection?.Value is not null, () => PageTexts.Direction + ": " + SelectedDirection?.Name, () => SelectedDirection = DirectionChoices[0]),
            Chosen(PageTexts.Country, country, () => SelectedCountry?.Value is not null, () => PageTexts.Country + ": " + SelectedCountry?.Name, () => SelectedCountry = AllCountries),
        };
        var vehicle = new FilterKind(PageTexts.Vehicle, new VehicleFilter(this), () => VehicleText != PageTexts.Vehicle, () => VehicleText, () =>
        {
            SelectedView = ViewChoices[0];
            SelectedVehicleType = AnyChoice;
            SelectedVehicleColor = AnyChoice;
            SelectedMake = AnyChoice;
            SelectedModel = AnyChoice;
        });
        vehicle.Opening += (_, _) => EnsureMakes();
        kinds.Add(vehicle);
        return new FilterSet(kinds, ClearFilters);

        static FilterKind Chosen(string name, ChoiceFilter editor, Func<bool> isSet, Func<string?> text, Action clear)
        {
            var kind = new FilterKind(name, editor, isSet, () => text() ?? name, clear);
            editor.Chosen += (_, _) => kind.RequestClose(); // a choice applies at once
            kind.Opening += (_, _) => editor.Refresh(); // the countries arrive with ALPV's lists
            return kind;
        }
    }
}

/// <summary>The editor of a filter with one choice (Area, Direction, Country): the choices and the chosen one.</summary>
public sealed class ChoiceFilter(Func<IEnumerable<object>> choices, Func<object?> get, Action<object?> set) : ObservableObject
{
    public IReadOnlyList<object> Choices => [.. choices()];

    public object? Selected
    {
        get => get();
        set
        {
            if (Equals(value, get()) || value is null)
            {
                return;
            }

            set(value);
            OnPropertyChanged();
            Chosen?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The user picked a choice.</summary>
    public event EventHandler? Chosen;

    /// <summary>The choices and the chosen one again (the editor opens).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Selected));
    }
}

/// <summary>The editor of the Vehicle filter: View, Type, Colour, Make and Model of the log.</summary>
public sealed class VehicleFilter(LogViewModel log)
{
    public LogViewModel Log { get; } = log;
}
