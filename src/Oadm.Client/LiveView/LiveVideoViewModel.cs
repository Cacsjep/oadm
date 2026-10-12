using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using Avalonia;
using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Vapix;

namespace Oadm.Client.LiveView;

public enum LiveViewState
{
    Closed,
    Connecting,
    Live,
    Reconnecting,
    Error,

    /// <summary>Hidden for a while: the stream stopped, the last picture stays.</summary>
    Paused,
}

/// <summary>
/// One live video: the picture of one camera channel with its overlays (progress ring, error, the hover buttons for
/// fullscreen and quality, the channel switch, the GPU warning, the details, the zoom and focus or PTZ controls). Used by
/// the side panel, the fullscreen window and later the camera grid; <see cref="LiveVideoOptions"/> turns parts off.
/// <para>
/// Streams through the server (credentials never reach the client) and decodes on a background thread. The resolution
/// follows the picture's size (Adaptive) or is the camera's largest (Full). A new resolution, a new quality or a restart
/// after hiding opens a second stream and keeps showing the old picture until the new stream delivers (the ring shows
/// after <see cref="LiveVideoOptions.SlowStartDelay"/>); when the new stream fails the old one stays. Hidden for
/// <see cref="LiveVideoOptions.HiddenStopDelay"/> it stops and keeps the last picture. A dropped stream reconnects with
/// backoff (1, 2, 4, 8, 10 s); permanent errors (no decoder, unknown device, credentials) stop.
/// </para>
/// </summary>
public sealed partial class LiveVideoViewModel : ObservableObject, IDisposable
{
    public const string DecoderUnavailableText = "Video decoder not available on this platform";

    /// <summary>Request box while the size of the picture is not known yet.</summary>
    private static readonly VideoResolution DefaultBox = new(LiveViewDefaults.Width, LiveViewDefaults.Height);

