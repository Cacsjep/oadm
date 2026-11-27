using System.Globalization;

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
    public const int WarningBelow = 75;

    /// <summary>Below this Confidence the number is in the error colour.</summary>
    public const int ErrorBelow = 60;

    internal LogRow(PlateRead read, PlateTagStore tags, TimeProvider time)
    {
        Read = read;
        var local = TimeZoneInfo.ConvertTime(read.Time, time.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date;
        TimeText = local.ToString(local.Date == today ? "HH:mm:ss" : "d MMM HH:mm:ss", CultureInfo.CurrentCulture);
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
    }

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

    [ObservableProperty] public partial string Country { get; set; } = string.Empty;

    [ObservableProperty] public partial string VehicleType { get; set; } = string.Empty;

    [ObservableProperty] public partial string VehicleColor { get; set; } = string.Empty;

    [ObservableProperty] public partial string VehicleMake { get; set; } = string.Empty;

    [ObservableProperty] public partial string VehicleModel { get; set; } = string.Empty;

    /// <summary>"Vehicle", "Vehicle: rear, car" on the flyout's button.</summary>
    public string VehicleText
    {
        get
        {
            var parts = new[] { SelectedView?.Value is null ? null : SelectedView.Name.ToLowerInvariant(), VehicleType, VehicleColor, VehicleMake, VehicleModel }
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
            return parts.Count == 0 ? PageTexts.Vehicle : PageTexts.Vehicle + ": " + string.Join(", ", parts);
        }
    }

    /// <summary>A filter other than the time range is set (Clear filters is on).</summary>
    public bool HasFilters => !Range.IsAll || !ResultChoices.All.Chosen || !TagChoices.All.Chosen || SelectedArea?.Value is not null
        || SelectedDirection?.Value is not null || SelectedView?.Value is not null || !string.IsNullOrWhiteSpace(Country)
        || !string.IsNullOrWhiteSpace(VehicleType) || !string.IsNullOrWhiteSpace(VehicleColor) || !string.IsNullOrWhiteSpace(VehicleMake)
        || !string.IsNullOrWhiteSpace(VehicleModel) || !string.IsNullOrWhiteSpace(SearchText);

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

    /// <summary>What the last export saved ("Saved: the newest 10,000 of 48,213 reads").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand), nameof(ExportPdfCommand))]
    public partial bool IsExporting { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

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
            Country = Text(Country)?.ToUpperInvariant(),
            View = SelectedView?.Value,
            VehicleType = Text(VehicleType),
            VehicleColor = Text(VehicleColor),
            VehicleMake = Text(VehicleMake),
            VehicleModel = Text(VehicleModel),
            Search = Text(SearchText),
        };
    }

    /// <summary>Reads the newest page again (a filter, the search or the camera choice changed).</summary>
    public void Reload()
    {
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

    partial void OnCountryChanged(string value) => ReloadAfterTyping();

    partial void OnVehicleTypeChanged(string value) => ReloadAfterTyping(vehicle: true);

    partial void OnVehicleColorChanged(string value) => ReloadAfterTyping(vehicle: true);

    partial void OnVehicleMakeChanged(string value) => ReloadAfterTyping(vehicle: true);

    partial void OnVehicleModelChanged(string value) => ReloadAfterTyping(vehicle: true);

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
            Country = string.Empty;
            VehicleType = string.Empty;
            VehicleColor = string.Empty;
            VehicleMake = string.Empty;
            VehicleModel = string.Empty;
            SearchText = string.Empty;
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
        Notice = null;
        ErrorText = null;
        try
        {
            ErrorText = await GuardedRun.RunAsync(async () =>
            {
                await using (target.ConfigureAwait(true))
                {
                    var total = (await _page.Client.CallAsync<CountReply>(LicensePlatesMethods.LogCount, query).ConfigureAwait(true)).Count;
                    var zone = _page.Time.LocalTimeZone;
                    var request = new LogExportRequest
                    {
                        Query = query,
                        Format = format,
                        TimeZone = zone.Id,
                        UtcOffsetMinutes = (int)zone.GetUtcOffset(_page.Time.GetUtcNow()).TotalMinutes,
                        RangeName = Range.Preset is DateRangePreset.All or DateRangePreset.Custom ? null : Range.Text,
                    };
                    var download = await _page.Client.DownloadAsync(LicensePlatesMethods.ExportLog, request).ConfigureAwait(true);
                    await using (download.ConfigureAwait(true))
                    {
                        await download.Content.CopyToAsync(target).ConfigureAwait(true);
                    }

                    Notice = PageTexts.ExportDone + ": " + (total > LicensePlateLimits.MaxExportRows
                        ? LicensePlateTexts.Newest(LicensePlateLimits.MaxExportRows, total)
                        : LicensePlateTexts.Reads(total));
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
        SetSelection([]);
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
}
