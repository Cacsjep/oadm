using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;

namespace Oadm.Client.LiveView;

/// <summary>
/// The live view side panel: which device is shown, opened and closed from the device icon. The picture and everything
/// over it is the shared <see cref="LiveVideoViewModel"/> (<see cref="Video"/>); the panel adds the camera's name and the
/// close button. The chosen channel is remembered per device for the client session.
/// </summary>
public sealed partial class LiveViewViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<string, int> _chosenCamera = [];

    public LiveViewViewModel(IOadmApi api, IVideoDecoderFactory decoders, IUiDispatcher ui, ILogger<LiveViewViewModel> logger, LiveVideoSettings? settings = null,
        TimeProvider? time = null, GpuDecodingService? gpu = null)
    {
        Video = new LiveVideoViewModel(api, decoders, ui, logger, settings ?? new LiveVideoSettings(), new LiveVideoOptions(), time, gpu);
        Video.CameraChosen += (_, camera) =>
        {
            if (Device is { } device)
            {
                _chosenCamera[device.Id] = camera;
            }
        };
        Video.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveVideoViewModel.SourceName))
            {
                OnPropertyChanged(nameof(Title));
            }
        };
    }

    /// <summary>The picture with its overlays.</summary>
    public LiveVideoViewModel Video { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial DeviceRowViewModel? Device { get; private set; }

    /// <summary>The model, plus the channel's name while the device has several ("P3265-V · View Area 2").</summary>
    public string Title
    {
        get
        {
            if (Device is null)
            {
                return "";
            }

            var model = string.IsNullOrEmpty(Device.Model) ? Device.Serial : Device.Model;
            return Video.SourceName is { } source ? $"{model}  ·  {source}" : model;
        }
    }

    /// <summary>The user's role may watch live view ("devices.liveView"); without it the device icons do nothing and an open panel closes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    public partial bool IsAllowed { get; set; } = true;

    partial void OnIsAllowedChanged(bool value)
    {
        if (!value && IsOpen)
        {
            Close();
        }
    }

    private bool CanToggle(DeviceRowViewModel? device) => IsAllowed;

    /// <summary>Opens the panel for a device; the same device again closes it, another switches.</summary>
    [RelayCommand(CanExecute = nameof(CanToggle))]
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

    [RelayCommand]
    private void Close()
    {
        Video.Stop();
        IsOpen = false;
        Device = null;
    }

    /// <summary>Shows a device (again: a changed RTSP port starts a new stream).</summary>
    public void Open(DeviceRowViewModel device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        IsOpen = true;
        var model = string.IsNullOrEmpty(device.Model) ? device.Serial : device.Model;
        Video.Start(device.Id, _chosenCamera.GetValueOrDefault(device.Id), model);
    }

    public void Dispose() => Video.Dispose();
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
