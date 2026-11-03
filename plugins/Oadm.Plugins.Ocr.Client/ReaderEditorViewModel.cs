using System.Collections.ObjectModel;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
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

/// <summary>
/// The selected reader (docs/spec/ocr.md "Page"): the camera's live video with the areas and the found lines over it, and
/// the form (name, mode, the stream settings in their dialog), its areas, the results and Save. A new reader starts from its
/// camera, which stays (no camera change: a new reader instead); nothing is saved before Save. While the page shows the reader (<see cref="StartPreview"/>) the server
/// reads it live as a preview with the draft as it is now (saved or not, no rule events): the draft goes to the server on
/// every change and every <see cref="OcrLimits.PreviewKeepAlive"/>; the preview's readings are fetched one call at a time
/// (a reading that arrives meanwhile is fetched once the call ends, newest wins).
/// </summary>
public sealed partial class ReaderEditorViewModel : ValidatingViewModel, IDisposable
{
    private static readonly Resolution[] FallbackResolutions = [new(3840, 2160), new(2688, 1512), new(1920, 1080), new(1280, 720), new(640, 360)];

    /// <summary>The live video of the page: the picture only, nothing that would sit under the areas or leave them behind.</summary>
    internal static readonly LiveVideoPlace VideoPlace = new()
    {
        ShowSourceSwitch = false,
        AllowFullscreen = false,
        AllowQualitySwitch = false,
        ShowControlsButton = false,
    };

    private readonly ICorePluginClientContext _ctx;
    private readonly Func<string, string, bool> _readerNameTaken;
    private readonly Func<byte[], Task<Bitmap?>> _decode;
    private readonly ISet<string> _onActionNoticeShown;
    private readonly string _draftKey = "new:" + Guid.NewGuid().ToString("N");
    private readonly ILiveVideo? _video;
    private string _savedJson;
    private bool _loading;
    private bool _fetching;
    private bool _fetchAgain;
    private bool _sending;
    private bool _sendAgain;
    private int _sendGeneration;
    private long _readSeq;
    private long _shownSeq;
    private bool _disposed;
    private CancellationTokenSource? _preview;
    private (Guid Device, int Source)? _shown;

    public ReaderEditorViewModel(
        ICorePluginClientContext ctx,
        Reader reader,
        bool isNew,
        EngineInfo engine,
        Func<Guid, string> cameraText,
        Func<string, string, bool> readerNameTaken,
        Func<byte[], Task<Bitmap?>> decode,
        ISet<string>? onActionNoticeShown = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(engine);
        CameraTextOf = cameraText ?? throw new ArgumentNullException(nameof(cameraText));
        _readerNameTaken = readerNameTaken ?? throw new ArgumentNullException(nameof(readerNameTaken));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _onActionNoticeShown = onActionNoticeShown ?? new HashSet<string>(StringComparer.Ordinal);
        IsNew = isNew;
        MayChange = ctx.Has(OcrViewModel.ChangePermissionKey);
        FpsChoices = [.. engine.FpsChoices.Order()];

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
            .Rule(nameof(SelectedSource), () => null)
            .Rule(nameof(SelectedResolution), () => null)
            .Rule(nameof(Fps), () => null)
            .Rule(nameof(Mode), () => null);
        Validation.Validate();
        _savedJson = isNew ? string.Empty : Signature();
        UpdateSaveState();

        _video = ctx.LiveVideo?.Create(VideoPlace);
        ShowVideo();
    }

    /// <summary>The reader was saved: its new state (with its id).</summary>
    public event EventHandler<Reader>? Saved;

    internal Func<Guid, string> CameraTextOf { get; }

    /// <summary>Shows the Stream settings and Rename dialogs (set by the view through the page).</summary>
    public IReaderDialogs? Dialogs { get; set; }

    /// <summary>The keep-alive's clock (tests pass a fake one).</summary>
    public TimeProvider PreviewTime { get; set; } = TimeProvider.System;

    public bool MayChange { get; }

    /// <summary>Not saved yet (New).</summary>
    [ObservableProperty]
    public partial bool IsNew { get; private set; }

