using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Client;

/// <summary>The dialogs of the page, shown by the view (tests answer them in place).</summary>
public interface IVisionDialogs
{
    /// <summary>Stream settings of the Camera node: true when the user chose OK.</summary>
    Task<bool> EditStreamAsync(StreamSettingsViewModel form);

    /// <summary>Rename or the class names of a model: true when the user chose its main button.</summary>
    Task<bool> EditModelAsync(ModelEditViewModel form);

    /// <summary>The model file and an optional labels file; null when cancelled.</summary>
    Task<IReadOnlyList<IModelFile>?> PickModelFilesAsync();
}

/// <summary>
/// The shown pipeline's draft (docs/spec/vision-center.md "Page"): the canvas, the pipeline's own settings (Name, Mode, the
/// camera and its stream), the selected node's form and live output, the live video with the overlay, Pause, the save status
/// and Save. A new pipeline starts from its camera, which stays. While the page shows the pipeline the server runs the draft as
/// a preview: <see cref="StartPreview"/> sends it on every change and every <see cref="VisionLimits.PreviewKeepAlive"/>
/// with an id the page makes per start; <see cref="StopPreview"/> ends it. A preview publishes no events.
/// </summary>
public sealed partial class PipelineEditorViewModel : ValidatingViewModel, INodeFormHost, IDisposable
{
    /// <summary>The live video of the page: the picture only (as Text recognition).</summary>
    internal static readonly LiveVideoPlace VideoPlace = new()
    {
        ShowSourceSwitch = false,
        AllowFullscreen = false,
        AllowQualitySwitch = false,
        ShowControlsButton = false,
    };

    private static readonly Resolution[] FallbackResolutions = [new(1920, 1080), new(1280, 720), new(640, 360)];

    private readonly ICorePluginClientContext _ctx;
    private readonly Func<string, string, bool> _nameTaken;
    private readonly Func<IReadOnlyList<VisionModel>> _models;
    private readonly Func<byte[], Task<Bitmap?>> _decode;
    private readonly Func<Pipeline, GraphCheck> _check;
    private readonly ILiveVideo? _video;
    private string _savedJson;
    private bool _loading;
    private bool _disposed;
    private bool _sending;
    private bool _sendAgain;
    private CancellationTokenSource? _preview;
    private PreviewEvent? _last;
    private PortKind? _formInput;

    public PipelineEditorViewModel(
        ICorePluginClientContext ctx,
        Pipeline pipeline,
        bool isNew,
        EngineInfo engine,
        Func<Guid, string> cameraText,
        Func<string, string, bool> pipelineNameTaken,
        Func<IReadOnlyList<VisionModel>> models,
        Func<byte[], Task<Bitmap?>> decode,
        Func<Pipeline, GraphCheck>? check = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(cameraText);
        _nameTaken = pipelineNameTaken ?? throw new ArgumentNullException(nameof(pipelineNameTaken));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _check = check ?? (p => VisionGraph.Check(p, id => _models().FirstOrDefault(m => m.Id == id)));
        MayChange = ctx.Has(VisionViewModel.ChangePermissionKey);
        MayPause = ctx.Has(HostPermissionKeys.DevicesLiveView);
        FpsChoices = engine.FpsChoices is { Count: > 0 } f ? f : VisionLimits.FpsChoices;

        _loading = true;
        Id = pipeline.Id;
        IsNew = isNew;
        Name = pipeline.Name;
        DeviceId = pipeline.DeviceId;
        CameraText = cameraText(pipeline.DeviceId);
        Mode = pipeline.Mode;
        Fps = FpsChoices.Contains(pipeline.Fps) ? pipeline.Fps : VisionLimits.DefaultFps;
        VideoSource = pipeline.VideoSource;
        Resolution = pipeline.Resolution;
        VideoSources = [pipeline.VideoSource];
        Resolutions = [pipeline.Resolution];
        Canvas = new CanvasViewModel(pipeline.Nodes, pipeline.Links) { CanEdit = MayChange };
        _loading = false;

        Canvas.Checker = (nodes, links) => _check(ToPipeline() with { Nodes = nodes, Links = links });
        Canvas.Recheck();
        Canvas.Changed += OnCanvasChanged;
        Canvas.DrawRequested += (_, node) => StartDrawing(node);
        Canvas.PropertyChanged += OnCanvasPropertyChanged;
        Validation.Rule(nameof(Name), NameProblem);
        Validation.Validate();
        _savedJson = isNew ? string.Empty : Signature();
        UpdateCameraLine();
        UpdateSelection();
        UpdateSaveState();

        _video = ctx.LiveVideo?.Create(VideoPlace);
        _video?.Start(DeviceId, VideoSource);
    }

