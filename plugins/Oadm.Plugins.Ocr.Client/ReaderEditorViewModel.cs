using System.Collections.ObjectModel;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;

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
    private int _pauseVersion;

    /// <summary>From the press on Pause until Resume: the table keeps its rows, whatever a reply says.</summary>
    private bool _tableFrozen;
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
        MayPause = ctx.Has(HostPermissionKeys.DevicesLiveView);

        FpsChoices = Ordered(engine.FpsChoices);

        _loading = true;
        Id = reader.Id;
        Name = reader.Name;
        DeviceId = reader.DeviceId;
        CameraText = cameraText(reader.DeviceId);
        Mode = reader.Mode;
        MovingObjects = reader.MovingObjects;
        Fps = OfferedFps(reader.Fps, FpsChoices);
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
        // A stored frame rate the server no longer offers shows as the offered one and the reader is Not saved: Save stores it.
        _savedJson = isNew ? string.Empty : Signature(ToReader() with { Fps = reader.Fps });
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

    /// <summary>The stream button: "Configure · 1920x1080 @ 4 fps" (no frame rate for On action).</summary>
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
    public bool IsOnAction => Mode == ReaderMode.OnAction;

    /// <summary>Watching: the frame rate shows.</summary>
    public bool IsWatching => Mode == ReaderMode.Watching;

    /// <summary>Belts and passing vehicles: no vote, a value counts once it was read in 2 frames in a row (only while Watching).</summary>
    [ObservableProperty]
    public partial bool MovingObjects { get; set; }

    [ObservableProperty]
    public partial int Fps { get; set; }

    /// <summary>The frame rates the server offers (the select of Frames per second), following its list (<see cref="ApplyFpsChoices"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<int> FpsChoices { get; private set; }

    /// <summary>
    /// The server's frame rates changed (the reader list, the engine): the select offers them; a rate no longer offered becomes
    /// the highest offered below it and the reader is Not saved.
    /// </summary>
    public void ApplyFpsChoices(IReadOnlyList<int>? choices)
    {
        if (choices is not { Count: > 0 } || Ordered(choices).SequenceEqual(FpsChoices))
        {
            return;
        }

        FpsChoices = Ordered(choices);
        if (!FpsChoices.Contains(Fps))
        {
            Fps = OfferedFps(Fps, FpsChoices);
        }
    }

    private static int[] Ordered(IReadOnlyList<int> choices) => [.. choices.Distinct().Order()];

    /// <summary><paramref name="fps"/> when offered, else the highest offered below it, else the default (or the lowest offered).</summary>
    internal static int OfferedFps(int fps, IReadOnlyList<int> choices)
    {
        if (choices.Count == 0 || choices.Contains(fps))
        {
            return fps;
        }

        var below = choices.Where(c => c < fps).ToList();
        return below.Count > 0 ? below.Max() : choices.Contains(OcrLimits.DefaultFps) ? OcrLimits.DefaultFps : choices.Min();
    }

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

    /// <summary>The live video shows: not while paused, the held frame is in its place (a hidden video stops streaming).</summary>
    public bool ShowLiveVideo => HasLiveVideo && !IsPaused;

    /// <summary>The user may see the held picture (the host's Live view); without it Pause is not offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    public partial bool MayPause { get; private set; }

    /// <summary>The page shows a frame the server holds: edits re-read that frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLiveVideo), nameof(PauseTip), nameof(ShowPauseState), nameof(IsPauseWorking), nameof(ResultsEmptyText))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    public partial bool IsPaused { get; private set; }

    /// <summary>The frame the server holds while paused; the editor owns it.</summary>
    [ObservableProperty]
    public partial Bitmap? HeldPicture { get; private set; }

    /// <summary>A Pause or Resume call is on its way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPauseState), nameof(IsPauseWorking))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    public partial bool IsPausing { get; private set; }

    /// <summary>The held frame is being read with the newest draft.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPauseState), nameof(IsPauseWorking))]
    public partial bool IsRereading { get; private set; }

    /// <summary>This page's preview has a reading: there is a frame to pause on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    public partial bool HasPreviewReading { get; private set; }

    /// <summary>The ring next to the button.</summary>
    public bool IsPauseWorking => IsPausing || IsRereading;

    /// <summary>The backing of the chip and the ring on the video.</summary>
    public bool ShowPauseState => IsPaused || IsPauseWorking;

    /// <summary>The tooltip of the button on the video.</summary>
    public string PauseTip => IsPaused ? OcrTexts.Resume : OcrTexts.Pause;

    private bool CanTogglePause() => MayPause && !IsPausing && (IsPaused || HasPreviewReading);

    /// <summary>Pause or Resume, the one button on the video.</summary>
    [RelayCommand(CanExecute = nameof(CanTogglePause))]
    private Task TogglePauseAsync() => IsPaused ? ResumeAsync() : PauseAsync();

    /// <summary>
    /// Pause: the server holds the frame of the latest reading and answers it, the very picture that was read (the client's
    /// video is another picture, the lines would not fit). A refusal stays in the host's message; nothing changes.
    /// </summary>
    private async Task PauseAsync()
    {
        if (PreviewId is not { } id)
        {
            return;
        }

        IsPausing = true;
        _tableFrozen = true;
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.PausePreview, OcrJson.Serialize(new PreviewRequest { PreviewId = id }), CancellationToken.None)
                .ConfigureAwait(true);
            var reply = OcrJson.Deserialize<PauseReply>(json);
            if (_disposed || PreviewId != id)
            {
                return;
            }

            if (!reply.Paused)
            {
                HasPreviewReading = false;
                if (reply.Problem is { } problem)
                {
                    await _ctx.ShowMessageAsync(OcrTexts.Pause, problem).ConfigureAwait(true);
                }

                return;
            }

            var picture = reply.Picture is { Length: > 0 } bytes ? await _decode(bytes).ConfigureAwait(true) : null;
            if (_disposed || PreviewId != id)
            {
                picture?.Dispose();
                return;
            }

            if (picture is null)
            {
                await _ctx.InvokeAsync(OcrMethods.ResumePreview, OcrJson.Serialize(new PreviewRequest { PreviewId = id }), CancellationToken.None).ConfigureAwait(true);
                return; // a picture that does not decode: live again
            }

            _pauseVersion++;
            HeldPicture = picture;
            IsPaused = true;
            await ApplyReplyJsonAsync(json, ++_readSeq).ConfigureAwait(true); // newer than any fetch still on its way
        }