    private static readonly TimeSpan[] DefaultBackoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)];

    private readonly IOadmApi _api;
    private readonly IVideoDecoderFactory _decoders;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly GpuDecodingService? _gpu;
    private readonly LiveVideoViewModel? _parent;

    private string? _deviceId;
    private string _title = "";
    private int _requestedCamera;
    private Session? _active;
    private Session? _pending;
    private int _sessionIds;
    private LiveImage? _current;
    private CancellationTokenSource? _sourcesLoad;
    private CancellationTokenSource? _resizeTimer;
    private CancellationTokenSource? _hideTimer;
    private CancellationTokenSource? _slowTimer;
    private bool _slow;
    private bool _sourcesKnown;
    private bool _paused;
    private bool _viewVisible = true;
    private bool _covered;
    private (int Width, int Height) _viewport;
    private bool _controlsWanted; // open or closed by the user; a camera with controls shows them so

    // Details: frames and bytes shown since the window started.
    private long _windowStart;
    private int _framesInWindow;
    private long _bytesAtWindowStart;

    public LiveVideoViewModel(IOadmApi api, IVideoDecoderFactory decoders, IUiDispatcher ui, ILogger logger, LiveVideoSettings settings,
        LiveVideoOptions? options = null, TimeProvider? time = null, GpuDecodingService? gpu = null)
        : this(api, decoders, ui, logger, settings, options, time, gpu, parent: null)
    {
    }

    private LiveVideoViewModel(IOadmApi api, IVideoDecoderFactory decoders, IUiDispatcher ui, ILogger logger, LiveVideoSettings settings,
        LiveVideoOptions? options, TimeProvider? time, GpuDecodingService? gpu, LiveVideoViewModel? parent)
    {
        _api = api;
        _decoders = decoders;
        _ui = ui;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _gpu = gpu;
        _parent = parent;
        Settings = settings;
        Options = options ?? new LiveVideoOptions();
        _controlsWanted = Options.ControlsOpen;
        Quality = Options.Quality;
        Optics = new OpticsViewModel(api, _time) { AllowZoom = Options.OpticsZoom, AllowFocus = Options.OpticsFocus, CanControl = settings.CanControlOptics };
        Ptz = new PtzViewModel(api, _time)
        {
            AllowMove = Options.PtzMove,
            AllowZoom = Options.PtzZoom,
            AllowPresets = Options.PtzPresets,
            AllowHome = Options.PtzHome,
            AllowCenter = Options.PtzCenter,
            CanControl = settings.CanControlOptics,
        };
        Optics.PropertyChanged += OnControlsChanged;
        Ptz.PropertyChanged += OnControlsChanged;
        Settings.PropertyChanged += OnSettingsChanged;
        if (_gpu is not null)
        {
            _gpu.Changed += OnGpuChanged;
        }
    }

    /// <summary>What this place offers (parts on or off, frame rates, delays).</summary>
    public LiveVideoOptions Options { get; }

    /// <summary>Client-wide settings: details overlay, adaptive frame rate, Optics Control.</summary>
    public LiveVideoSettings Settings { get; }

    /// <summary>Zoom and focus of the shown camera (cameras with a remote lens).</summary>
    public OpticsViewModel Optics { get; }

    /// <summary>PTZ of the shown channel (PTZ cameras).</summary>
    public PtzViewModel Ptz { get; }

    /// <summary>Reconnect delays; tests shorten them.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = DefaultBackoff;

    /// <summary>This one shows in the fullscreen window (the button and Esc close it).</summary>
    public bool IsFullscreen => _parent is not null;

    /// <summary>The device shown, null while stopped.</summary>
    public string? DeviceId => _deviceId;

    /// <summary>The camera's name for the fullscreen window ("AXIS P3265-V").</summary>
    public string Title => _title;

    /// <summary>The fullscreen button or a double-click: the view opens or closes the fullscreen window.</summary>
    public event EventHandler? FullscreenRequested;

    /// <summary>The user chose another channel (the side panel remembers it per device).</summary>
    public event EventHandler<int>? CameraChosen;

    /// <summary>Background task of the newest stream (tests await it).</summary>
    internal Task Running => (_pending ?? _active)?.Running ?? Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PictureSize), nameof(ShowDetails), nameof(HasError))]
    public partial IImage? Image { get; private set; }

    /// <summary>Size of the decoded picture (the overlays place themselves on it).</summary>
    public Size PictureSize => Image?.Size ?? default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsReconnecting))]
    public partial LiveViewState State { get; private set; }

    /// <summary>The reason while reconnecting or failed (the ring's tooltip, or the error over the picture).</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>The picture area shows the error instead of a picture.</summary>
    public bool HasError => State == LiveViewState.Error;

    /// <summary>The reason shows below the ring.</summary>
    public bool IsReconnecting => State == LiveViewState.Reconnecting;

    /// <summary>The progress ring: no picture yet, reconnecting, or a new stream that is slow to deliver.</summary>
    [ObservableProperty]
    public partial bool ShowRing { get; private set; }

    /// <summary>Codec, resolution, frame rate, bit rate and decoding (the details card top right).</summary>
    [ObservableProperty]
    public partial VideoDetails Details { get; private set; } = VideoDetails.None;

    /// <summary>The details card shows: the setting is on and a picture is there.</summary>
    public bool ShowDetails => Settings.ShowDetails && Image is not null && Details != VideoDetails.None;

    /// <summary>
    /// Why the graphics card does not decode the stream although "Decode video on the graphics card" is on, and what to do
    /// (the small orange icon over the picture); empty otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDecodingWarning))]
    public partial string DecodingWarning { get; private set; } = "";

    public bool HasDecodingWarning => DecodingWarning.Length > 0;

    /// <summary>Channels of the device (view areas, sensors, encoder inputs).</summary>
    public ObservableCollection<LiveViewSourceOption> Sources { get; } = [];

    /// <summary>The device has several channels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSourceSwitch), nameof(SourceName))]
    public partial bool HasSourceChoice { get; private set; }

    /// <summary>The channel buttons over the picture: several channels and the place allows choosing.</summary>
    public bool ShowSourceSwitch => HasSourceChoice && Options.ShowSourceSwitch;

    /// <summary>Name of the shown channel while the device has several ("View Area 2"), else null.</summary>
    public string? SourceName => HasSourceChoice ? Sources.FirstOrDefault(s => s.IsSelected)?.Name : null;

    /// <summary>Camera being streamed (1-based), 0 until the sources or the first frame tell which one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceName))]
    public partial int Camera { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityText), nameof(QualityTip))]
    public partial LiveVideoQuality Quality { get; private set; }

    public string QualityText => Quality == LiveVideoQuality.Full ? "Full" : "Adaptive";

    public string QualityTip => Quality == LiveVideoQuality.Full
        ? "Largest resolution. Click for adaptive quality."
        : "Resolution follows the size. Click for full quality.";

    /// <summary>The zoom and focus or PTZ controls are open (from the start with <see cref="LiveVideoOptions.ControlsOpen"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpticsBar), nameof(ShowPtz))]
    public partial bool IsControlsOpen { get; private set; }

    /// <summary>The controls button: the camera has zoom and focus or PTZ, the role may use them, the place shows it.</summary>
    public bool HasControls => Options.ShowControlsButton && (Optics.IsVisible || Ptz.IsVisible);

    public bool ShowOpticsBar => IsControlsOpen && HasControls && Optics.IsVisible;

    /// <summary>The PTZ circle and bar (only while the controls are open).</summary>
    public bool ShowPtz => IsControlsOpen && HasControls && Ptz.IsVisible;

    public bool ShowFocusArea => ShowOpticsBar && Optics.IsEditingArea;

    public bool AllowFullscreen => Options.AllowFullscreen;

    public bool AllowQualitySwitch => Options.AllowQualitySwitch;

    /// <summary>Starts the video of a device; camera 0 = the first channel. A running video stops first.</summary>
    public void Start(string deviceId, int camera = 0, string title = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        Stop();
        _deviceId = deviceId;
        _title = title;
        _requestedCamera = camera;
        Camera = camera;
        if (_decoders.SupportedCodecs.Count == 0)
        {
            LogDecoderUnavailable(_logger, _decoders.UnavailableReason ?? "unknown");
            SetState(LiveViewState.Error, DecoderUnavailableText);
            return;
        }

        SetState(LiveViewState.Connecting, "");
        LoadSources(deviceId);
    }

    /// <summary>Stops the stream and clears the picture.</summary>
    public void Stop()
    {
        CancelSessions();
        Cancel(ref _sourcesLoad);
        Cancel(ref _resizeTimer);
        Cancel(ref _hideTimer);
        _deviceId = null;
        _sourcesKnown = false;
        _paused = false;
        Sources.Clear();
        HasSourceChoice = false;
        Camera = 0;
        IsControlsOpen = false;
        Optics.Clear();
        Ptz.Clear();
        DecodingWarning = "";
        SwapImage(null);
        SetState(LiveViewState.Closed, "");
    }

    /// <summary>The picture area's size in device pixels; the resolution follows once it rests.</summary>
    public void SetViewport(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width, height) == _viewport)
        {
            return;
        }

        _viewport = (width, height);
        if (_deviceId is null || (_active is null && _pending is null))
        {
            return; // the first stream takes the size when it starts
        }

        ReevaluateLater();
    }

    /// <summary>A change that may need another stream, once it rests (sizes while dragging, a frame rate being typed).</summary>
    private void ReevaluateLater()
    {
        Cancel(ref _resizeTimer);
        var cts = new CancellationTokenSource();
        _resizeTimer = cts;
        _ = AfterAsync(Options.ResizeDebounce, cts, () => Reevaluate(force: false));
    }

    /// <summary>Whether the picture can be seen (on screen, page shown, window not minimized).</summary>
    public void SetVisible(bool visible)
    {
        if (visible == _viewVisible)
        {
            return;
        }

        _viewVisible = visible;
        UpdateVisibility();
    }

    /// <summary>Another channel of the device.</summary>
    [RelayCommand]
    private void SelectSource(LiveViewSourceOption? option)
    {
        if (option is null || _deviceId is not { } deviceId)
        {
            return;
        }

        CameraChosen?.Invoke(this, option.Camera);
        if (option.Camera == Camera && (_active is not null || _pending is not null))
        {
            return;
        }

        _requestedCamera = option.Camera;
        Camera = option.Camera;
        MarkSelected();
        Ptz.EndDrag();
        _ = Ptz.LoadAsync(deviceId, option.Camera);
        _ = Optics.LoadAsync(deviceId, option.Sensor);
        Restart(clearPicture: true);
    }

    [RelayCommand]
    private void ToggleQuality()
    {
        Quality = Quality == LiveVideoQuality.Full ? LiveVideoQuality.Adaptive : LiveVideoQuality.Full;
        Reevaluate(force: false);
    }

    [RelayCommand]
    private void ToggleControls()
    {
        _controlsWanted = !IsControlsOpen;
        IsControlsOpen = _controlsWanted && HasControls;
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (Options.AllowFullscreen && (_deviceId is not null || IsFullscreen))
        {
            FullscreenRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The video for the fullscreen window: its own stream of the same channel at the size of the screen. This one stops
    /// streaming while the window covers it; disposing the window's video brings it back.
    /// </summary>
    public LiveVideoViewModel CreateFullscreen()
    {
        var video = new LiveVideoViewModel(_api, _decoders, _ui, _logger, Settings, Options with { Quality = Quality, ControlsOpen = _controlsWanted }, _time, _gpu, this)
        {
            Backoff = Backoff,
        };
        video.CameraChosen += (_, camera) =>
        {
            if (Sources.FirstOrDefault(s => s.Camera == camera) is { } option)
            {
                SelectSourceCommand.Execute(option);
            }
        };
        if (_deviceId is { } deviceId)
        {
            video.Start(deviceId, Camera != 0 ? Camera : _requestedCamera, _title);
        }

        _covered = true;
        UpdateVisibility();
        return video;
    }

    public void Dispose()
    {
        Stop();
        Optics.PropertyChanged -= OnControlsChanged;
        Ptz.PropertyChanged -= OnControlsChanged;
        Settings.PropertyChanged -= OnSettingsChanged;
        if (_gpu is not null)
        {
            _gpu.Changed -= OnGpuChanged;
        }

        Optics.Dispose();
        Ptz.Dispose();
        if (_parent is { } parent)
        {
            parent._covered = false;
            parent.UpdateVisibility();
        }
    }

    /// <summary>16:9, the shape of nearly every monitor: used whenever the channel offers it.</summary>
    public const double WideAspect = 16.0 / 9;

    /// <summary>The aspect the video is shown in: 16:9 when the channel lists it, else the sensor's (the largest resolution's).</summary>
    public static double PreferredAspect(IReadOnlyList<VideoResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        return resolutions.Any(r => SameAspect(r.Aspect, WideAspect))
            ? WideAspect
            : resolutions.Count == 0 ? WideAspect : resolutions.MaxBy(r => (long)r.Width * r.Height).Aspect;
    }

    /// <summary>The largest resolution of the preferred aspect (Full quality).</summary>
    public static VideoResolution LargestResolution(IReadOnlyList<VideoResolution> resolutions)
    {
        var aspect = PreferredAspect(resolutions);
        return resolutions.Where(r => SameAspect(r.Aspect, aspect)).MaxBy(r => (long)r.Width * r.Height);
    }

    /// <summary>
    /// The resolution for a picture area: of the preferred aspect (16:9 when listed), the smallest that fills the area
    /// without enlarging, else the largest. Without a list the area itself (the server picks).
    /// </summary>
    public static VideoResolution ChooseResolution(IReadOnlyList<VideoResolution> resolutions, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        if (resolutions.Count == 0)
        {
            return new VideoResolution(width, height);
        }

        var aspect = PreferredAspect(resolutions);
        var largest = LargestResolution(resolutions);
        if (width <= 0 || height <= 0)
        {
            return largest;
        }

        // Shown with Stretch.Uniform: the picture takes the full width or the full height of the area.
        var needed = Math.Min(width, height * aspect);
        return resolutions
            .Where(r => SameAspect(r.Aspect, aspect) && r.Width >= needed * 0.95)
            .OrderBy(r => (long)r.Width * r.Height)
            .FirstOrDefault(largest);
    }

    private static bool SameAspect(double a, double b) => Math.Abs(a - b) < 0.02;

    private StreamTarget TargetFor(int camera)
    {
        var source = Sources.FirstOrDefault(s => s.Camera == camera) ?? Sources.FirstOrDefault();
        var list = source?.Resolutions ?? [];
        if (Quality == LiveVideoQuality.Full)
        {
            var full = list.Count > 0 ? LargestResolution(list) : DefaultBox;
            return new StreamTarget(camera, full.Width, full.Height, Math.Max(1, Options.MaxFps));
        }

        var size = list.Count == 0 ? DefaultBox
            : _viewport == default ? ChooseResolution(list, DefaultBox.Width, DefaultBox.Height)
            : ChooseResolution(list, _viewport.Width, _viewport.Height);
        return new StreamTarget(camera, size.Width, size.Height, Math.Clamp(Settings.AdaptiveFps, 1, Math.Max(1, Options.MaxFps)));
    }

    private void LoadSources(string deviceId)
    {
        var cts = new CancellationTokenSource();
        _sourcesLoad = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<LiveViewSource> sources = [];
            try
            {
                sources = await _api.ListLiveViewSourcesAsync(deviceId, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or HttpRequestException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                LogStreamProblem(_logger, "video sources not available: " + ex.Message);
            }

            _ui.Post(() =>
            {
                if (token.IsCancellationRequested || _deviceId != deviceId)
                {
                    return;
                }

                ApplySources(deviceId, sources);
            });
        });
    }

    private void ApplySources(string deviceId, IReadOnlyList<LiveViewSource> sources)
    {
        Sources.Clear();
        foreach (var source in sources)
        {
            Sources.Add(new LiveViewSourceOption(source.Camera, source.Name, source.Sensor,
                source.Resolutions.Select(r => new VideoResolution(r.Width, r.Height)).ToList()));
        }

        _sourcesKnown = true;
        HasSourceChoice = Sources.Count > 1;
        if (Camera == 0 || Sources.All(s => s.Camera != Camera))
        {
            Camera = _requestedCamera != 0 ? _requestedCamera : Sources.FirstOrDefault()?.Camera ?? 0;
        }

        MarkSelected();
        var selected = Sources.FirstOrDefault(s => s.IsSelected);
        _ = Optics.LoadAsync(deviceId, selected?.Sensor ?? 0);
        _ = Ptz.LoadAsync(deviceId, Camera);
        if (!_paused)
        {
            Restart(clearPicture: false);
        }
    }

    /// <summary>Highlights the streamed channel.</summary>
    private void MarkSelected()
    {
        foreach (var option in Sources)
        {
            option.IsSelected = option.Camera == Camera;
        }

        OnPropertyChanged(nameof(SourceName));
    }

    /// <summary>A stream that starts over: the channel changed (the picture clears) or the first one starts.</summary>
    private void Restart(bool clearPicture)
    {
        CancelSessions();
        if (clearPicture)
        {
            SwapImage(null);
        }

        if (_deviceId is null || !_sourcesKnown || _paused)
        {
            return;
        }

        StartSession(TargetFor(Camera));
    }

    /// <summary>The size, the quality, the frame rate or the decoder changed: a new stream only when it would differ.</summary>
    private void Reevaluate(bool force)
    {
        if (_deviceId is null || !_sourcesKnown || _paused || State == LiveViewState.Error)
        {
            return;
        }

        var target = TargetFor(Camera);
        if (!force)
        {
            if (_pending is { } pending && pending.Target == target)
            {
                return;
            }

            if (_active is { } active && active.Target == target)
            {
                CancelPending();
                return;
            }
        }

        StartSession(target);
    }

    /// <summary>Opens a new stream next to the shown one; it takes over once it delivers a picture.</summary>
    private void StartSession(StreamTarget target)
    {
        CancelPending();
        var request = new LiveViewRequest
        {
            DeviceId = _deviceId!,
            MaxWidth = target.Width,
            MaxHeight = target.Height,
            Fps = target.Fps,
            Camera = target.Camera,
        };
        request.AcceptedCodecs.AddRange(_decoders.SupportedCodecs);
        var session = new Session(++_sessionIds, request, target);
        _pending = session;
        if (_active is null && Image is null)
        {
            SetState(LiveViewState.Connecting, "");
        }

        StartSlowTimer();
        session.Running = Task.Run(() => RunAsync(session));
        UpdateRing();
        if (!IsSeen)
        {
            UpdateVisibility();
        }
    }

    private bool IsSeen => _viewVisible && !_covered;

    private async Task RunAsync(Session session)
    {
        var ct = session.Cts.Token;
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            IVideoDecoder? decoder = null;
            var decoderCodec = VideoCodec.Unspecified;
            try
            {
                await foreach (var frame in _api.WatchLiveViewAsync(session.Request, ct).ConfigureAwait(false))
                {
                    Interlocked.Add(ref session.Bytes, frame.Data.Length);
                    if (decoder is null || frame.Codec != decoderCodec)
                    {
                        decoder?.Dispose();
                        decoder = _decoders.Create(frame.Codec);
                        decoderCodec = frame.Codec;
                    }

                    var image = decoder.Decode(frame);
                    if (image is not null)
                    {
                        attempt = 0;
                        var picture = new Picture(image, frame.Codec, frame.Camera, decoder.UsesGpu, decoder.GpuProblem ?? "");
                        _ui.Post(() => OnPicture(session, picture));
                    }
                }

                throw new RpcException(new Status(StatusCode.Unavailable, "The stream ended."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var permanent = ex is RpcException { StatusCode: StatusCode.NotFound or StatusCode.PermissionDenied or StatusCode.FailedPrecondition or StatusCode.InvalidArgument };
                var reason = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
                var delay = Backoff[Math.Min(attempt, Backoff.Count - 1)];
                attempt++;
                LogStreamProblem(_logger, reason);
                var retry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ui.Post(() => retry.TrySetResult(OnFailed(session, reason, permanent, delay)));
                try
                {
                    if (!await retry.Task.WaitAsync(ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                decoder?.Dispose();
            }
        }
    }

    /// <summary>A decoded picture: a new stream takes over with its first one.</summary>
    private void OnPicture(Session session, Picture picture)
    {
        if (session == _pending)
        {
            if (_active is { } old)
            {
                old.Cancel();
            }

            _active = session;
            _pending = null;
            Cancel(ref _slowTimer);
            _slow = false;
            _windowStart = 0;
        }
        else if (session != _active)
        {
            picture.Image.Dispose(); // replaced or stopped meanwhile
            return;
        }

        if (picture.Camera > 0 && picture.Camera != Camera)
        {
            Camera = picture.Camera;
            MarkSelected();
        }

        DecodingWarning = picture.GpuProblem;
        SwapImage(picture.Image);
        UpdateDetails(session, picture);
        SetState(LiveViewState.Live, "");
    }

    /// <summary>A stream failed. Returns whether it tries again.</summary>
    private bool OnFailed(Session session, string reason, bool permanent, TimeSpan delay)
    {
        if (session != _active && session != _pending)
        {
            return false;
        }

        if (session == _pending && _active is not null)
        {
            // A new size or quality the camera refuses: the shown stream stays.
            _pending = null;
            session.Cancel();
            Cancel(ref _slowTimer);
            _slow = false;
            UpdateRing();
            return false;
        }

        if (permanent)
        {
            CancelSessions();
            SwapImage(null);
            DecodingWarning = "";
            SetState(LiveViewState.Error, reason);
            return false;
        }

        SetState(LiveViewState.Reconnecting, string.Create(CultureInfo.InvariantCulture, $"{reason} Retrying in {delay.TotalSeconds:0} s."));
        return true;
    }

    private void UpdateDetails(Session session, Picture picture)
    {
        var now = _time.GetTimestamp();
        var codec = picture.Codec switch
        {
            VideoCodec.H265 => "H.265",
            VideoCodec.H264 => "H.264",
            _ => picture.Codec.ToString(),
        };
        var decoding = picture.UsesGpu ? "GPU" : "Processor";
        var resolution = string.Create(CultureInfo.InvariantCulture, $"{picture.Image.Width}x{picture.Image.Height}");
        var bytes = Interlocked.Read(ref session.Bytes);
        if (_windowStart == 0)
        {
            _windowStart = now;
            _framesInWindow = 0;
            _bytesAtWindowStart = bytes;
            Details = new VideoDetails(codec, resolution, VideoDetails.Measuring, VideoDetails.Measuring, decoding);
            OnPropertyChanged(nameof(ShowDetails));
            return;
        }

        _framesInWindow++;
        var elapsed = _time.GetElapsedTime(_windowStart, now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            var fps = _framesInWindow / elapsed.TotalSeconds;
            var kbit = (bytes - _bytesAtWindowStart) * 8 / 1000.0 / elapsed.TotalSeconds;
            Details = new VideoDetails(codec, resolution,
                string.Create(CultureInfo.InvariantCulture, $"{fps:0.0} fps"),
                string.Create(CultureInfo.InvariantCulture, $"{kbit:N0} kbit/s"), decoding);
            _framesInWindow = 0;
            _bytesAtWindowStart = bytes;
            _windowStart = now;
        }
        else if (Details.Codec != codec || Details.Resolution != resolution || Details.Decoding != decoding)
        {
            Details = Details with { Codec = codec, Resolution = resolution, Decoding = decoding };
        }

        OnPropertyChanged(nameof(ShowDetails));
    }

    private void UpdateVisibility()
    {
        if (IsSeen)
        {
            Cancel(ref _hideTimer);
            if (_paused)
            {
                _paused = false;
                if (_deviceId is not null && _sourcesKnown && State != LiveViewState.Error)
                {
                    // The last picture stays until the new stream delivers; the ring shows when that takes long.
                    SetState(LiveViewState.Connecting, "");
                    StartSession(TargetFor(Camera));
                }
            }

            return;
        }

        if (_hideTimer is not null || _paused)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _hideTimer = cts;
        _ = AfterAsync(Options.HiddenStopDelay, cts, () =>
        {
            _hideTimer = null;
            Pause();
        });
    }

    private void Pause()
    {
        if (_deviceId is null)
        {
            return;
        }

        CancelSessions();
        Ptz.EndDrag();
        _paused = true;
        if (State != LiveViewState.Error)
        {
            SetState(LiveViewState.Paused, "");
        }
    }

    private void StartSlowTimer()
    {
        Cancel(ref _slowTimer);
        _slow = false;
        var cts = new CancellationTokenSource();
        _slowTimer = cts;
        _ = AfterAsync(Options.SlowStartDelay, cts, () =>
        {
            _slow = true;
            UpdateRing();
        });
    }

    private void UpdateRing()
    {
        ShowRing = State switch
        {
            LiveViewState.Error or LiveViewState.Closed or LiveViewState.Paused => false,
            LiveViewState.Reconnecting => true,
            _ when Image is null => State == LiveViewState.Connecting,
            _ => _pending is not null && _slow,
        };
    }

    private void SetState(LiveViewState state, string text)
    {
        State = state;
        StatusText = text;
        UpdateRing();
    }

    private void SwapImage(LiveImage? image)
    {
        var old = _current;
        _current = image;
        Image = image?.Image;
        old?.Dispose();
        if (image is null)
        {
            _windowStart = 0;
            Details = VideoDetails.None;
        }
    }

    private void CancelPending()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            pending.Cancel();
        }

        Cancel(ref _slowTimer);
        _slow = false;
    }

    private void CancelSessions()
    {
        CancelPending();
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
        }

        Cancel(ref _resizeTimer);
        UpdateRing();
    }

    private void OnControlsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OpticsViewModel.IsVisible) or nameof(PtzViewModel.IsVisible))
        {
            IsControlsOpen = HasControls && _controlsWanted;

            OnPropertyChanged(nameof(HasControls));
            OnPropertyChanged(nameof(ShowOpticsBar));
            OnPropertyChanged(nameof(ShowPtz));
            OnPropertyChanged(nameof(ShowFocusArea));
        }
        else if (e.PropertyName == nameof(OpticsViewModel.IsEditingArea))
        {
            OnPropertyChanged(nameof(ShowFocusArea));
        }
    }

    partial void OnIsControlsOpenChanged(bool value)
    {
        if (!value)
        {
            Ptz.EndDrag();
        }

        OnPropertyChanged(nameof(ShowFocusArea));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LiveVideoSettings.ShowDetails):
                OnPropertyChanged(nameof(ShowDetails));
                break;
            case nameof(LiveVideoSettings.AdaptiveFps):
                ReevaluateLater();
                break;
            case nameof(LiveVideoSettings.CanControlOptics):
                Optics.CanControl = Settings.CanControlOptics;
                Ptz.CanControl = Settings.CanControlOptics;
                break;
        }
    }

    /// <summary>The test result or the setting changed: a new stream with the right decoder (the picture stays until it delivers).</summary>
    private void OnGpuChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_gpu?.Status.State is not GpuDecodingState.Checking && (_active is not null || _pending is not null))
        {
            Reevaluate(force: true);
        }
    });

    private async Task AfterAsync(TimeSpan delay, CancellationTokenSource cts, Action action)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _ui.Post(() =>
        {
            if (!cts.IsCancellationRequested)
            {
                action();
            }
        });
    }

    private static void Cancel(ref CancellationTokenSource? cts)
    {
        if (cts is { } old)
        {
            cts = null;
            old.Cancel();
            old.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live view: video decoder not available ({Reason})")]
    private static partial void LogDecoderUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live view stream: {Reason}")]
    private static partial void LogStreamProblem(ILogger logger, string reason);

    /// <summary>What a stream asks the server for.</summary>
    internal readonly record struct StreamTarget(int Camera, int Width, int Height, int Fps);

    private sealed record Picture(LiveImage Image, VideoCodec Codec, int Camera, bool UsesGpu, string GpuProblem);

    /// <summary>One stream from the server: shown (active) or waiting for its first picture (pending).</summary>
    private sealed class Session(int id, LiveViewRequest request, StreamTarget target)
    {
        public long Bytes;

        public int Id { get; } = id;

        public LiveViewRequest Request { get; } = request;

        public StreamTarget Target { get; } = target;

        public CancellationTokenSource Cts { get; } = new();

        public Task Running { get; set; } = Task.CompletedTask;

        /// <summary>Ends the stream. The token source has no timer, so it is left to the collector (never disposed under a running stream).</summary>
        public void Cancel() => Cts.Cancel();
    }
}

/// <summary>What the details card over the picture shows, one row each.</summary>
public sealed record VideoDetails(string Codec, string Resolution, string FrameRate, string BitRate, string Decoding)
{
    /// <summary>Frame rate and bit rate during the first second of a stream.</summary>
    public const string Measuring = "-";

    public static readonly VideoDetails None = new("", "", "", "", "");
}
