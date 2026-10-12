using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

/// <summary>
/// Zoom and focus of the camera in the live view (Optics Control): the bar over the bottom of the picture. Shown only for a
/// camera with remote zoom or focus and a role with "Optics Control"; other cameras show nothing. Slider moves are sent
/// once the slider rests for <see cref="Debounce"/>; Autofocus, Reset focus and a new autofocus area wait for the camera's
/// focus search. Every change goes through the server (the stored login); the reply is the camera's state afterwards.
/// </summary>
public sealed partial class OpticsViewModel(IOadmApi api, TimeProvider? time = null) : ObservableObject, IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private string? _deviceId;
    private int _sensor;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _pendingZoom;
    private CancellationTokenSource? _pendingFocus;
    private bool _applying;

    /// <summary>How long a slider must rest before its value is sent.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The role has "Optics Control" (set by the device page from the session).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool CanControl { get; set; } = true;

    /// <summary>The camera has remote zoom or focus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool IsSupported { get; private set; }

    /// <summary>The bar shows: a supported camera and a role that may change it.</summary>
    public bool IsVisible => IsSupported && CanControl;

    [ObservableProperty]
    public partial bool HasZoom { get; private set; }

    [ObservableProperty]
    public partial bool HasFocus { get; private set; }

    [ObservableProperty]
    public partial double MaxZoom { get; private set; } = 1;

    /// <summary>1 (wide) to <see cref="MaxZoom"/> (tele); the slider binds it two-way.</summary>
    [ObservableProperty]
    public partial double Zoom { get; set; } = 1;

    /// <summary>0 (near) to 100 (far); the slider binds it two-way.</summary>
    [ObservableProperty]
    public partial double Focus { get; set; }

    /// <summary>A focus search runs (Autofocus, Reset focus, a new autofocus area): the buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutofocusCommand), nameof(ResetFocusCommand), nameof(EditAreaCommand), nameof(ApplyAreaCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Focusing" while a search runs, or the camera's refusal; empty otherwise.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsError { get; private set; }

    /// <summary>The autofocus area is drawn over the picture and can be dragged anew.</summary>
    [ObservableProperty]
    public partial bool IsEditingArea { get; private set; }

    /// <summary>The autofocus area as fractions of the picture (x, y, width, height); the overlay binds it two-way.</summary>
    [ObservableProperty]
    public partial Avalonia.Rect Area { get; set; } = new(0.25, 0.25, 0.5, 0.5);

    private Avalonia.Rect _cameraArea = new(0.25, 0.25, 0.5, 0.5);

    /// <summary>Reads the optics of the source's sensor (0-based); called when the live view opens a device or switches source.</summary>
    public async Task LoadAsync(string deviceId, int sensor = 0)
    {
        Clear();
        _deviceId = deviceId;
        _sensor = Math.Max(0, sensor);
        var cts = new CancellationTokenSource();
        _load = cts;
        try
        {
            var state = await api.GetOpticsAsync(deviceId, _sensor, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested && _deviceId == deviceId)
            {
                Apply(state);
            }
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or HttpRequestException)
        {
            // Optics are an extra of the live view: without an answer there are no controls.
        }
    }

    /// <summary>The live view closed or switched devices.</summary>
    public void Clear()
    {
        Cancel(ref _load);
        Cancel(ref _pendingZoom);
        Cancel(ref _pendingFocus);
        _deviceId = null;
        IsSupported = false;
        HasZoom = false;
        HasFocus = false;
        IsEditingArea = false;
        IsBusy = false;
        SetStatus("", false);
    }

    public void Dispose() => Clear();

    partial void OnZoomChanged(double value)
    {
        if (!_applying && HasZoom)
        {
            _ = SendLaterAsync(ref _pendingZoom, new SetOpticsRequest { Magnification = Math.Clamp(value, 1, MaxZoom) });
        }
    }

    partial void OnFocusChanged(double value)
    {
        if (!_applying && HasFocus)
        {
            _ = SendLaterAsync(ref _pendingFocus, new SetOpticsRequest { FocusPosition = Math.Clamp(value / 100, 0, 1) });
        }
    }

    private bool CanSearch() => HasFocus && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task AutofocusAsync() => SearchAsync(new SetOpticsRequest { Autofocus = true });

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task ResetFocusAsync() => SearchAsync(new SetOpticsRequest { ResetFocus = true });

    /// <summary>Autofocus area: shows the current area over the picture; Set sends a new one, Cancel leaves it.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void EditArea()
    {
        if (IsEditingArea)
        {
            CancelArea();
            return;
        }

        Area = _cameraArea;
        IsEditingArea = true;
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task ApplyAreaAsync()
    {
        var area = Area;
        IsEditingArea = false;
        await SearchAsync(new SetOpticsRequest
        {
            FocusWindow = new OpticsWindow { X = area.X, Y = area.Y, Width = area.Width, Height = area.Height },
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelArea()
    {
        IsEditingArea = false;
        Area = _cameraArea;
    }

    private async Task SearchAsync(SetOpticsRequest request)
    {
        IsBusy = true;
        SetStatus("Focusing", false);
        try
        {
            await SendAsync(request).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            if (!IsError)
            {
                SetStatus("", false);
            }
        }
    }

    private Task SendLaterAsync(ref CancellationTokenSource? pending, SetOpticsRequest request)
    {
        Cancel(ref pending);
        var cts = new CancellationTokenSource();
        pending = cts;
        return SendAfterDelayAsync(cts, request);
    }

    private async Task SendAfterDelayAsync(CancellationTokenSource cts, SetOpticsRequest request)
    {
        try
        {
            if (Debounce > TimeSpan.Zero)
            {
                await Task.Delay(Debounce, _time, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested)
        {
            // The lens is still moving when the camera answers: the slider keeps the user's value.
            await SendAsync(request, applyAnswer: false).ConfigureAwait(true);
        }
    }

    private async Task SendAsync(SetOpticsRequest request, bool applyAnswer = true)
    {
        if (_deviceId is not { } deviceId)
        {
            return;
        }

        request.DeviceId = deviceId;
        request.Sensor = _sensor;
        try
        {
            var state = await api.SetOpticsAsync(request, CancellationToken.None).ConfigureAwait(true);
            if (_deviceId == deviceId)
            {
                SetStatus("", false);
                if (applyAnswer)
                {
                    Apply(state);
                }
            }
        }
        catch (RpcException ex)
        {
            if (_deviceId == deviceId)
            {
                SetStatus(string.IsNullOrEmpty(ex.Status.Detail) ? "The camera did not answer." : ex.Status.Detail, true);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            SetStatus("The camera did not answer.", true);
        }
    }

    /// <summary>Takes the camera's state without sending it back.</summary>
    private void Apply(OpticsState state)
    {
        _applying = true;
        try
        {
            HasZoom = state.HasZoom;
            HasFocus = state.HasFocus;
            MaxZoom = Math.Max(1, state.MaxMagnification);
            Zoom = Math.Clamp(state.Magnification, 1, MaxZoom);
            Focus = Math.Clamp(state.FocusPosition, 0, 1) * 100;
            if (state.FocusWindow is { } w && w.Width > 0 && w.Height > 0)
            {
                _cameraArea = new Avalonia.Rect(w.X, w.Y, w.Width, w.Height);
                if (!IsEditingArea)
                {
                    Area = _cameraArea;
                }
            }

            IsSupported = state.Supported && (HasZoom || HasFocus);
        }
        finally
        {
            _applying = false;
        }

        AutofocusCommand.NotifyCanExecuteChanged();
        ResetFocusCommand.NotifyCanExecuteChanged();
        EditAreaCommand.NotifyCanExecuteChanged();
        ApplyAreaCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string text, bool error)
    {
        StatusText = text;
        IsError = error;
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