#pragma warning disable CA1031 // The server's reason in the host's message: the page stays live.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(OcrTexts.Pause, ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsPausing = false;
            _tableFrozen = IsPaused;
        }
    }

    /// <summary>Resume: the server drops the frame and reads live; the page waits for the next reading before Pause works again.</summary>
    private async Task ResumeAsync()
    {
        IsPausing = true;
        try
        {
            if (PreviewId is { } id)
            {
                var json = await _ctx.InvokeAsync(OcrMethods.ResumePreview, OcrJson.Serialize(new PreviewRequest { PreviewId = id }), CancellationToken.None)
                    .ConfigureAwait(true);
                if (!_disposed)
                {
                    PreviewProblem = OcrJson.Deserialize<PreviewReply>(json).Problem;
                }
            }

            if (!_disposed)
            {
                LeavePause();
                await RefreshPreviewAsync(_preview?.Token ?? CancellationToken.None).ConfigureAwait(true); // the last reads again, at once
            }
        }
#pragma warning disable CA1031 // The server's reason in the host's message: the page stays paused.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(OcrTexts.Resume, ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsPausing = false;
        }
    }

    /// <summary>
    /// The frame is gone (Resume, the server's answer, the page hides): the live video again, the table comes up to date with the
    /// next fetch. A fetch still on its way may hold the paused answer, without rows: it no longer counts.
    /// </summary>
    private void LeavePause()
    {
        if (IsPaused)
        {
            HeldAreas = [];
            ClearNote();
            _shownSeq = ++_readSeq;
        }

        _tableFrozen = false;
        _pauseVersion++;
        var held = HeldPicture;
        HasPreviewReading = false;
        IsRereading = false;
        IsPaused = false;
        HeldPicture = null;
        held?.Dispose();
    }

    /// <summary>The user's permissions changed: without Live view the frame goes, the server drops it with the next call.</summary>
    public void ApplyPermissions()
    {
        MayPause = _ctx.Has(HostPermissionKeys.DevicesLiveView);
        if (!MayPause && IsPaused)
        {
            _ = ResumeAsync();
        }
    }

    /// <summary>The picture's shape for the overlay: the reader's resolution.</summary>
    public Resolution PictureSize => SelectedResolution;

    /// <summary>The lines of the latest reading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownLines))]
    public partial IReadOnlyList<TextLine> Lines { get; private set; } = [];

    /// <summary>The lines drawn over the live video: none while an area is drawn, moved or resized.</summary>
    public IReadOnlyList<TextLine> ShownLines => IsDrawing || IsAdjustingArea ? [] : Lines;

    /// <summary>
    /// The note above Save ("More than 30 texts found. ...", "Lane: Text too small. ..."): live each alert stays
    /// <see cref="OcrLimits.NoteHold"/> after the last reading that had it, so it can be read on a belt; while paused the held
    /// frame's own note with a line per area with a format ("Lane: no match"), at once. Null without one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadingNote), nameof(NoteMaxLines))]
    public partial string? ReadingNote { get; private set; }

    public bool HasReadingNote => !string.IsNullOrEmpty(ReadingNote);

    /// <summary>Four for the first line of the note, which wraps in the settings column; one more for each line after it.</summary>
    public int NoteMaxLines => 4 + (ReadingNote?.Count(c => c == '\n') ?? 0);

    /// <summary>The last reads, newest on top; frozen while paused (only <see cref="ApplyLive"/> changes them).</summary>
    public RangeObservableCollection<ResultRow> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    /// <summary>The held frame's result per area, in the reader's order; empty while live.</summary>
    public IReadOnlyList<ResultRow> HeldAreas { get; private set; } = [];

    /// <summary>
    /// Below the results while there are none: why the preview reads nothing, else that it waits (nothing without areas: the
    /// video says it). While paused nothing comes in.
    /// </summary>
    public string ResultsEmptyText => PreviewProblem ?? (!HasAreas ? string.Empty : IsPaused ? OcrTexts.NoReads : OcrTexts.WaitingForPicture);

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
        MovingObjects = MovingObjects,
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

        // The newest read wins: an older answer that arrives later never replaces it.
        var version = ++_cameraVersion;
        var camera = DeviceId;
        IsLoadingCamera = true;
        CameraOptions? options = null;
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.GetCameraOptions, OcrJson.Serialize(new CameraOptionsRequest { DeviceId = camera }), ct)
                .ConfigureAwait(true);
            options = OcrJson.Deserialize<CameraOptions>(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (version == _cameraVersion)
            {
                IsLoadingCamera = false;
            }

            return;
        }