    [ObservableProperty]
    public partial string Id { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Guid DeviceId { get; private set; }

    /// <summary>"AXIS Q1798-LE · 10.0.0.48": the reader's camera (shown in the reader list).</summary>
    [ObservableProperty]
    public partial string CameraText { get; private set; }

    /// <summary>"View area 1 · 1920x1080 · 4 fps" below Stream settings (no frame rate for On action).</summary>
    public string StreamSummary => OcrTexts.StreamSummary(SelectedResolution, IsWatching ? Fps : null);

    /// <summary>A refusal of the server for the video source, the resolution or the frame rate, in the summary's place.</summary>
    public string? StreamError => ErrorOf(nameof(SelectedSource)) ?? ErrorOf(nameof(SelectedResolution)) ?? ErrorOf(nameof(Fps));

    [ObservableProperty]
    public partial IReadOnlyList<CameraSource> VideoSources { get; private set; }

    [ObservableProperty]
    public partial CameraSource? SelectedSource { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Resolution> Resolutions { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PictureSize))]
    public partial Resolution SelectedResolution { get; set; }

    /// <summary>The camera's sources and sizes are being read.</summary>
    [ObservableProperty]
    public partial bool IsLoadingCamera { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatching), nameof(IsOnAction), nameof(ModeChoice))]
    public partial ReaderMode Mode { get; set; }

    /// <summary>The Mode select's choices, in this order.</summary>
    public static IReadOnlyList<string> ModeChoices { get; } = [OcrTexts.Watching, OcrTexts.OnAction];

    /// <summary>The Mode select: "Watching" or "On action".</summary>
    public string ModeChoice
    {
        get => IsOnAction ? OcrTexts.OnAction : OcrTexts.Watching;
        set => Mode = value == OcrTexts.OnAction ? ReaderMode.OnAction : ReaderMode.Watching;
    }

    /// <summary>On action: reads only when an event rule runs Read text.</summary>
    public bool IsOnAction
    {
        get => Mode == ReaderMode.OnAction;
        set
        {
            if (value)
            {
                Mode = ReaderMode.OnAction;
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

    /// <summary>The frame rates offered now (the select of Frames per second).</summary>
    public IReadOnlyList<int> FpsChoices { get; }

    public ObservableCollection<AreaViewModel> Areas { get; } = [];

    [ObservableProperty]
    public partial AreaViewModel? SelectedArea { get; set; }

    public bool HasAreas => Areas.Count > 0;

    /// <summary>
    /// No area yet and not drawing: the live video is dimmed with "Start by drawing an area" (text is read only inside
    /// areas, the preview reads nothing).
    /// </summary>
    public bool ShowStartHint => !HasAreas && !IsDrawing;

    public static string NoAreasText => OcrTexts.NoAreas;

    /// <summary>Drawing (New in the Areas header): the next drag on the live video adds an area.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewAreaCommand))]
    [NotifyPropertyChangedFor(nameof(ShownLines), nameof(ShowStartHint))]
    public partial bool IsDrawing { get; set; }

    /// <summary>An area is being moved or resized on the live video (the lines are hidden meanwhile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownLines))]
    public partial bool IsAdjustingArea { get; private set; }

    /// <summary>The live video's overlay started (true) or ended (false) a move or resize of an area.</summary>
    public void SetAdjusting(bool adjusting) => IsAdjustingArea = adjusting;

    public bool CanAddArea => MayChange && Areas.Count < OcrLimits.MaxAreas;

    /// <summary>New's tooltip in the Areas header: what it does, or why it is off (8 areas).</summary>
    public string NewAreaTip => Areas.Count >= OcrLimits.MaxAreas ? ReaderRules.TooManyAreas : OcrTexts.NewAreaTip;

    /// <summary>Raised to the overlay: areas, selection or lines changed (it draws again).</summary>
    [ObservableProperty]
    public partial int OverlayVersion { get; private set; }

    /// <summary>The live video of the reader's camera and video source; null when the client has none (an older OADM).</summary>
    public Control? VideoView => _video?.View;

    public bool HasLiveVideo => _video is not null;

    /// <summary>The picture's shape for the overlay: the reader's resolution.</summary>
    public Resolution PictureSize => SelectedResolution;

    /// <summary>The lines of the latest reading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownLines))]
    public partial IReadOnlyList<TextLine> Lines { get; private set; } = [];

    /// <summary>The lines drawn over the live video: none while an area is drawn, moved or resized.</summary>
    public IReadOnlyList<TextLine> ShownLines => IsDrawing || IsAdjustingArea ? [] : Lines;

    /// <summary>The latest reading's note ("More than 30 texts found. ..."); null without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadingNote))]
    public partial string? ReadingNote { get; private set; }

    public bool HasReadingNote => !string.IsNullOrEmpty(ReadingNote);

    public RangeObservableCollection<ResultRow> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    /// <summary>Below the results while there are none: why the preview reads nothing, else that it waits (nothing without areas: the video says it).</summary>
    public string ResultsEmptyText => PreviewProblem ?? (HasAreas ? OcrTexts.WaitingForPicture : string.Empty);

    /// <summary>"Last reading 14:32:06" below the live video, or "Waiting for the first picture."</summary>
    [ObservableProperty]
    public partial string PictureText { get; private set; } = OcrTexts.WaitingForPicture;

    /// <summary>
    /// This page's preview of the reader; null while there is none. The page makes the id itself and sends it with every
    /// call, so it can always end the preview it started (also one whose start answer it never saw).
    /// </summary>
    public string? PreviewId { get; private set; }

    /// <summary>Why the preview reads nothing now (the camera, the limits); null while it reads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultsEmptyText), nameof(HasPreviewProblem))]
    public partial string? PreviewProblem { get; private set; }

    public bool HasPreviewProblem => PreviewProblem is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatus), nameof(HasSaveStatus), nameof(IsSaveStatusOk))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
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

    /// <summary>The live state of the saved reader (list row): "Camera not reachable, trying again in 30 s" or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeChipText), nameof(IsModeOk), nameof(IsModeError))]
    public partial string? Problem { get; set; }

    /// <summary>The server's problem text in either mode, else Watching · 2 fps or On action.</summary>
    public string ModeChipText => IsModeError ? Problem! : IsWatching ? OcrTexts.WatchingAt(Fps) : OcrTexts.OnAction;

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
        ShowVideo();
    }

    /// <summary>The default size, else the camera's largest below it, else its smallest.</summary>
    internal static Resolution BestOf(IReadOnlyList<Resolution> sizes)
    {
        var limit = Resolution.Default.Width * Resolution.Default.Height;
        return sizes.Contains(Resolution.Default)
            ? Resolution.Default
            : sizes.Where(s => s.Width * s.Height <= limit).OrderByDescending(s => s.Width * s.Height).FirstOrDefault(sizes.OrderBy(s => s.Width * s.Height).First());
    }

    /// <summary>
    /// Stream settings: the dialog with the video source, the resolution and (Watching) the frame rate; OK hands them to the
    /// reader (Not saved like any edit, the preview follows), Cancel changes nothing.
    /// </summary>
    [RelayCommand]
    private async Task EditStreamAsync()
    {
        if (Dialogs is not { } dialogs)
        {
            return;
        }

        var form = new StreamSettingsViewModel(
            VideoSources,
            SelectedSource,
            Resolutions,
            SelectedResolution,
            FpsChoices,
            Fps,
            IsWatching,
            IsLoadingCamera,
            new Dictionary<string, string?>
            {
                [nameof(StreamSettingsViewModel.SelectedSource)] = ErrorOf(nameof(SelectedSource)),
                [nameof(StreamSettingsViewModel.SelectedResolution)] = ErrorOf(nameof(SelectedResolution)),
                [nameof(StreamSettingsViewModel.Fps)] = ErrorOf(nameof(Fps)),
            });
        if (!await dialogs.EditStreamAsync(form).ConfigureAwait(true))
        {
            return;
        }

        if (form.SelectedSource is { } source && !Equals(source, SelectedSource))
        {
            SelectedSource = source;
        }

        SelectedResolution = form.SelectedResolution;
        if (IsWatching)
        {
            Fps = form.Fps;
        }
    }

    /// <summary>Rename (the tag icon in an area's row): the dialog with the area's name; Rename sets it.</summary>
    [RelayCommand]
    private async Task RenameAreaAsync(AreaViewModel? area)
    {
        if (area is null || !Areas.Contains(area) || !MayChange || Dialogs is not { } dialogs)
        {
            return;
        }

        var form = new RenameAreaViewModel(area.Name, area.IsNameTaken);
        if (await dialogs.RenameAreaAsync(form).ConfigureAwait(true) && Areas.Contains(area))
        {
            area.Name = form.Name.Trim();
            SelectedArea = area;
        }
    }

    /// <summary>The live video shows the reader's camera and video source (once per change).</summary>
    private void ShowVideo()
    {
        if (_video is null || _disposed || DeviceId == Guid.Empty)
        {
            return;
        }

        var wanted = (DeviceId, SelectedSource?.Number ?? 1);
        if (_shown != wanted)
        {
            _shown = wanted;
            _video.Start(wanted.DeviceId, wanted.Item2);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDraw))]
    private void NewArea() => IsDrawing = !IsDrawing;

    private bool CanDraw() => IsDrawing || CanAddArea;

    /// <summary>Esc on the live video.</summary>
    public void StopDrawing() => IsDrawing = false;

    /// <summary>
    /// A rectangle was drawn: a new area named with the lowest free number ("Area 2" when Area 2 was deleted; the others keep
    /// theirs, rules name them), selected; drawing ends.
    /// </summary>
    public AreaViewModel? AddArea(RelativeBox box)
    {
        IsDrawing = false;
        if (!CanAddArea || !box.IsValid)
        {
            return null;
        }

        var number = 1;
        while (Areas.Any(a => string.Equals(a.Name.Trim(), $"Area {number}", StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        var area = AddAreaViewModel(new Area { Name = $"Area {number}", Box = box });
        SelectedArea = area;
        return area;
    }

    /// <summary>
    /// A move or resize of <paramref name="area"/> on the live video ended at <paramref name="box"/>; held inside the
    /// picture with the minimum size (<see cref="AreaGeometry"/>). The reader is Not saved like after any other edit.
    /// </summary>
    public void SetAreaBox(AreaViewModel area, RelativeBox box)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (!MayChange || !Areas.Contains(area))
        {
            return;
        }

        var width = Math.Clamp(box.Width, AreaGeometry.MinSize, 1);
        var height = Math.Clamp(box.Height, AreaGeometry.MinSize, 1);
        area.Box = AreaGeometry.Move(new RelativeBox(box.X, box.Y, width, height), 0, 0);
        SelectedArea = area;
    }

    /// <summary>Moves (<see cref="AreaHandle.Move"/>) or resizes <paramref name="area"/> by a drag of (dx, dy), relative to the picture.</summary>
    public void EditArea(AreaViewModel area, AreaHandle handle, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (handle != AreaHandle.None)
        {
            SetAreaBox(area, AreaGeometry.Resize(area.Box, handle, dx, dy));
        }
    }

    /// <summary>An arrow key: the selected area moves by (dx, dy), held inside the picture.</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (SelectedArea is { } area)
        {
            EditArea(area, AreaHandle.Move, dx, dy);
        }
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

    /// <summary>A click on the live video: the topmost area under the point, else none.</summary>
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

            if (IsNew && _onActionNoticeShown.Contains(_draftKey))
            {
                _onActionNoticeShown.Add(saved.Id);
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
    /// Shows a refusal below the field of its key (<see cref="OcrFields"/>): the watching limits below Mode, the video source,
    /// resolution and frame rate below Stream settings, an area's below the area; false for a key the page has no field for
    /// (the camera: it never changes on the page).
    /// </summary>
    internal bool ShowFieldError(FieldError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var property = error.Field switch
        {
            OcrFields.Name => nameof(Name),
            OcrFields.VideoSource => nameof(SelectedSource),
            OcrFields.Resolution => nameof(SelectedResolution),
            OcrFields.Fps => nameof(Fps),
            OcrFields.Mode => nameof(Mode),
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

    /// <summary>
    /// The page shows the reader: the server previews it with the draft now, again on every change and every
    /// <see cref="OcrLimits.PreviewKeepAlive"/> until <see cref="StopPreview"/>.
    /// </summary>
    public void StartPreview()
    {
        if (_preview is not null || _disposed || DeviceId == Guid.Empty)
        {
            return;
        }

        _preview = new CancellationTokenSource();
        _ = KeepPreviewAsync(_preview.Token);
    }

    /// <summary>The page hides the reader: the keep-alive stops and the server ends the preview.</summary>
    public void StopPreview()
    {
        if (_preview is { } preview)
        {
            _preview = null;
            preview.Cancel();
            preview.Dispose();
        }

        // A send still on its way never blocks the next start: it belongs to the old generation.
        _sendGeneration++;
        _sending = false;
        _sendAgain = false;
        if (PreviewId is { } id)
        {
            PreviewId = null;
            _ = EndPreviewAsync(id);
        }
    }

    /// <summary>A preview is running for this page (tests).</summary>
    public bool IsPreviewing => _preview is not null;

    private async Task EndPreviewAsync(string id)
    {
        try
        {
            await _ctx.InvokeAsync(OcrMethods.StopPreview, OcrJson.Serialize(new PreviewRequest { PreviewId = id }), CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The server ends it anyway when its lease runs out.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task KeepPreviewAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendPreviewAsync(ct).ConfigureAwait(true);
            try
            {
                await Task.Delay(OcrLimits.PreviewKeepAlive, PreviewTime, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends the draft to the preview (starts it, keeps it alive, takes the change). One call at a time; a change during a
    /// call sends once more after it.
    /// </summary>
    public async Task SendPreviewAsync(CancellationToken ct)
    {
        if (_sending)
        {
            _sendAgain = true;
            return;
        }

        _sending = true;
        var generation = _sendGeneration;
        try
        {
            do
            {
                _sendAgain = false;
                var id = PreviewId ??= Guid.NewGuid().ToString("N");
                var request = new PreviewRequest { PreviewId = id, Reader = ToReader() };
                var json = await _ctx.InvokeAsync(OcrMethods.StartPreview, OcrJson.Serialize(request), ct).ConfigureAwait(true);
                var reply = OcrJson.Deserialize<PreviewReply>(json);
                if (_disposed || ct.IsCancellationRequested || generation != _sendGeneration)
                {
                    // Stopped meanwhile: the start may have reached the server after the stop, so end it again.
                    _ = EndPreviewAsync(reply.PreviewId.Length > 0 ? reply.PreviewId : id);
                    return;
                }

                PreviewId = reply.PreviewId.Length > 0 ? reply.PreviewId : id;
                PreviewProblem = reply.Problem;
            }
            while (_sendAgain && !ct.IsCancellationRequested && !_disposed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The server's reason (too many previews, the camera) where the results would be.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_disposed)
            {
                PreviewProblem = ServerErrors.Message(ex);
            }
        }
        finally
        {
            if (generation == _sendGeneration)
            {
                _sending = false;
            }
        }
    }

    /// <summary>The draft changed: the preview takes it now (while it runs).</summary>
    private void SendDraft()
    {
        if (_preview is { } preview && !_loading)
        {
            _ = SendPreviewAsync(preview.Token);
        }
    }

    /// <summary>
    /// A reading of this page's preview arrived (live event): fetch it with its cut-outs. One call at a time; a request
    /// during a call fetches once more after it (the newest reading wins).
    /// </summary>
    public async Task RefreshPreviewAsync(CancellationToken ct)
    {
        if (PreviewId is null)
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
                if (PreviewId is not { } id)
                {
                    return;
                }

                var seq = ++_readSeq;
                var json = await _ctx.InvokeAsync(OcrMethods.GetPreview, OcrJson.Serialize(new PreviewRequest { PreviewId = id }), ct).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                await ApplyReplyJsonAsync(json, seq).ConfigureAwait(true);
            }
            while (_fetchAgain && !ct.IsCancellationRequested && !_disposed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // A missed reading: the next one brings new results.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>On open: the saved reader's last reading, shown until the preview's first one arrives.</summary>
    public async Task LoadLastReadingAsync(CancellationToken ct)
    {
        if (IsNew || string.IsNullOrEmpty(Id))
        {
            return;
        }

        try
        {
            var seq = ++_readSeq;
            var json = await _ctx.InvokeAsync(OcrMethods.GetReading, OcrJson.Serialize(new ReaderRequest { ReaderId = Id }), ct).ConfigureAwait(true);
            if (!_disposed && Results.Count == 0)
            {
                await ApplyReplyJsonAsync(json, seq).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // No last reading: the preview brings one.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Shows a fetched reading unless a later fetch (<paramref name="seq"/>, in the order the calls started) was shown already:
    /// the saved reader's last reading never replaces the preview's newer one across the decoding.
    /// </summary>
    private async Task ApplyReplyJsonAsync(string? json, long seq)
    {
        var reply = OcrJson.Deserialize<ReadingReply>(json);
        var details = reply.Details;
        var cutOuts = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        foreach (var detail in details)
        {
            if (detail.Cutout is { Length: > 0 } bytes && !cutOuts.ContainsKey(detail.Area) && await _decode(bytes).ConfigureAwait(true) is { } cut)
            {
                cutOuts[detail.Area] = cut;
            }
        }

        if (_disposed || seq < _shownSeq)
        {
            DisposeAll(cutOuts.Values);
            return;
        }

        _shownSeq = seq;
        ApplyReading(reply.Reading, details, cutOuts);
    }

    /// <summary>Shows a reading: its lines over the live video, the results with the server's cut-outs by area (the editor owns the bitmaps from now on).</summary>
    public void ApplyReading(Reading? reading, IReadOnlyList<AreaDetails>? details, IReadOnlyDictionary<string, Bitmap>? cutOuts = null)
    {
        var oldCutOuts = _cutOuts;
        if (reading is null)
        {
            DisposeAll(cutOuts?.Values);
            return;
        }

        _cutOuts = cutOuts is null ? [] : [.. cutOuts.Values];
        if (Areas.Count == 0)
        {
            // Text is read only inside areas: a reading from before the last area went shows nothing.
            reading = reading with { Areas = [], Note = null };
        }

        Lines = [.. reading.Areas.SelectMany(a => a.Lines)];
        ReadingNote = reading.Note;
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
            IImage? cut = cutOuts is not null && cutOuts.TryGetValue(area.Area, out var served) ? served : null;
            rows.Add(new ResultRow(area, detail, hasFormat, reading.Time, hasFormat && area.MatchesFormat && area.Text.Length > 0 ? cut : null));
        }

        Results.ReplaceAll(rows);
        OnPropertyChanged(nameof(HasResults));
        OverlayVersion++;
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopPreview();
        _disposed = true;
        _video?.Dispose();
        Results.Clear();
        DisposeAll(_cutOuts);
        _cutOuts = [];
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(StreamError));
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

        if (e.PropertyName is nameof(DeviceId) or nameof(SelectedSource))
        {
            ShowVideo();
        }

        if (e.PropertyName is nameof(SelectedSource) or nameof(SelectedResolution) or nameof(Fps) or nameof(Mode))
        {
            OnPropertyChanged(nameof(StreamSummary));
        }
    }

    partial void OnModeChanged(ReaderMode value)
    {
        OnPropertyChanged(nameof(ModeChipText));
        OnPropertyChanged(nameof(IsModeOk));
        OnPropertyChanged(nameof(IsModeError));
        if (!_loading && value == ReaderMode.OnAction && _onActionNoticeShown.Add(IsNew ? _draftKey : Id))
        {
            _ = _ctx.ShowMessageAsync(OcrTexts.OnActionNoticeTitle, OcrTexts.OnActionNotice);
        }
    }

    partial void OnFpsChanged(int value)
    {
        if (!_loading)
        {
            Validation.SetServerError(nameof(Mode), null); // another rate: the refusal about it no longer holds
        }

        OnPropertyChanged(nameof(ModeChipText));
    }

    partial void OnSelectedResolutionChanged(Resolution value) => OverlayVersion++;

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
        if (Areas.Count == 0)
        {
            // No area, no reading: the lines and results of the last one go, the video shows the start hint.
            Lines = [];
            ReadingNote = null;
            Results.Clear();
            OnPropertyChanged(nameof(HasResults));
        }

        OnPropertyChanged(nameof(HasAreas));
        OnPropertyChanged(nameof(ShowStartHint));
        OnPropertyChanged(nameof(ResultsEmptyText));
        OnPropertyChanged(nameof(CanAddArea));
        OnPropertyChanged(nameof(NewAreaTip));
        NewAreaCommand.NotifyCanExecuteChanged();
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
        SendDraft();
    }

    private string Signature() => OcrJson.Serialize(ToReader() with { Id = string.Empty });

    private string? NameError() =>
        ReaderRules.NameProblem(Name) ?? (_readerNameTaken(Id, Name.Trim()) ? ReaderRules.NameTaken(Name.Trim()) : null);

    private static string SourceName(int number) => $"View area {number}";
}
