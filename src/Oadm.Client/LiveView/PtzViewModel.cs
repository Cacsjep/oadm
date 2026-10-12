using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

/// <summary>
/// PTZ of the camera in the live view: the circle in the middle of the picture moves the camera while it is dragged (the
/// further from the center, the faster), the mouse wheel zooms, a click centers the camera on that point, and the bar
/// offers Home, the presets and a zoom slider. Shown only for a PTZ camera and a role with "Optics Control". A drag sends
/// the current speed every <see cref="RepeatInterval"/>; the camera stops by itself one second after the last one, so a
/// lost connection never leaves it turning.
/// </summary>
public sealed partial class PtzViewModel(IOadmApi api, TimeProvider? time = null) : ObservableObject, IDisposable
{
    /// <summary>Zoom steps of one mouse wheel notch (of 1..9999).</summary>
    public const int WheelStep = 400;

    /// <summary>Below this share of the circle's reach a drag does not move (no creeping).</summary>
    public const double DeadZone = 0.08;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private string? _deviceId;
    private int _camera = 1;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _drag;
    private CancellationTokenSource? _pendingZoom;
    private CancellationTokenSource? _pendingWheel;
    private CancellationTokenSource? _refresh;
    private (int X, int Y) _speed;
    private (int X, int Y)? _sent = (0, 0);
    private bool _pumping;
    private int _generation;
    private int _wheelSteps;
    private bool _applying;

    /// <summary>How often a drag repeats its speed (the camera stops 1 s after the last one).</summary>
    public TimeSpan RepeatInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the zoom slider and the wheel rest before they are sent.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>After a move the zoom slider reads the camera's zoom again after this time.</summary>
    public TimeSpan RefreshDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Parts the place shows (<see cref="LiveVideoOptions"/>): the circle, zoom (slider and wheel), presets, Home, click to center.</summary>
    public bool AllowMove { get; init; } = true;

    public bool AllowZoom { get; init; } = true;

    public bool AllowPresets { get; init; } = true;

    public bool AllowHome { get; init; } = true;