#pragma warning disable CA1031 // An older server or a camera that does not answer: the reader's own values and the usual sizes.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (version != _cameraVersion || _disposed || camera != DeviceId)
        {
            return;
        }

        IsLoadingCamera = false;
        ApplyCameraOptions(options);
    }

    private int _cameraVersion;

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

    /// <summary>The entries of an area's row menu (ui:RowActions).</summary>
    public IReadOnlyList<Oadm.Sdk.Client.Controls.IssuedCertificateAction> ActionsOf(AreaViewModel area) =>
    [
        new(OcrTexts.UseReadTextAsFormat, UseReadTextAsFormatCommand, area),
        new(OcrTexts.Rename, RenameAreaCommand, area),
        Oadm.Sdk.Client.Controls.IssuedCertificateAction.Separator,
        new("Delete", RemoveAreaCommand, area, IsDanger: true),
    ];

    /// <summary>
    /// The boxes an area shows on the video: every line with any text; with a format one box, the value at its place, and
    /// nothing else: no match gives none, a voted value that is not in this frame has no place and gives none. Other lines
    /// are never picked by touching that box: a turned label's boxes overlap.
    /// </summary>
    private IEnumerable<TextLine> LinesToShow(AreaResult area, AreaDetails? details, int index)
    {
        if (index >= Areas.Count || !Areas[index].IsFormat)
        {
            return area.Lines;
        }

        return area.MatchesFormat && details?.MatchBox is { } box ? [new TextLine(area.Text, box, area.Confidence)] : [];
    }

    /// <summary>The format the area's latest read text gives; null without a text or when it makes no valid format.</summary>
    private string? FormatFromReadText(AreaViewModel? area)
    {
        var index = area is null ? -1 : Areas.IndexOf(area);
        if (!MayChange || index < 0 || index >= _areaTexts.Count)
        {
            return null;
        }

        var format = Oadm.Plugins.Ocr.Matching.TextFormat.FromText(_areaTexts[index]);
        return format.Length > 0 && format.Length <= AreaViewModel.MaxFormatLength && ReaderRules.FormatProblem(format) is null ? format : null;
    }

    private bool CanUseReadTextAsFormat(AreaViewModel? area) => FormatFromReadText(area) is not null;

    /// <summary>The menu entry: the area's format follows the shape of the text read now; the user adjusts it (@@@@ to @@@U).</summary>
    [RelayCommand(CanExecute = nameof(CanUseReadTextAsFormat))]
    private void UseReadTextAsFormat(AreaViewModel? area)
    {
        if (area is not null && FormatFromReadText(area) is { } format)
        {
            area.FormatText = format;
        }
    }

    /// <summary>Rename (in an area's menu): the dialog with the area's name; Rename sets it.</summary>
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
        var index = Areas.IndexOf(area);
        if (index < _areaTexts.Count)
        {
            SetAreaTexts([.. _areaTexts.Where((_, i) => i != index)]); // the others keep their own text
        }

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
            OcrFields.Areas => nameof(Areas),
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

    /// <summary>A refusal of the server about the area list as a whole (too many areas), below the Areas panel; any change of the areas clears it.</summary>
    public string? AreasError => ErrorOf(nameof(Areas));

    public bool HasAreasError => AreasError is not null;

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
        LeavePause(); // the server's preview ends and its frame with it
        ClearNote();
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
                var pauseVersion = _pauseVersion;
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

                // An answer from before a Pause or Resume says nothing about it.
                if (IsPaused && pauseVersion == _pauseVersion)
                {
                    if (reply.Paused)
                    {
                        IsRereading = reply.Rereading;
                    }
                    else
                    {
                        LeavePause(); // the frame went on the server: another stream setting, the lease, a restart
                    }
                }
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
            IsRereading = IsPaused; // the ring from the first moment; the answer tells whether a reading follows
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
                var json = await _ctx.InvokeAsync(OcrMethods.GetPreview, OcrJson.Serialize(new PreviewRequest { PreviewId = id, KnownCutouts = KnownCutouts() }), ct).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                if (await ApplyReplyJsonAsync(json, seq).ConfigureAwait(true))
                {
                    HasPreviewReading = true;
                    IsRereading = false;
                }
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
    /// the saved reader's last reading never replaces the preview's newer one across the decoding. True when a reading shows.
    /// </summary>
    private async Task<bool> ApplyReplyJsonAsync(string? json, long seq)
    {
        var reply = OcrJson.Deserialize<ReadingReply>(json);
        if (IsPaused)
        {
            if (_disposed || seq < _shownSeq)
            {
                return false;
            }

            _shownSeq = seq;
            ApplyReading(reply.Reading, reply.Details);
            return reply.Reading is not null;
        }

        // Live: only the rows that bring a cut-out are decoded, each once; a row the page has keeps its picture.
        var decoded = new Dictionary<long, Bitmap?>();
        foreach (var read in reply.Recent)
        {
            if (read.Cutout is { Length: > 0 } bytes && !(_rows.TryGetValue(read.Id, out var row) && row.Picture is not null))
            {
                decoded[read.Id] = await _decode(bytes).ConfigureAwait(true);
            }
        }

        if (_disposed || seq < _shownSeq)
        {
            DisposeAll(decoded.Values);
            return false;
        }

        _shownSeq = seq;
        ApplyLive(reply, decoded);
        return reply.Reading is not null;
    }

    /// <summary>
    /// The live table: the reading's lines and note, the last reads newest on top. Nothing happens to the grid while the rows
    /// stay the same (20 rows at 15 frames per second stay cheap); a row that leaves gives its bitmap back. With a Pause on its
    /// way the rows stay: the server may already answer paused, without rows.
    /// </summary>
    private void ApplyLive(ReadingReply reply, Dictionary<long, Bitmap?> decoded)
    {
        if (reply.Reading is null && reply.Recent.Count == 0)
        {
            DisposeAll(decoded.Values);
            return; // the preview ended or has read nothing yet: the next reading tells
        }

        if (reply.Reading is { } reading)
        {
            HoldNote(ShowReading(reading, reply.Details), reply.Details);
        }

        if (_tableFrozen)
        {
            DisposeAll(decoded.Values);
            return;
        }

        var rows = new List<ResultRow>(reply.Recent.Count);
        var kept = new HashSet<long>();
        foreach (var read in Areas.Count == 0 ? [] : reply.Recent)
        {
            var bitmap = decoded.Remove(read.Id, out var made) ? made : null;
            if (!_rows.TryGetValue(read.Id, out var row))
            {
                row = new ResultRow(read, bitmap);
                _rows[read.Id] = row;
            }
            else if (row.Picture is null && read.Cutout is { Length: > 0 })
            {
                row.Picture = read.Cutout;
                row.CutOut = bitmap;
            }
            else
            {
                bitmap?.Dispose();
            }

            rows.Add(row);
            kept.Add(read.Id);
        }

        DisposeAll(decoded.Values);
        var leaving = _rows.Where(r => !kept.Contains(r.Key)).ToList();
        if (!rows.SequenceEqual(Results))
        {
            Results.ReplaceAll(rows);
            OnPropertyChanged(nameof(HasResults));
        }

        foreach (var (id, row) in leaving)
        {
            _rows.Remove(id);
            DisposeCutOut(row);
        }
    }

    /// <summary>
    /// Shows the reading of a held frame: its lines over the picture and its result per area, matched by the area's place in the
    /// reader (names can repeat while they are edited), in <see cref="HeldAreas"/> and the note; the table stays as it is.
    /// </summary>
    public void ApplyReading(Reading? reading, IReadOnlyList<AreaDetails>? details)
    {
        if (reading is null)
        {
            return;
        }

        reading = ShowReading(reading, details);
        var rows = new List<ResultRow>(reading.Areas.Count);
        for (var i = 0; i < reading.Areas.Count; i++)
        {
            var detail = details is not null && i < details.Count ? details[i] : null;
            rows.Add(new ResultRow(reading.Areas[i], detail, i < Areas.Count && Areas[i].IsFormat, reading.Time));
        }

        HeldAreas = rows;
        var values = rows.Where(r => r.HasFormat).Select(r => OcrTexts.HeldValue(r.Area, r.IsMatch ? r.Text : null));
        ClearNote();
        ReadingNote = string.Join('\n', new[] { NoteOf(reading, details) }.OfType<string>().Concat(values)) is { Length: > 0 } note ? note : null;
    }

    /// <summary>What a reading shows besides the table and the note: its lines and each area's text. Answers it as shown.</summary>
    private Reading ShowReading(Reading reading, IReadOnlyList<AreaDetails>? details)
    {
        if (Areas.Count == 0)
        {
            // Text is read only inside areas: a reading from before the last area went shows nothing.
            reading = reading with { Areas = [], Note = null };
        }

        Lines = [.. reading.Areas.SelectMany((a, i) => LinesToShow(a, details is not null && i < details.Count ? details[i] : null, i))];
        SetAreaTexts([.. reading.Areas.Select((a, i) => a.Text.Length > 0 || (i < Areas.Count && Areas[i].IsFormat) ? a.Text : a.TextRead)]);
        OverlayVersion++;
        return reading;
    }

    /// <summary>The reading's note and the areas whose text is too small (the table has no row per area).</summary>
    private static string? NoteOf(Reading reading, IReadOnlyList<AreaDetails>? details) => JoinNote(reading.Note, SmallAreas(reading, details));

    private static List<string> SmallAreas(Reading reading, IReadOnlyList<AreaDetails>? details) =>
        [.. reading.Areas.Where((_, i) => details is not null && i < details.Count && details[i].TooSmall).Select(a => a.Area)];

    private static string? JoinNote(string? note, List<string> small) =>
        small.Count == 0 ? note : string.Join(' ', new[] { note, OcrTexts.TooSmallIn(small) }.OfType<string>());

    /// <summary>
    /// A live reading's alerts: each stays <see cref="OcrLimits.NoteHold"/> after this reading, a new one joins them, so the
    /// text changes only when an alert comes or goes and never flickers between two readings.
    /// </summary>
    private void HoldNote(Reading reading, IReadOnlyList<AreaDetails>? details)
    {
        var until = PreviewTime.GetUtcNow() + OcrLimits.NoteHold;
        if (reading.Note is { } note)
        {
            _heldNote = (note, until);
        }

        foreach (var area in SmallAreas(reading, details))
        {
            var at = _heldSmall.FindIndex(s => s.Area == area);
            if (at >= 0)
            {
                _heldSmall[at] = (area, until);
            }
            else
            {
                _heldSmall.Add((area, until));
            }
        }

        ShowHeldNote();
        if (!_noteClock && (_heldNote is not null || _heldSmall.Count > 0))
        {
            _noteClock = true;
            _ = ExpireNoteAsync(_noteVersion);
        }
    }

    /// <summary>Takes the alerts whose time ended away; the rest stay as they are.</summary>
    private void ShowHeldNote()
    {
        var now = PreviewTime.GetUtcNow();
        if (_heldNote is { } held && held.Until <= now)
        {
            _heldNote = null;
        }

        _heldSmall.RemoveAll(s => s.Until <= now);
        ReadingNote = JoinNote(_heldNote?.Text, [.. _heldSmall.Select(s => s.Area)]);
    }

    /// <summary>The note also ends when no reading comes any more.</summary>
    private async Task ExpireNoteAsync(int version)
    {
        while (!_disposed && version == _noteVersion && (_heldNote is not null || _heldSmall.Count > 0))
        {
            var next = _heldSmall.Select(s => s.Until).Concat(_heldNote is { } held ? [held.Until] : []).Min();
            await Task.Delay(next > PreviewTime.GetUtcNow() ? next - PreviewTime.GetUtcNow() : TimeSpan.Zero, PreviewTime).ConfigureAwait(true);
            if (!_disposed && version == _noteVersion)
            {
                ShowHeldNote();
            }
        }

        if (version == _noteVersion)
        {
            _noteClock = false;
        }
    }

    /// <summary>The note goes at once: the reader or the frame it belongs to went.</summary>
    private void ClearNote()
    {
        _noteVersion++;
        _noteClock = false;
        _heldNote = null;
        _heldSmall.Clear();
        ReadingNote = null;
    }

    /// <summary>What each area reads now; an open menu's entry follows it.</summary>
    private void SetAreaTexts(IReadOnlyList<string> texts)
    {
        if (!texts.SequenceEqual(_areaTexts))
        {
            _areaTexts = texts;
            UseReadTextAsFormatCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The ids of the rows whose cut-out the page has, so the server sends each once; null without one.</summary>
    private List<long>? KnownCutouts()
    {
        var known = _rows.Where(r => r.Value.Picture is not null).Select(r => r.Key).ToList();
        return known.Count > 0 ? known : null;
    }

    /// <summary>A click on a row's thumbnail: the dialog with its own copy of the picture, so the row may leave while it is open.</summary>
    [RelayCommand]
    private async Task OpenCutOutAsync(ResultRow? row)
    {
        if (row?.Picture is not { } bytes || Dialogs is not { } dialogs || await _decode(bytes).ConfigureAwait(true) is not { } picture)
        {
            return;
        }

        try
        {
            await dialogs.ShowCutOutAsync(new CutOutViewModel(row, picture)).ConfigureAwait(true);
        }
        finally
        {
            picture.Dispose();
        }
    }

    /// <summary>The rows of the last reads by id: a row stays with its decoded picture while it stays in the list.</summary>
    private readonly Dictionary<long, ResultRow> _rows = [];

    private IReadOnlyList<string> _areaTexts = [];

    private (string Text, DateTimeOffset Until)? _heldNote;
    private readonly List<(string Area, DateTimeOffset Until)> _heldSmall = [];
    private bool _noteClock;
    private int _noteVersion;

    /// <summary>The table empties; every picture goes.</summary>
    private void ClearResults()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        foreach (var row in _rows.Values)
        {
            DisposeCutOut(row);
        }

        _rows.Clear();
    }

    private static void DisposeCutOut(ResultRow row)
    {
        var bitmap = row.CutOut as Bitmap;
        row.CutOut = null;
        bitmap?.Dispose();
    }

    private static void DisposeAll(IEnumerable<Bitmap?>? bitmaps)
    {
        foreach (var bitmap in bitmaps ?? [])
        {
            bitmap?.Dispose();
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
        ClearResults();
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(StreamError));
        OnPropertyChanged(nameof(ModeError));
        OnPropertyChanged(nameof(AreasError));
        OnPropertyChanged(nameof(HasAreasError));
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

        if (e.PropertyName is nameof(Name) or nameof(DeviceId) or nameof(SelectedSource) or nameof(SelectedResolution) or nameof(Mode) or nameof(Fps) or nameof(MovingObjects))
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
        ClearAreasRefusal();
        if (Areas.Count == 0)
        {
            // No area, no reading: the lines and results of the last one go, the video shows the start hint.
            Lines = [];
            ClearNote();
            SetAreaTexts([]);
            HeldAreas = [];
            ClearResults();
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
        ClearAreasRefusal();
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

    /// <summary>Any change of the areas: the server's refusal of the list no longer holds.</summary>
    private void ClearAreasRefusal()
    {
        if (AreasError is not null)
        {
            Validation.SetServerError(nameof(Areas), null);
        }
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

    private string Signature() => Signature(ToReader());

    private static string Signature(Reader reader) => OcrJson.Serialize(reader with { Id = string.Empty });

    private string? NameError() =>
        ReaderRules.NameProblem(Name) ?? (_readerNameTaken(Id, Name.Trim()) ? ReaderRules.NameTaken(Name.Trim()) : null);

    private static string SourceName(int number) => $"View area {number}";
}
