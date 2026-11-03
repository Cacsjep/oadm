using System.Collections.ObjectModel;
using System.ComponentModel;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>A frame rate choice of a watching reader (one radio button).</summary>
public sealed partial class FpsOption(ReaderEditorViewModel owner, int value) : ObservableObject
{
    public int Value { get; } = value;

    public string Text => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsChecked
    {
        get => owner.Fps == Value;
        set
        {
            if (value)
            {
                owner.Fps = Value;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>
/// The right card: the selected reader's form (name, camera, video source, resolution, mode, frame rate), its areas, the
/// picture of the last reading with the found lines, the results, Read now and Save (docs/spec/ocr.md "Page"). A new reader
/// starts from its camera; nothing is saved before Save. The picture is fetched with getReading, one call at a time: a
/// reading that arrives meanwhile is fetched once the call ends (newest wins), so a watching reader never queues frames.
/// </summary>
public sealed partial class ReaderEditorViewModel : ValidatingViewModel, IDisposable
{
    private static readonly Resolution[] FallbackResolutions = [new(3840, 2160), new(2688, 1512), new(1920, 1080), new(1280, 720), new(640, 360)];

    private readonly ICorePluginClientContext _ctx;
    private readonly Func<string, string, bool> _readerNameTaken;
    private readonly Func<byte[], Task<Bitmap?>> _decode;
    private string _savedJson;
    private bool _loading;
    private bool _fetching;
    private bool _fetchAgain;
    private bool _disposed;
    private Bitmap? _picture;

    public ReaderEditorViewModel(
        ICorePluginClientContext ctx,
        Reader reader,
        bool isNew,
        EngineInfo engine,
        Func<Guid, string> cameraText,
        Func<string, string, bool> readerNameTaken,
        Func<byte[], Task<Bitmap?>> decode)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(engine);
        CameraTextOf = cameraText ?? throw new ArgumentNullException(nameof(cameraText));
        _readerNameTaken = readerNameTaken ?? throw new ArgumentNullException(nameof(readerNameTaken));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        IsNew = isNew;
        MayChange = ctx.Has(OcrViewModel.ChangePermissionKey);
        FpsOptions = [.. engine.FpsChoices.Order().Select(v => new FpsOption(this, v))];

        _loading = true;
        Id = reader.Id;
        Name = reader.Name;
        DeviceId = reader.DeviceId;
        CameraText = cameraText(reader.DeviceId);
        Mode = reader.Mode;
        Fps = engine.FpsChoices.Contains(reader.Fps) ? reader.Fps : OcrLimits.DefaultFps;
        VideoSources = [new CameraSource { Number = reader.VideoSource, Name = SourceName(reader.VideoSource) }];
        SelectedSource = VideoSources[0];
        Resolutions = [reader.Resolution];
        SelectedResolution = reader.Resolution;
        foreach (var area in reader.Areas)
        {
            AddAreaViewModel(area);
        }

        _loading = false;
        Areas.CollectionChanged += (_, _) => OnAreasChanged();
        // Source, resolution and mode have no check of their own on the page: their rules only let an edit clear the
        // server's refusal below them.
        Validation
            .Rule(nameof(Name), NameError)
            .Rule(nameof(DeviceId), () => DeviceId == Guid.Empty ? ReaderRules.CameraMissing : null)
            .Rule(nameof(SelectedSource), () => null)
            .Rule(nameof(SelectedResolution), () => null)
            .Rule(nameof(Mode), () => null);
        Validation.Validate();
        _savedJson = isNew ? string.Empty : Signature();
        UpdateSaveState();
    }

    /// <summary>The reader was saved: its new state (with its id).</summary>
    public event EventHandler<Reader>? Saved;

    internal Func<Guid, string> CameraTextOf { get; }

    /// <summary>Opens the camera picker (set by the view through the page); null when cancelled.</summary>
    public Func<Guid?, Task<Guid?>>? PickCamera { get; set; }

    public bool MayChange { get; }

    /// <summary>Not saved yet (New).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadNowBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(ReadNowCommand))]
    public partial bool IsNew { get; private set; }

    [ObservableProperty]
    public partial string Id { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Guid DeviceId { get; private set; }

    /// <summary>"AXIS Q1798-LE · 10.0.0.48" on the camera button.</summary>
    [ObservableProperty]
    public partial string CameraText { get; private set; }

    /// <summary>The camera's error below its button (only after Save was tried).</summary>
    public string? CameraError => ErrorOf(nameof(DeviceId));

    [ObservableProperty]
    public partial IReadOnlyList<CameraSource> VideoSources { get; private set; }

    [ObservableProperty]
    public partial CameraSource? SelectedSource { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Resolution> Resolutions { get; private set; }

    [ObservableProperty]
    public partial Resolution SelectedResolution { get; set; }

    /// <summary>The camera's sources and sizes are being read.</summary>
    [ObservableProperty]
    public partial bool IsLoadingCamera { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatching), nameof(IsOnDemand), nameof(ResultsEmptyText))]
    public partial ReaderMode Mode { get; set; }

    public bool IsOnDemand
    {
        get => Mode == ReaderMode.OnDemand;
        set
        {
            if (value)
            {
                Mode = ReaderMode.OnDemand;
            }
        }
    }

    /// <summary>Watching: the frame rate shows.</summary>
    public bool IsWatching
    {
        get => Mode == ReaderMode.Watching;
        set
        {
            if (value)
            {
                Mode = ReaderMode.Watching;
            }
        }
    }

    [ObservableProperty]
    public partial int Fps { get; set; }

    public IReadOnlyList<FpsOption> FpsOptions { get; }

    public ObservableCollection<AreaViewModel> Areas { get; } = [];

    [ObservableProperty]
    public partial AreaViewModel? SelectedArea { get; set; }

    public string AreaCountText => OcrTexts.AreaCount(Areas.Count);

    public bool HasAreas => Areas.Count > 0;

    public static string NoAreasText => OcrTexts.NoAreas;

    /// <summary>Draw area is on: the next drag on the picture adds an area.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DrawAreaCommand))]
    public partial bool IsDrawing { get; set; }

    public bool CanAddArea => MayChange && Areas.Count < OcrLimits.MaxAreas;

    /// <summary>Why Draw area is off (its tooltip).</summary>
    public string? DrawBlockedReason => Areas.Count >= OcrLimits.MaxAreas ? ReaderRules.TooManyAreas : null;

    /// <summary>Raised to the picture: areas, selection or lines changed (the overlay draws again).</summary>
    [ObservableProperty]
    public partial int OverlayVersion { get; private set; }

    /// <summary>The picture of the last reading; null before one.</summary>
    public IImage? Picture => _picture;

    /// <summary>The picture's size (for its shape); the reader's resolution before a reading.</summary>
    [ObservableProperty]
    public partial Resolution PictureSize { get; private set; }

    /// <summary>The lines of the last reading, drawn on the picture.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TextLine> Lines { get; private set; } = [];

    public RangeObservableCollection<ResultRow> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    public string ResultsEmptyText => IsWatching && !IsNew ? OcrTexts.WaitingForPicture : OcrTexts.ReadNowHint;

    /// <summary>"Last reading 14:32:06" or "No reading yet" over the picture.</summary>
    [ObservableProperty]
    public partial string PictureText { get; private set; } = OcrTexts.NoReading;

    /// <summary>A Read now or a picture is on its way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadNowCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatus), nameof(HasSaveStatus), nameof(IsSaveStatusOk), nameof(ReadNowBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ReadNowCommand))]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatus), nameof(HasSaveStatus), nameof(IsSaveStatusOk))]
    public partial bool JustSaved { get; private set; }

    /// <summary>"Not saved" left of Save while changed, "Saved" right after a save.</summary>
    public string? SaveStatus => IsDirty ? OcrTexts.NotSaved : JustSaved ? OcrTexts.Saved : null;

    public bool HasSaveStatus => SaveStatus is not null;

    public bool IsSaveStatusOk => SaveStatus == OcrTexts.Saved;

    /// <summary>Why Save is off (its tooltip): the first error of the form or of an area.</summary>
    public string? SaveBlockedReason => FormError ?? Areas.Select(a => a.FirstError).FirstOrDefault(e => e is not null);

    /// <summary>Why Read now is off (its tooltip).</summary>
    public string? ReadNowBlockedReason => IsNew || IsDirty ? OcrTexts.SaveFirst : null;

    /// <summary>The live state of the saved reader (list row): "Camera not reachable, trying again in 30 s" or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeChipText), nameof(IsModeOk), nameof(IsModeError))]
    public partial string? Problem { get; set; }

    /// <summary>The server's problem text in either mode, else Watching · 2 fps or On demand.</summary>
    public string ModeChipText => IsModeError ? Problem! : IsWatching ? OcrTexts.WatchingAt(Fps) : OcrTexts.OnDemand;

    public bool IsModeOk => IsWatching && !IsModeError;

    public bool IsModeError => !string.IsNullOrEmpty(Problem);

    /// <summary>The readers changed on the server: a refusal about the watching limits may no longer hold.</summary>
    public void ClearModeRefusal() => Validation.SetServerError(nameof(Mode), null);

    /// <summary>The reader as it would be saved.</summary>
    public Reader ToReader() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        DeviceId = DeviceId,
        VideoSource = SelectedSource?.Number ?? 1,
        Resolution = SelectedResolution,
        Mode = Mode,
        Fps = Fps,
        Areas = [.. Areas.Select(a => a.ToArea())],
    };

    /// <summary>The camera's video sources and picture sizes (the reader's own stay chosen when the camera has them).</summary>
    public async Task LoadCameraOptionsAsync(CancellationToken ct)
    {
        if (DeviceId == Guid.Empty)
        {
            return;
        }

        IsLoadingCamera = true;
        CameraOptions? options = null;
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.GetCameraOptions, OcrJson.Serialize(new CameraOptionsRequest { DeviceId = DeviceId }), ct)
                .ConfigureAwait(true);
            options = OcrJson.Deserialize<CameraOptions>(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // An older server or a camera that does not answer: the reader's own values and the usual sizes.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            IsLoadingCamera = false;
        }

        ApplyCameraOptions(options);
    }

    /// <summary>Shows the camera's choices; null: the reader's own values and the usual sizes.</summary>
    public void ApplyCameraOptions(CameraOptions? options)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var sourceNumber = SelectedSource?.Number ?? 1;
            var sources = options?.VideoSources is { Count: > 0 } s ? s : [new CameraSource { Number = sourceNumber, Name = SourceName(sourceNumber) }];
            VideoSources = sources;
            SelectedSource = sources.FirstOrDefault(v => v.Number == sourceNumber) ?? sources[0];

            var current = SelectedResolution;
            IReadOnlyList<Resolution> sizes = options?.Resolutions is { Count: > 0 } r ? r : [.. FallbackResolutions.Append(current).Distinct().OrderByDescending(x => x.Width * x.Height)];
            Resolutions = sizes;
            SelectedResolution = sizes.Contains(current) ? current : BestOf(sizes);
        }
        finally
        {
            _loading = wasLoading;
        }