    public bool AllowCenter { get; init; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool CanControl { get; set; } = true;

    /// <summary>The role lost Optics Control: a running drag stops at once.</summary>
    partial void OnCanControlChanged(bool value)
    {
        if (!value)
        {
            EndDrag();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool IsSupported { get; private set; }

    public bool IsVisible => IsSupported && CanControl;

    [ObservableProperty]
    public partial bool CanMove { get; private set; }

    [ObservableProperty]
    public partial bool CanZoom { get; private set; }

    [ObservableProperty]
    public partial bool CanCenter { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HomeCommand))]
    public partial bool CanGoHome { get; private set; }

    public ObservableCollection<string> Presets { get; } = [];

    [ObservableProperty]
    public partial bool HasPresets { get; private set; }

    /// <summary>Choosing a preset moves the camera there.</summary>
    [ObservableProperty]
    public partial string? SelectedPreset { get; set; }

    /// <summary>1 (wide) .. 9999 (tele); the slider binds it two-way.</summary>
    [ObservableProperty]
    public partial double Zoom { get; set; } = 1;

    /// <summary>A drag of the circle runs: the overlay draws the line to the pointer.</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; private set; }

    /// <summary>The camera's refusal or "The camera did not answer."; empty otherwise.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>Reads what the PTZ camera offers on the channel; called when the live view opens a device or switches source.</summary>
    public async Task LoadAsync(string deviceId, int camera)
    {
        Clear();
        _deviceId = deviceId;
        _camera = Math.Max(1, camera);
        var cts = new CancellationTokenSource();
        _load = cts;
        try
        {
            var state = await api.GetPtzAsync(deviceId, _camera, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested && _deviceId == deviceId)
            {
                Apply(state, presets: true);
            }
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or HttpRequestException)
        {
            // PTZ is an extra of the live view: without an answer there are no controls.
        }
    }

    public void Clear()
    {
        if (_deviceId is not null && _drag is not null)
        {
            _ = SendAsync(new MovePtzRequest { Move = PtzMove.Stop });
        }

        Cancel(ref _load);
        Cancel(ref _drag);
        Cancel(ref _pendingZoom);
        Cancel(ref _pendingWheel);
        Cancel(ref _refresh);
        _generation++;
        _speed = (0, 0);
        _sent = (0, 0);
        _wheelSteps = 0;
        _deviceId = null;
        IsSupported = false;
        IsDragging = false;
        Presets.Clear();
        HasPresets = false;
        StatusText = "";
    }

    public void Dispose() => Clear();

    /// <summary>The circle is grabbed: from now on <see cref="DragTo"/> sets the speed, repeated until <see cref="EndDrag"/>.</summary>
    public void BeginDrag()
    {
        if (!CanMove || _deviceId is null)
        {
            return;
        }

        Cancel(ref _drag);
        var cts = new CancellationTokenSource();
        _drag = cts;
        _speed = (0, 0);
        _sent = (0, 0);
        IsDragging = true;
        _ = RepeatAsync(cts.Token);
    }

    /// <summary>The pointer's offset from the center as a share of the circle's reach (-1..1, y down on screen).</summary>
    public void DragTo(double x, double y)
    {
        if (!IsDragging)
        {
            return;
        }

        static int Speed(double v) => Math.Abs(v) < DeadZone ? 0 : (int)Math.Round(Math.Clamp(v, -1, 1) * 100);
        var speed = (Speed(x), -Speed(y)); // up on screen = tilt up
        if (speed != _speed)
        {
            _speed = speed;
            Pump();
        }
    }

    /// <summary>The circle is released: stop at once.</summary>
    public void EndDrag()
    {
        if (!IsDragging)
        {
            return;
        }

        Cancel(ref _drag);
        IsDragging = false;
        _speed = (0, 0);
        _sent = null; // the stop always goes out, after any move still on its way
        Pump();
        RefreshLater();
    }

    /// <summary>Mouse wheel over the picture: notches add up and are sent once the wheel rests.</summary>
    public void Wheel(double notches)
    {
        if (!CanZoom || _deviceId is null || notches == 0)
        {
            return;
        }

        _wheelSteps += (int)Math.Round(notches * WheelStep);
        Cancel(ref _pendingWheel);
        var cts = new CancellationTokenSource();
        _pendingWheel = cts;
        _ = AfterAsync(Debounce, cts, async () =>
        {
            var steps = _wheelSteps;
            _wheelSteps = 0;
            if (steps != 0)
            {
                await SendAsync(new MovePtzRequest { Move = PtzMove.ZoomBy, Value = steps }).ConfigureAwait(true);
                RefreshLater();
            }
        });
    }

    /// <summary>A click on the picture (fractions 0..1): that point becomes the center.</summary>
    public void CenterAt(double x, double y)
    {
        if (!CanCenter || _deviceId is null)
        {
            return;
        }

        _ = SendAsync(new MovePtzRequest { Move = PtzMove.Center, X = Math.Clamp(x, 0, 1), Y = Math.Clamp(y, 0, 1) });
        RefreshLater();
    }

    private bool HasHome() => CanGoHome;

    [RelayCommand(CanExecute = nameof(HasHome))]
    private async Task HomeAsync()
    {
        await SendAsync(new MovePtzRequest { Move = PtzMove.Home }).ConfigureAwait(true);
        RefreshLater();
    }

    partial void OnSelectedPresetChanged(string? value)
    {
        if (!_applying && !string.IsNullOrEmpty(value))
        {
            _ = SendAsync(new MovePtzRequest { Move = PtzMove.Preset, Preset = value });
            RefreshLater();
            // Back to "Presets", so choosing the same one again goes there again.
            _applying = true;
            SelectedPreset = null;
            _applying = false;
        }
    }

    partial void OnZoomChanged(double value)
    {
        if (_applying || !CanZoom || _deviceId is null)
        {
            return;
        }

        Cancel(ref _pendingZoom);
        var cts = new CancellationTokenSource();
        _pendingZoom = cts;
        _ = AfterAsync(Debounce, cts, () => SendAsync(new MovePtzRequest { Move = PtzMove.Zoom, Value = (int)Math.Round(Math.Clamp(value, 1, 9999)) }));
    }

    private static MovePtzRequest Move((int X, int Y) speed) =>
        speed == (0, 0) ? new MovePtzRequest { Move = PtzMove.Stop } : new MovePtzRequest { Move = PtzMove.Continuous, X = speed.X, Y = speed.Y };

    /// <summary>Repeats the current speed while the drag lasts, so the camera keeps moving (it stops 1 s after the last).</summary>
    private async Task RepeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(RepeatInterval, _time, ct).ConfigureAwait(true);
                if (!ct.IsCancellationRequested && _speed != (0, 0))
                {
                    _sent = null;
                    Pump();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Sends the newest drag speed, one request at a time: pointer moves only replace the speed that goes next, so a
    /// drag never floods the camera and a stop is never overtaken by a move still on its way.
    /// </summary>
    private void Pump()
    {
        if (_pumping || _deviceId is null)
        {
            return;
        }

        _pumping = true;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        var generation = _generation;
        try
        {
            while (_sent != _speed && generation == _generation)
            {
                var speed = _speed;
                _sent = speed;
                await SendAsync(Move(speed)).ConfigureAwait(true);
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    /// <summary>Reads the zoom again after the camera moved (presets, home, the wheel and a center change it).</summary>
    private void RefreshLater()
    {
        if (_deviceId is not { } deviceId)
        {
            return;
        }

        Cancel(ref _refresh);
        var cts = new CancellationTokenSource();
        _refresh = cts;
        var camera = _camera;
        _ = AfterAsync(RefreshDelay, cts, async () =>
        {
            try
            {
                var state = await api.GetPtzAsync(deviceId, camera, cts.Token).ConfigureAwait(true);
                if (_deviceId == deviceId && !cts.IsCancellationRequested && state.Supported)
                {
                    Apply(state, presets: false);
                }
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or HttpRequestException)
            {
            }
        });
    }

    private async Task AfterAsync(TimeSpan delay, CancellationTokenSource cts, Func<Task> action)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested)
        {
            await action().ConfigureAwait(true);
        }
    }

    private async Task SendAsync(MovePtzRequest request)
    {
        if (_deviceId is not { } deviceId)
        {
            return;
        }

        request.DeviceId = deviceId;
        request.Camera = _camera;
        var generation = _generation;
        try
        {
            await api.MovePtzAsync(request, CancellationToken.None).ConfigureAwait(true);
            if (generation == _generation)
            {
                StatusText = "";
            }
        }
        catch (RpcException ex)
        {
            if (generation == _generation)
            {
                StatusText = string.IsNullOrEmpty(ex.Status.Detail) ? "The camera did not answer." : ex.Status.Detail;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
        {
            if (generation == _generation)
            {
                StatusText = "The camera did not answer.";
            }
        }
    }

    private void Apply(PtzState state, bool presets)
    {
        _applying = true;
        try
        {
            CanMove = state.CanMove && AllowMove;
            CanZoom = state.CanZoom && AllowZoom;
            CanCenter = state.CanCenter && AllowCenter;
            CanGoHome = state.CanGoHome && AllowHome;
            Zoom = Math.Clamp(state.Zoom, 1, 9999);
            if (presets)
            {
                Presets.Clear();
                foreach (var preset in AllowPresets ? state.Presets : [])
                {
                    Presets.Add(preset);
                }

                HasPresets = Presets.Count > 0;
                SelectedPreset = null;
            }

            IsSupported = state.Supported && (CanMove || CanZoom || CanCenter || CanGoHome || HasPresets);
        }
        finally
        {
            _applying = false;
        }
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
}
