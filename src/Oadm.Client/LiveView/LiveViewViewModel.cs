using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

public enum LiveViewState
{
    Closed,
    Connecting,
    Live,
    Reconnecting,
    Error,
}

/// <summary>
/// The live view side panel: which device is shown, the current picture and the connection
/// state. Streams through the server (credentials never reach the client), decodes on a
/// background thread and swaps pictures on the UI thread. A dropped stream reconnects with
/// backoff (1, 2, 4, 8, 10 s); permanent errors (no decoder, unknown device, credentials) stop.
/// </summary>
public sealed partial class LiveViewViewModel : ObservableObject, IDisposable
{
    public const string DecoderUnavailableText = "Video decoder not available on this platform";

    private static readonly TimeSpan[] DefaultBackoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)];

    private readonly IOadmApi _api;
    private readonly IVideoDecoderFactory _decoders;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<LiveViewViewModel> _logger;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _session;
    private int _sessionId;
    private LiveImage? _current;
    private int _framesInWindow;
    private long _windowStart;
    private string _streamInfo = "";
    private readonly Dictionary<string, int> _chosenCamera = [];
    private CancellationTokenSource? _sourcesLoad;

    public LiveViewViewModel(IOadmApi api, IVideoDecoderFactory decoders, IUiDispatcher ui, ILogger<LiveViewViewModel> logger, TimeProvider? time = null)
    {
        _api = api;
        _decoders = decoders;
        _ui = ui;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Requested stream size and rate (the server picks the closest camera resolution).</summary>
    public int MaxWidth { get; set; } = LiveViewDefaults.Width;

    public int MaxHeight { get; set; } = LiveViewDefaults.Height;

    public int Fps { get; set; } = LiveViewDefaults.Fps;

    /// <summary>Reconnect delays; tests shorten them.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = DefaultBackoff;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial DeviceRowViewModel? Device { get; private set; }

    [ObservableProperty]
    public partial IImage? Image { get; private set; }

    [ObservableProperty]
    public partial LiveViewState State { get; private set; }

    /// <summary>Short state for the chip: Connecting, Live, Reconnecting, Error.</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = "";

    /// <summary>Codec, resolution and fps while live; the reason otherwise.</summary>
    [ObservableProperty]
    public partial string DetailText { get; private set; } = "";

    public PillKind StateKind => State switch
    {
        LiveViewState.Live => PillKind.Ok,
        LiveViewState.Connecting => PillKind.Accent,
        LiveViewState.Reconnecting => PillKind.Warning,
        LiveViewState.Error => PillKind.Error,
        _ => PillKind.Neutral,
    };

    public string Title => Device is null ? "" : string.IsNullOrEmpty(Device.Model) ? Device.Serial : Device.Model;

    public string Subtitle
    {
        get
        {
            if (Device is null)
            {
                return "";
            }

            var source = HasSourceChoice ? Sources.FirstOrDefault(s => s.IsSelected)?.Name : null;
            return source is null ? $"{Device.DisplayAddress}  ·  {Device.Serial}" : $"{Device.DisplayAddress}  ·  {Device.Serial}  ·  {source}";
        }
    }

    /// <summary>Video sources of the device (view areas, sensors, channels); the switch shows when there are several.</summary>
    public ObservableCollection<LiveViewSourceOption> Sources { get; } = [];

    [ObservableProperty]
    public partial bool HasSourceChoice { get; private set; }

    /// <summary>Camera being streamed (1-based), 0 until the first frame tells which one the server picked.</summary>
    [ObservableProperty]
    public partial int Camera { get; private set; }

    /// <summary>Background stream task of the current session (tests await it).</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Opens the panel for a device; the same device again closes it, another switches.</summary>
    [RelayCommand]
    private void Toggle(DeviceRowViewModel? device)
    {
        if (device is null)
        {
            return;
        }

        if (IsOpen && Device?.Id == device.Id)
        {
            Close();
            return;
        }

        Open(device);
    }

    /// <summary>Switches to another source of the open device and remembers the choice for this session.</summary>
    [RelayCommand]
    private void SelectSource(LiveViewSourceOption? option)
    {
        if (option is null || Device is null || !IsOpen)
        {
            return;
        }

        _chosenCamera[Device.Id] = option.Camera;
        if (option.Camera == Camera)
        {
            return;
        }

        StartStream(Device, option.Camera);
    }

    [RelayCommand]
    private void Close()
    {
        StopSession();
        StopSourcesLoad();
        Sources.Clear();
        HasSourceChoice = false;
        Camera = 0;
        IsOpen = false;
        Device = null;
        SetState(LiveViewState.Closed, "", "");
        SwapImage(null);
    }

    public void Open(DeviceRowViewModel device)
    {
        ArgumentNullException.ThrowIfNull(device);
        StopSourcesLoad();
        Sources.Clear();
        HasSourceChoice = false;
        Device = device;
        IsOpen = true;
        StartStream(device, _chosenCamera.GetValueOrDefault(device.Id));
        if (State != LiveViewState.Error)
        {
            LoadSources(device);
        }
    }

    private void StartStream(DeviceRowViewModel device, int camera)
    {
        StopSession();
        SwapImage(null);
        Camera = camera;
        MarkSelected();
        var cts = new CancellationTokenSource();
        _session = cts;
        var id = ++_sessionId;
        if (_decoders.SupportedCodecs.Count == 0)
        {
            SetState(LiveViewState.Error, "Error", DecoderUnavailableText);
            LogDecoderUnavailable(_logger, _decoders.UnavailableReason ?? "unknown");
            Running = Task.CompletedTask;
            return;
        }

        SetState(LiveViewState.Connecting, "Connecting", device.DisplayAddress);
        var request = new LiveViewRequest { DeviceId = device.Id, MaxWidth = MaxWidth, MaxHeight = MaxHeight, Fps = Fps, Camera = camera };
        request.AcceptedCodecs.AddRange(_decoders.SupportedCodecs);
        Running = Task.Run(() => RunAsync(id, request, cts.Token));
    }

    public void Dispose()
    {
        StopSession();
        StopSourcesLoad();
        SwapImage(null);
    }

    private void LoadSources(DeviceRowViewModel device)
    {
        var cts = new CancellationTokenSource();
        _sourcesLoad = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var sources = await _api.ListLiveViewSourcesAsync(device.Id, cts.Token).ConfigureAwait(false);
                _ui.Post(() =>
                {
                    if (cts.IsCancellationRequested || Device?.Id != device.Id)
                    {
                        return;
                    }

                    Sources.Clear();
                    foreach (var source in sources)
                    {
                        Sources.Add(new LiveViewSourceOption(source.Camera, source.Name));
                    }

                    HasSourceChoice = Sources.Count > 1;
                    MarkSelected();
                });
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or HttpRequestException)
            {
                LogStreamFailed(_logger, "video sources not available: " + ex.Message);
            }
        });
    }

    private void StopSourcesLoad()
    {
        if (_sourcesLoad is { } cts)
        {
            _sourcesLoad = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>Highlights the streamed source (the first one while the server default is used).</summary>
    private void MarkSelected()
    {
        var camera = Camera != 0 ? Camera : Sources.FirstOrDefault()?.Camera ?? 0;
        foreach (var option in Sources)
        {
            option.IsSelected = option.Camera == camera;
        }

        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnHasSourceChoiceChanged(bool value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnDeviceChanged(DeviceRowViewModel? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnStateChanged(LiveViewState value) => OnPropertyChanged(nameof(StateKind));

    private async Task RunAsync(int id, LiveViewRequest request, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            IVideoDecoder? decoder = null;
            var decoderCodec = VideoCodec.Unspecified;
            try
            {
                await foreach (var frame in _api.WatchLiveViewAsync(request, ct).ConfigureAwait(false))
                {
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
                        var info = string.Create(CultureInfo.InvariantCulture, $"{CodecName(frame.Codec)}  ·  {image.Width}x{image.Height}");
                        var camera = frame.Camera;
                        _ui.Post(() => Show(id, image, info, camera));
                    }
                }

                throw new RpcException(new Status(StatusCode.Unavailable, "The stream ended."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.PermissionDenied or StatusCode.FailedPrecondition or StatusCode.InvalidArgument)
            {
                LogStreamFailed(_logger, ex.Status.Detail);
                _ui.Post(() => SetStateFor(id, LiveViewState.Error, "Error", ex.Status.Detail));
                return;
            }
            catch (Exception ex) when (ex is RpcException or IOException or InvalidOperationException or HttpRequestException)
            {
                var delay = Backoff[Math.Min(attempt, Backoff.Count - 1)];
                attempt++;
                var reason = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
                LogStreamFailed(_logger, reason);
                var text = string.Create(CultureInfo.InvariantCulture, $"{reason} Retrying in {delay.TotalSeconds:0} s.");
                _ui.Post(() => SetStateFor(id, LiveViewState.Reconnecting, "Reconnecting", text));
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

    private void Show(int id, LiveImage image, string info, int camera)
    {
        if (id != _sessionId)
        {
            image.Dispose(); // panel switched or closed meanwhile
            return;
        }

        if (camera > 0 && camera != Camera)
        {
            Camera = camera;
            MarkSelected();
        }

        SwapImage(image);
        // fps = frames shown after the window started / time since then.
        var now = _time.GetTimestamp();
        if (_windowStart == 0)
        {
            _windowStart = now;
            _framesInWindow = 0;
            _streamInfo = info;
            SetState(LiveViewState.Live, "", _streamInfo); // no chip while live: the picture says it
            return;
        }

        _framesInWindow++;
        var elapsed = _time.GetElapsedTime(_windowStart, now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            _streamInfo = string.Create(CultureInfo.InvariantCulture, $"{info}  ·  {_framesInWindow / elapsed.TotalSeconds:0.0} fps");
            _framesInWindow = 0;
            _windowStart = now;
        }
        else if (State != LiveViewState.Live)
        {
            _streamInfo = info;
        }

        SetState(LiveViewState.Live, "", _streamInfo); // no chip while live: the picture says it
    }

    private void SetStateFor(int id, LiveViewState state, string stateText, string detail)
    {
        if (id == _sessionId)
        {
            SetState(state, stateText, detail);
        }
    }

    private void SetState(LiveViewState state, string stateText, string detail)
    {
        State = state;
        StateText = stateText;
        DetailText = detail;
    }

    private void SwapImage(LiveImage? image)
    {
        var old = _current;
        _current = image;
        Image = image?.Image;
        old?.Dispose();
        if (image is null)
        {
            _framesInWindow = 0;
            _windowStart = 0;
            _streamInfo = "";
        }
    }

    private void StopSession()
    {
        _sessionId++;
        if (_session is { } cts)
        {
            _session = null;
            cts.Cancel();
            _ = Running.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        }
    }

    private static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H265 => "H.265",
        VideoCodec.H264 => "H.264",
        _ => codec.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live view: video decoder not available ({Reason})")]
    private static partial void LogDecoderUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live view stream: {Reason}")]
    private static partial void LogStreamFailed(ILogger logger, string reason);
}

/// <summary>Default live view request: full HD at 25 fps, which any current PC decodes in software.</summary>
public static class LiveViewDefaults
{
    public const int Width = 1920;
    public const int Height = 1080;
    public const int Fps = 25;
}

/// <summary>
/// Whether a device row gets the live view, based on the device category from the server:
/// offered for devices with video (camera, encoder, intercom) and for
/// devices whose category is not known yet.
/// </summary>
public static class LiveViewSupport
{
    public static Avalonia.Data.Converters.FuncValueConverter<DeviceRowViewModel?, bool> IsSupportedConverter { get; } =
        new(row => row is not null && IsSupported(row));

    public static bool IsSupported(DeviceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The category comes from the device (basicdeviceinfo ProdType). Until it is known, allow
        // live view and let the stream tell.
        return row.HasVideo || row.Category == Oadm.Contracts.V1.DeviceCategory.Unknown;
    }
}