        UpdateSaveState();
    }

    /// <summary>The default size, else the camera's largest below it, else its smallest.</summary>
    internal static Resolution BestOf(IReadOnlyList<Resolution> sizes)
    {
        var limit = Resolution.Default.Width * Resolution.Default.Height;
        return sizes.Contains(Resolution.Default)
            ? Resolution.Default
            : sizes.Where(s => s.Width * s.Height <= limit).OrderByDescending(s => s.Width * s.Height).FirstOrDefault(sizes.OrderBy(s => s.Width * s.Height).First());
    }

    [RelayCommand]
    private async Task ChooseCameraAsync()
    {
        if (PickCamera is not { } pick || await pick(DeviceId == Guid.Empty ? null : DeviceId).ConfigureAwait(true) is not { } id || id == DeviceId)
        {
            return;
        }

        SetCamera(id);
        await LoadCameraOptionsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Another camera: its text, the first video source.</summary>
    internal void SetCamera(Guid id)
    {
        DeviceId = id;
        CameraText = CameraTextOf(id);
        SelectedSource = new CameraSource { Number = 1, Name = SourceName(1) };
        VideoSources = [SelectedSource];
    }

    [RelayCommand(CanExecute = nameof(CanDraw))]
    private void DrawArea() => IsDrawing = !IsDrawing;

    private bool CanDraw() => IsDrawing || CanAddArea;

    /// <summary>Esc on the picture.</summary>
    public void StopDrawing() => IsDrawing = false;

    /// <summary>A rectangle was drawn: a new area "Area 3", selected; drawing ends.</summary>
    public AreaViewModel? AddArea(RelativeBox box)
    {
        IsDrawing = false;
        if (!CanAddArea || !box.IsValid)
        {
            return null;
        }

        var number = Areas.Count + 1;
        while (Areas.Any(a => string.Equals(a.Name.Trim(), $"Area {number}", StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        var area = AddAreaViewModel(new Area { Name = $"Area {number}", Box = box });
        SelectedArea = area;
        return area;
    }

    [RelayCommand]
    private void RemoveArea(AreaViewModel? area)
    {
        if (area is null || !Areas.Contains(area))
        {
            return;
        }

        area.Changed -= OnAreaChanged;
        area.ValidityChanged -= OnAreaValidityChanged;
        Areas.Remove(area);
        if (ReferenceEquals(SelectedArea, area))
        {
            SelectedArea = Areas.LastOrDefault();
        }

        foreach (var other in Areas)
        {
            other.Revalidate();
        }
    }

    /// <summary>A click on the picture: the topmost area under the point, else none.</summary>
    public void SelectAreaAt(Point relative)
    {
        var hit = Areas.LastOrDefault(a => relative.X >= a.Box.X && relative.X <= a.Box.X + a.Box.Width && relative.Y >= a.Box.Y && relative.Y <= a.Box.Y + a.Box.Height);
        if (hit is not null)
        {
            SelectedArea = hit;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        foreach (var area in Areas)
        {
            area.ShowAllErrors();
        }

        if (!IsValidToSave())
        {
            UpdateSaveState();
            return;
        }

        IsSaving = true;
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.SaveReader, OcrJson.Serialize(new SaveReaderRequest { Reader = ToReader() }), CancellationToken.None)
                .ConfigureAwait(true);
            var reply = OcrJson.Deserialize<SaveReaderReply>(json);
            if (reply.Reader is not { } saved)
            {
                await ShowRefusalAsync(reply.Errors).ConfigureAwait(true);
                return;
            }

            Id = saved.Id;
            IsNew = false;
            _savedJson = Signature();
            JustSaved = true;
            UpdateSaveState();
            Saved?.Invoke(this, saved);
        }
#pragma warning disable CA1031 // A refusal without a field (the reader is gone, too many readers): the host's message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("Save reader", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>The server's field errors, each below its field; one that fits no field in the host's message.</summary>
    private async Task ShowRefusalAsync(IReadOnlyList<FieldError> errors)
    {
        var left = errors.Where(e => !ShowFieldError(e)).Select(e => e.Message).Distinct(StringComparer.Ordinal).ToList();
        if (left.Count > 0 || errors.Count == 0)
        {
            await _ctx.ShowMessageAsync("Save reader", left.Count > 0 ? string.Join(Environment.NewLine, left) : OcrTexts.NotSaved).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Shows a refusal below the field of its key (<see cref="OcrFields"/>): the watching limits and the frame rate below
    /// Mode, an area's below the area; false for a key the page has no field for.
    /// </summary>
    internal bool ShowFieldError(FieldError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var property = error.Field switch
        {
            OcrFields.Name => nameof(Name),
            OcrFields.Camera => nameof(DeviceId),
            OcrFields.VideoSource => nameof(SelectedSource),
            OcrFields.Resolution => nameof(SelectedResolution),
            OcrFields.Mode or OcrFields.Fps => nameof(Mode),
            _ => null,
        };
        if (property is not null)
        {
            Validation.SetServerError(property, error.Message);
            return true;
        }

        if (OcrFields.ParseArea(error.Field) is { } area && area.Index < Areas.Count)
        {
            Areas[area.Index].SetServerError(area.Part, error.Message);
            return true;
        }

        return false;
    }

    /// <summary>A refusal of the server about the mode (the watching limits), below Mode.</summary>
    public string? ModeError => ErrorOf(nameof(Mode));

    private bool CanSave() => MayChange && IsDirty && !IsSaving && IsValidToSave();

    private bool IsValidToSave() => IsFormValid && Areas.All(a => a.IsValid);

    [RelayCommand(CanExecute = nameof(CanReadNow))]
    private async Task ReadNowAsync()
    {
        IsReading = true;
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.ReadNow, OcrJson.Serialize(new ReaderRequest { ReaderId = Id }), CancellationToken.None)
                .ConfigureAwait(true);
            await ApplyReplyJsonAsync(json).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The server's reason ("Already reading", the camera's answer) in the host's message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("Read now", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsReading = false;
        }
    }

    private bool CanReadNow() => MayChange && !IsNew && !IsDirty && !IsReading;

    /// <summary>
    /// A reading of this reader arrived (live event) or the reader was opened: fetch its picture. One call at a time; a
    /// request during a call fetches once more after it (the newest reading wins).
    /// </summary>
    public async Task RefreshReadingAsync(CancellationToken ct)
    {
        if (IsNew || string.IsNullOrEmpty(Id))
        {
            return;
        }

        if (_fetching)
        {
            _fetchAgain = true;
            return;
        }

        _fetching = true;
        try
        {
            do
            {
                _fetchAgain = false;
                var json = await _ctx.InvokeAsync(OcrMethods.GetReading, OcrJson.Serialize(new ReaderRequest { ReaderId = Id }), ct).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                await ApplyReplyJsonAsync(json).ConfigureAwait(true);
            }
            while (_fetchAgain && !ct.IsCancellationRequested && !_disposed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // A missed picture: the next reading brings a new one.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _fetching = false;
        }
    }

    private async Task ApplyReplyJsonAsync(string? json)
    {
        var reply = OcrJson.Deserialize<ReadingReply>(json);
        var details = reply.Details;
        var picture = reply.Jpeg is { Length: > 0 } jpeg ? await _decode(jpeg).ConfigureAwait(true) : null;
        var cutOuts = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        foreach (var detail in details)
        {
            if (detail.Cutout is { Length: > 0 } bytes && !cutOuts.ContainsKey(detail.Area) && await _decode(bytes).ConfigureAwait(true) is { } cut)
            {
                cutOuts[detail.Area] = cut;
            }
        }

        if (_disposed)
        {
            picture?.Dispose();
            DisposeAll(cutOuts.Values);
            return;
        }

        ApplyReading(reply.Reading, details, picture, cutOuts);
    }

    /// <summary>
    /// Shows a reading with its decoded picture and the server's cut-outs by area (the editor owns the bitmaps from now on).
    /// </summary>
    public void ApplyReading(Reading? reading, IReadOnlyList<AreaDetails>? details, Bitmap? picture, IReadOnlyDictionary<string, Bitmap>? cutOuts = null)
    {
        var old = _picture;
        var oldCutOuts = _cutOuts;
        if (reading is null)
        {
            picture?.Dispose();
            DisposeAll(cutOuts?.Values);
            return;
        }

        _cutOuts = cutOuts is null ? [] : [.. cutOuts.Values];
        _picture = picture ?? (reading.Resolution == PictureSize ? old : null);
        OnPropertyChanged(nameof(Picture));
        PictureSize = reading.Resolution.Width > 0 ? reading.Resolution : SelectedResolution;
        Lines = [.. reading.Areas.SelectMany(a => a.Lines)];
        PictureText = OcrTexts.LastReading(reading.Time);

        var formats = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var area in Areas)
        {
            formats.TryAdd(area.Name.Trim(), area.IsFormat);
        }

        var rows = new List<ResultRow>(reading.Areas.Count);
        for (var i = 0; i < reading.Areas.Count; i++)
        {
            var area = reading.Areas[i];
            var detail = details?.FirstOrDefault(d => d.Area == area.Area);
            var hasFormat = formats.TryGetValue(area.Area, out var isFormat) && isFormat;
            IImage? cut = cutOuts is not null && cutOuts.TryGetValue(area.Area, out var served) ? served : CutOut(area, detail);
            rows.Add(new ResultRow(area, detail, hasFormat, reading.Time, hasFormat && area.MatchesFormat && area.Text.Length > 0 ? cut : null));
        }

        Results.ReplaceAll(rows);
        OnPropertyChanged(nameof(HasResults));
        OverlayVersion++;
        if (old is not null && !ReferenceEquals(old, _picture))
        {
            old.Dispose();
        }

        DisposeAll(oldCutOuts);
    }

    private List<Bitmap> _cutOuts = [];

    private static void DisposeAll(IEnumerable<Bitmap>? bitmaps)
    {
        foreach (var bitmap in bitmaps ?? [])
        {
            bitmap.Dispose();
        }
    }

    /// <summary>The value's piece of the picture: the server's match box, else the box around the area's lines.</summary>
    private CroppedBitmap? CutOut(AreaResult area, AreaDetails? detail)
    {
        if (_picture is not { } picture || area.Text.Length == 0)
        {
            return null;
        }

        var box = detail?.MatchBox ?? Around(area.Lines);
        if (box is not { } b || !b.IsValid)
        {
            return null;
        }

        var size = picture.PixelSize;
        var rect = new PixelRect(
            (int)Math.Floor(b.X * size.Width),
            (int)Math.Floor(b.Y * size.Height),
            Math.Max(1, (int)Math.Ceiling(b.Width * size.Width)),
            Math.Max(1, (int)Math.Ceiling(b.Height * size.Height)));
        rect = rect.Intersect(new PixelRect(size));
        return rect.Width > 0 && rect.Height > 0 ? new CroppedBitmap(picture, rect) : null;
    }

    private static RelativeBox? Around(IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var left = lines.Min(l => l.Box.X);
        var top = lines.Min(l => l.Box.Y);
        var right = lines.Max(l => l.Box.X + l.Box.Width);
        var bottom = lines.Max(l => l.Box.Y + l.Box.Height);
        return new RelativeBox(left, top, right - left, bottom - top);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Results.Clear();
        _picture?.Dispose();
        _picture = null;
        DisposeAll(_cutOuts);
        _cutOuts = [];
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CameraError));
        OnPropertyChanged(nameof(ModeError));
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null)
        {
            return;
        }

        if (e.PropertyName is nameof(Name) or nameof(DeviceId) or nameof(SelectedSource) or nameof(SelectedResolution) or nameof(Mode) or nameof(Fps))
        {
            UpdateSaveState();
        }
    }

    partial void OnModeChanged(ReaderMode value)
    {
        OnPropertyChanged(nameof(ModeChipText));
        OnPropertyChanged(nameof(IsModeOk));
        OnPropertyChanged(nameof(IsModeError));
    }

    partial void OnFpsChanged(int value)
    {
        if (!_loading)
        {
            Validation.SetServerError(nameof(Mode), null); // another rate: the refusal about it no longer holds
        }

        foreach (var option in FpsOptions)
        {
            option.Refresh();
        }

        OnPropertyChanged(nameof(ModeChipText));
    }

    partial void OnSelectedResolutionChanged(Resolution value)
    {
        if (_picture is null)
        {
            PictureSize = value;
        }
    }

    partial void OnSelectedAreaChanged(AreaViewModel? oldValue, AreaViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        newValue?.IsSelected = true;
        OverlayVersion++;
    }

    partial void OnIsDrawingChanged(bool value) => OverlayVersion++;

    private AreaViewModel AddAreaViewModel(Area area)
    {
        var vm = new AreaViewModel(area, (self, name) => Areas.Any(a => !ReferenceEquals(a, self) && string.Equals(a.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)));
        vm.Changed += OnAreaChanged;
        vm.ValidityChanged += OnAreaValidityChanged;
        Areas.Add(vm);
        return vm;
    }

    private void OnAreasChanged()
    {
        OnPropertyChanged(nameof(AreaCountText));
        OnPropertyChanged(nameof(HasAreas));
        OnPropertyChanged(nameof(CanAddArea));
        OnPropertyChanged(nameof(DrawBlockedReason));
        DrawAreaCommand.NotifyCanExecuteChanged();
        OverlayVersion++;
        UpdateSaveState();
    }

    private void OnAreaChanged(object? sender, EventArgs e)
    {
        if (sender is AreaViewModel changed)
        {
            foreach (var other in Areas.Where(a => !ReferenceEquals(a, changed)))
            {
                other.Revalidate();
            }
        }

        OverlayVersion++;
        UpdateSaveState();
    }

    private void OnAreaValidityChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSaveState()
    {
        if (_loading)
        {
            return;
        }

        var dirty = IsNew || Signature() != _savedJson;
        if (dirty)
        {
            JustSaved = false;
        }

        IsDirty = dirty;
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private string Signature() => OcrJson.Serialize(ToReader() with { Id = string.Empty });

    private string? NameError() =>
        ReaderRules.NameProblem(Name) ?? (_readerNameTaken(Id, Name.Trim()) ? ReaderRules.NameTaken(Name.Trim()) : null);

    private static string SourceName(int number) => $"View area {number}";
}