    /// <summary>The pipeline was saved: its new state (with its id).</summary>
    public event EventHandler<Pipeline>? Saved;

    public IVisionDialogs? Dialogs { get; set; }

    /// <summary>The keep-alive's clock (tests pass a fake one).</summary>
    public TimeProvider PreviewTime { get; set; } = TimeProvider.System;

    public CanvasViewModel Canvas { get; }

    public bool MayChange { get; }

    public string Id { get; private set; }

    [ObservableProperty]
    public partial bool IsNew { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    public static int MaxNameLength => VisionLimits.MaxNameLength;

    public Guid DeviceId { get; }

    /// <summary>"AXIS Q1798-LE · 10.0.0.48".</summary>
    public string CameraText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeChoice), nameof(IsWatching), nameof(StreamSummary))]
    public partial PipelineMode Mode { get; set; }

    public static IReadOnlyList<string> ModeChoices { get; } = [VisionTexts.Watching, VisionTexts.OnAction];

    public string ModeChoice
    {
        get => Mode == PipelineMode.OnAction ? VisionTexts.OnAction : VisionTexts.Watching;
        set => Mode = value == VisionTexts.OnAction ? PipelineMode.OnAction : PipelineMode.Watching;
    }

    public bool IsWatching => Mode == PipelineMode.Watching;

    public IReadOnlyList<int> FpsChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamSummary))]
    public partial int Fps { get; set; }

    [ObservableProperty]
    public partial int VideoSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamSummary), nameof(PictureSize))]
    public partial Resolution Resolution { get; set; }

    public IReadOnlyList<int> VideoSources { get; private set; }

    public IReadOnlyList<Resolution> Resolutions { get; private set; }

    /// <summary>The camera's sources and sizes are being read.</summary>
    [ObservableProperty]
    public partial bool IsLoadingCamera { get; private set; }

    /// <summary>"Configure · 1280x720 @ 8 fps" (no frame rate On action).</summary>
    public string StreamSummary => VisionTexts.StreamSummary(Resolution, IsWatching ? Fps : null);

    /// <summary>The size the boxes of the latest frame belong to (the picture's shape on the page).</summary>
    public Resolution PictureSize => _last?.Resolution ?? Resolution;

    // ---- Selection: the form and the live output ----

    /// <summary>The selected node's form; null: nothing or a link selected (the pipeline's own settings show).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForm), nameof(ShowPipelineForm))]
    public partial NodeFormViewModel? Form { get; private set; }

    public bool HasForm => Form is not null;

    public bool ShowPipelineForm => Form is null;

    [ObservableProperty]
    public partial bool IsLinkSelected { get; private set; }

    /// <summary>The callout below the pipeline's settings: Delete for a selected link, else how to see a node.</summary>
    public string SelectionNote => IsLinkSelected ? VisionTexts.LinkSelected : VisionTexts.SelectNode;

    public NodeOutputViewModel Output { get; } = new();

    // ---- The video and its overlay ----

    public Control? VideoView => _video?.View;

    public bool HasLiveVideo => _video is not null;

    public bool ShowLiveVideo => HasLiveVideo && !IsPaused;

    [ObservableProperty]
    public partial IReadOnlyList<OverlayItem> OverlayItems { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ShapeItem> Shapes { get; private set; } = [];

    /// <summary>A shape is drawn on the video now: the node kind (Area, Line); null while not drawing.</summary>
    [ObservableProperty]
    public partial string? DrawKind { get; private set; }

    public NodeViewModel? DrawingNode { get; private set; }

    // ---- Pause ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    public partial bool MayPause { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLiveVideo), nameof(PauseTip), nameof(ShowPauseState))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPauseState))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    public partial bool IsPausing { get; private set; }

    [ObservableProperty]
    public partial Bitmap? HeldPicture { get; private set; }

    public bool ShowPauseState => IsPaused || IsPausing;

    public string PauseTip => IsPaused ? VisionTexts.Resume : VisionTexts.Pause;

    public static string PausedTip => VisionTexts.PausedTip;

    // ---- Save ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatus), nameof(HasSaveStatus), nameof(IsSaveStatusOk))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    public string SaveStatus => IsDirty ? VisionTexts.NotSaved : VisionTexts.Saved;

    public bool HasSaveStatus => !IsNew || IsDirty;

    public bool IsSaveStatusOk => !IsDirty;

    /// <summary>Why Save is off: the errors on the nodes, of the graph or of the name; null when it may save.</summary>
    [ObservableProperty]
    public partial string? SaveBlockedReason { get; private set; }

    // ---- The preview ----

    /// <summary>The preview's id while it runs (32 hex digits, new per start).</summary>
    public string? PreviewId { get; private set; }

    public bool IsPreviewing => _preview is not null;

    /// <summary>Why the preview runs nothing now (the server's text); null while it runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewProblem))]
    public partial string? PreviewProblem { get; private set; }

    public bool HasPreviewProblem => PreviewProblem is not null;

    /// <summary>The pipeline as it would be saved.</summary>
    public Pipeline ToPipeline() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        DeviceId = DeviceId,
        VideoSource = VideoSource,
        Resolution = Resolution,
        Mode = Mode,
        Fps = Fps,
        Nodes = Canvas.ToNodes(),
        Links = Canvas.ToLinks(),
    };

    // ---- INodeFormHost ----

    public IReadOnlyList<ModelOption> ModelsOf(ModelKind kind) =>
        [.. _models().Where(m => m.Kind == kind).Select(m => new ModelOption(m.Id, m.Missing ? $"{m.Name} ({VisionTexts.Missing})" : m.Name))];

    public IReadOnlyList<string> ClassesFor(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        // A model node: its own model; any other: the detection model before it.
        var at = VisionGraph.FieldOf(node.Info, NodeKinds.ModelKey) is not null ? node : Canvas.InputOf(node)?.From;
        for (var i = 0; i <= Canvas.Nodes.Count && at is not null; i++)
        {
            if ((ReferenceEquals(at, node) || at.Kind == NodeKinds.Detect)
                && VisionGraph.FieldOf(at.Info, NodeKinds.ModelKey) is { } field
                && NodeValues.Text(NodeValues.Get(at.Settings, field)) is { } id
                && _models().FirstOrDefault(m => m.Id == id) is { } model)
            {
                return [.. model.ShownClasses];
            }

            at = Canvas.InputOf(at)?.From;
        }

        return [];
    }

    public void StartDrawing(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!MayChange || NodeValues.ShapeField(node.Info, node.InputKind) is null)
        {
            return;
        }

        Canvas.SelectedNode = node;
        DrawingNode = node;
        DrawKind = node.Kind == NodeKinds.Line ? NodeKinds.Line : NodeKinds.Area;
    }

    public void StopDrawing()
    {
        DrawingNode = null;
        DrawKind = null;
    }

    /// <summary>A shape drawn on the video for the node being drawn.</summary>
    public void ShapeDrawn(IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var node = DrawingNode;
        StopDrawing();
        if (node is not null)
        {
            SetShape(node, points);
        }
    }

    /// <summary>A shape moved or edited on the video.</summary>
    public void SetShape(NodeViewModel node, IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(points);
        if (!MayChange || NodeValues.ShapeField(node.Info, node.InputKind) is not { } field)
        {
            return;
        }

        Canvas.SetSetting(node, field.Key, NodeValues.ToJson(points));
        Canvas.SelectedNode = node;
        Form?.Refresh();
    }

    /// <summary>An arrow key on the video: the selected shape moves.</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (Shapes.FirstOrDefault(s => s.IsSelected) is { } shape)
        {
            SetShape(shape.Node, ShapeGeometry.MovePoints(shape.Points, dx, dy));
        }
    }

    // ---- The Camera node's stream ----

    /// <summary>The camera's video sources and picture sizes (the pipeline's own stay chosen when the camera has them).</summary>
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
            var json = await _ctx.InvokeAsync(VisionMethods.GetCameraOptions, VisionJson.Serialize(new CameraOptionsRequest { DeviceId = DeviceId }), ct)
                .ConfigureAwait(true);
            options = VisionJson.Deserialize<CameraOptions>(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            IsLoadingCamera = false;
            return;
        }
#pragma warning disable CA1031 // A camera that does not answer: the pipeline's own values and the usual sizes.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (_disposed)
        {
            return;
        }

        IsLoadingCamera = false;
        VideoSources = options?.VideoSources is { Count: > 0 } s ? s : [VideoSource];
        Resolutions = options?.Resolutions is { Count: > 0 } r ? r
            : [.. FallbackResolutions.Append(Resolution).Distinct().OrderByDescending(x => x.Width * x.Height)];
        if (!Resolutions.Contains(Resolution))
        {
            var limit = Resolution.Default.Width * Resolution.Default.Height;
            Resolution = Resolutions.Where(x => x.Width * x.Height <= limit).OrderByDescending(x => x.Width * x.Height).FirstOrDefault()
                ?? Resolutions.OrderBy(x => x.Width * x.Height).First();
        }
    }

    [RelayCommand]
    private async Task EditStreamAsync()
    {
        if (Dialogs is not { } dialogs)
        {
            return;
        }

        var sources = VideoSources.Select(n => new StreamSource(n, VisionTexts.VideoSource(n))).ToList();
        var form = new StreamSettingsViewModel(sources, sources.FirstOrDefault(s => s.Number == VideoSource), Resolutions, Resolution,
            FpsChoices, Fps, IsWatching, IsLoadingCamera);
        if (!await dialogs.EditStreamAsync(form).ConfigureAwait(true) || !MayChange)
        {
            return;
        }

        VideoSource = form.SelectedSource?.Number ?? VideoSource;
        Resolution = form.SelectedResolution ?? Resolution;
        if (IsWatching)
        {
            Fps = form.Fps;
        }
    }

    // ---- Pause ----

    private bool CanTogglePause() => MayPause && !IsPausing && PreviewId is not null;

    [RelayCommand(CanExecute = nameof(CanTogglePause))]
    private Task TogglePauseAsync() => IsPaused ? ResumeAsync() : PauseAsync();

    /// <summary>Pause: the server holds the frame and answers it; the draft then runs again on it after every change.</summary>
    private async Task PauseAsync()
    {
        if (PreviewId is not { } id)
        {
            return;
        }

        IsPausing = true;
        try
        {
            var json = await _ctx.InvokeAsync(VisionMethods.PausePreview, VisionJson.Serialize(new PreviewRequest { PreviewId = id }), CancellationToken.None)
                .ConfigureAwait(true);
            var reply = VisionJson.Deserialize<PauseReply>(json);
            if (_disposed || PreviewId != id)
            {
                return;
            }

            if (!reply.Paused)
            {
                if (reply.Problem is { } problem)
                {
                    await _ctx.ShowMessageAsync(VisionTexts.Pause, problem).ConfigureAwait(true);
                }

                return;
            }

            var picture = reply.Picture is { Length: > 0 } bytes ? await _decode(bytes).ConfigureAwait(true) : null;
            if (_disposed || PreviewId != id)
            {
                picture?.Dispose();
                return;
            }

            HeldPicture = picture;
            IsPaused = true;
            if (reply.State is { } state)
            {
                ApplyPreview(state with { PreviewId = id });
            }
        }
#pragma warning disable CA1031 // The server's reason in the host's message: the page stays live.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(VisionTexts.Pause, ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsPausing = false;
        }
    }

    private async Task ResumeAsync()
    {
        IsPausing = true;
        try
        {
            if (PreviewId is { } id)
            {
                await _ctx.InvokeAsync(VisionMethods.ResumePreview, VisionJson.Serialize(new PreviewRequest { PreviewId = id }), CancellationToken.None)
                    .ConfigureAwait(true);
            }

            LeavePause();
        }
#pragma warning disable CA1031 // The server's reason in the host's message: the page stays paused.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(VisionTexts.Resume, ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsPausing = false;
        }
    }

    private void LeavePause()
    {
        var held = HeldPicture;
        IsPaused = false;
        HeldPicture = null;
        held?.Dispose();
    }

    public void ApplyPermissions()
    {
        MayPause = _ctx.Has(HostPermissionKeys.DevicesLiveView);
        if (!MayPause && IsPaused)
        {
            LeavePause();
        }
    }

    // ---- The preview ----

    /// <summary>The page shows the pipeline: a new preview of the draft, kept alive until <see cref="StopPreview"/>.</summary>
    public void StartPreview()
    {
        if (_preview is not null || _disposed)
        {
            return;
        }

        _preview = new CancellationTokenSource();
        PreviewId = Guid.NewGuid().ToString("N");
        _ = KeepPreviewAsync(_preview.Token);
        TogglePauseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The page is hidden or shows another pipeline: the server ends the preview.</summary>
    public void StopPreview()
    {
        if (_preview is not { } preview)
        {
            return;
        }

        _preview = null;
        preview.Cancel();
        preview.Dispose();
        LeavePause();
        if (PreviewId is { } id)
        {
            PreviewId = null;
            _ = EndPreviewAsync(id);
        }

        TogglePauseCommand.NotifyCanExecuteChanged();
    }

    private async Task EndPreviewAsync(string id)
    {
        try
        {
            await _ctx.InvokeAsync(VisionMethods.StopPreview, VisionJson.Serialize(new PreviewRequest { PreviewId = id }), CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The lease ends by itself on the server.
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
                await Task.Delay(VisionLimits.PreviewKeepAlive, PreviewTime, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The draft to the server now (one call at a time: a change meanwhile is sent once the call ends, newest wins).</summary>
    public async Task SendPreviewAsync(CancellationToken ct)
    {
        if (_sending)
        {
            _sendAgain = true;
            return;
        }

        _sending = true;
        try
        {
            do
            {
                _sendAgain = false;
                if (PreviewId is not { } id || ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var request = new PreviewRequest { PreviewId = id, Pipeline = ToPipeline() };
                    var json = await _ctx.InvokeAsync(VisionMethods.StartPreview, VisionJson.Serialize(request), ct).ConfigureAwait(true);
                    if (PreviewId == id)
                    {
                        PreviewProblem = VisionJson.Deserialize<PreviewReply>(json).Problem;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The server's reason above the video; the next change or keep-alive tries again.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    PreviewProblem = ServerErrors.Message(ex);
                }
            }
            while (_sendAgain && !_disposed);
        }
        finally
        {
            _sending = false;
        }
    }

    /// <summary>The preview's live event: the live lines, the link counts, the overlay and the selected node's output.</summary>
    public void ApplyPreview(PreviewEvent preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.PreviewId != PreviewId || _disposed)
        {
            return;
        }

        var size = PictureSize;
        _last = preview;
        if (size != PictureSize)
        {
            OnPropertyChanged(nameof(PictureSize));
        }

        Canvas.ApplyOutputs(preview.Nodes);
        UpdateCameraLine();
        RebuildOverlay();
        if (Canvas.SelectedNode is { } node)
        {
            Output.Apply(node, _last);
        }
    }

    // ---- Save ----

    private bool CanSave() => MayChange && IsDirty && !IsSaving && SaveBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        Form?.ShowAllErrors();
        UpdateSaveState();
        if (!CanSave())
        {
            return;
        }

        IsSaving = true;
        try
        {
            var pipeline = ToPipeline();
            var json = await _ctx.InvokeAsync(VisionMethods.SavePipeline, VisionJson.Serialize(new SavePipelineRequest { Pipeline = pipeline }), CancellationToken.None)
                .ConfigureAwait(true);
            var reply = VisionJson.Deserialize<SavePipelineReply>(json);
            if (reply.Errors.Count > 0 || reply.GraphErrors.Count > 0 || reply.Pipeline is null)
            {
                foreach (var (key, message) in reply.Errors)
                {
                    if (key.StartsWith("node:", StringComparison.Ordinal))
                    {
                        Canvas.Find(key[5..])?.Error = message;
                    }
                    else if (key == "name")
                    {
                        Validation.SetServerError(nameof(Name), message);
                    }
                    else
                    {
                        await _ctx.ShowMessageAsync(VisionTexts.Save, message).ConfigureAwait(true);
                    }
                }

                if (reply.GraphErrors.Count > 0)
                {
                    await _ctx.ShowMessageAsync(VisionTexts.Save, string.Join(" ", reply.GraphErrors)).ConfigureAwait(true);
                }

                return;
            }

            Id = reply.Pipeline.Id;
            IsNew = false;
            _savedJson = Signature();
            Canvas.ForgetHistory();
            UpdateSaveState();
            Saved?.Invoke(this, reply.Pipeline);
        }
#pragma warning disable CA1031 // The server's reason in the host's message; the draft stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync(VisionTexts.Save, ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    protected override void OnValidationChanged() => UpdateSaveState();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (_loading)
        {
            return;
        }

        if (e.PropertyName == nameof(Mode))
        {
            Canvas.Recheck();
        }
        else if (e.PropertyName == nameof(VideoSource))
        {
            _video?.Start(DeviceId, VideoSource);
        }

        if (e.PropertyName is nameof(Mode) or nameof(VideoSource) or nameof(Name) or nameof(Fps) or nameof(Resolution))
        {
            UpdateCameraLine();
            DraftChanged();
        }
    }

    private string? NameProblem()
    {
        var name = Name.Trim();
        return name.Length == 0 ? GraphTexts.EnterName
            : name.Length > VisionLimits.MaxNameLength ? GraphTexts.NameTooLong
            : _nameTaken(Id, name) ? VisionTexts.PipelineNameTaken
            : null;
    }

    private void OnCanvasChanged(object? sender, EventArgs e)
    {
        if (Form is { } form && (!ReferenceEquals(form.Node, Canvas.SelectedNode) || _formInput != form.Node.InputKind))
        {
            UpdateSelection();
        }

        RebuildOverlay();
        DraftChanged();
    }

    private void OnCanvasPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasViewModel.SelectedNode) or nameof(CanvasViewModel.SelectedLink))
        {
            UpdateSelection();
            RebuildOverlay();
        }
        else if (e.PropertyName == nameof(CanvasViewModel.ErrorCount))
        {
            UpdateSaveState();
        }
    }

    private void UpdateSelection()
    {
        var node = Canvas.SelectedNode;
        if (DrawingNode is not null && !ReferenceEquals(DrawingNode, node))
        {
            StopDrawing();
        }

        IsLinkSelected = Canvas.SelectedLink is not null;
        OnPropertyChanged(nameof(SelectionNote));
        if (Form?.Node != node)
        {
            Form = node is null ? null : new NodeFormViewModel(Canvas, node, this);
            _formInput = node?.InputKind;
        }
        else if (node is not null && _formInput != node.InputKind)
        {
            Form = new NodeFormViewModel(Canvas, node, this);
            _formInput = node.InputKind;
        }

        if (node is not null)
        {
            Output.Apply(node, _last);
        }
    }

    private void RebuildOverlay()
    {
        var outputs = _last?.Nodes.GroupBy(o => o.NodeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal)
            ?? new Dictionary<string, NodeOutput>(StringComparer.Ordinal);
        var (items, shapes) = VideoOverlay.Build(Canvas.Nodes, Canvas.SelectedNode, outputs);
        OverlayItems = items;
        Shapes = shapes;
    }

    /// <summary>The Camera node's live line: the camera and its stream, until the preview says more.</summary>
    private void UpdateCameraLine()
    {
        var model = CameraText.Split(" · ")[0];
        foreach (var camera in Canvas.Nodes.Where(n => n.IsCamera))
        {
            var live = _last?.Nodes.LastOrDefault(o => o.NodeId == camera.Id)?.Live;
            camera.Live = string.IsNullOrEmpty(live) ? VisionTexts.CameraSummary(model, Resolution, IsWatching ? Fps : null) : live;
        }
    }

    private void DraftChanged()
    {
        UpdateSaveState();
        if (_preview is { } preview)
        {
            _ = SendPreviewAsync(preview.Token);
        }
    }

    private void UpdateSaveState()
    {
        if (Canvas is null)
        {
            return;
        }

        IsDirty = IsNew || Signature() != _savedJson;
        SaveBlockedReason = Canvas.ErrorCount > 0 ? VisionTexts.FixErrors(Canvas.ErrorCount)
            : Validation.FirstError ?? Form?.FirstError;
        OnPropertyChanged(nameof(HasSaveStatus));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private string Signature() => VisionJson.Serialize(ToPipeline() with { Id = string.Empty });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopPreview();
        _disposed = true;
        Canvas.Changed -= OnCanvasChanged;
        Canvas.PropertyChanged -= OnCanvasPropertyChanged;
        _video?.Dispose();
        HeldPicture?.Dispose();
    }
}
