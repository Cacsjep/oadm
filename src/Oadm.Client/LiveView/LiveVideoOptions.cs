using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.LiveView;

/// <summary>Which resolution a live video asks for.</summary>
public enum LiveVideoQuality
{
    /// <summary>The camera resolution that fits the picture's size (a resize swaps it silently), at the adaptive frame rate.</summary>
    Adaptive,

    /// <summary>The camera's largest resolution at <see cref="LiveVideoOptions.MaxFps"/>.</summary>
    Full,
}

/// <summary>
/// What a live video component offers where it is used: the side panel shows everything, a grid tile can turn parts off and
/// set the channel from outside. Every part is on by default.
/// </summary>
public sealed record LiveVideoOptions
{
    /// <summary>The channel buttons over the picture (several view areas, sensors or encoder channels); off when the place sets the channel.</summary>
    public bool ShowSourceSwitch { get; init; } = true;

    /// <summary>The fullscreen button (top left on hover) and the double-click.</summary>
    public bool AllowFullscreen { get; init; } = true;

    /// <summary>The Full / Adaptive switch (top left on hover).</summary>
    public bool AllowQualitySwitch { get; init; } = true;

    /// <summary>The button that opens zoom and focus or PTZ (bottom right, cameras that have them, role with "Optics Control").</summary>
    public bool ShowControlsButton { get; init; } = true;

    /// <summary>The zoom and focus or PTZ controls are open from the start (the button closes them).</summary>
    public bool ControlsOpen { get; init; } = true;

    /// <summary>PTZ parts: the circle that moves the camera, zoom (slider and mouse wheel), presets, Home, click to center.</summary>
    public bool PtzMove { get; init; } = true;

    public bool PtzZoom { get; init; } = true;

    public bool PtzPresets { get; init; } = true;

    public bool PtzHome { get; init; } = true;

    public bool PtzCenter { get; init; } = true;

    /// <summary>Zoom and focus parts of cameras with a remote lens: the zoom slider; focus with autofocus, area and reset.</summary>
    public bool OpticsZoom { get; init; } = true;

    public bool OpticsFocus { get; init; } = true;

    /// <summary>Frame rate of Full quality, and the upper bound of the adaptive one.</summary>
    public int MaxFps { get; init; } = LiveViewDefaults.Fps;

    /// <summary>The quality the component starts with.</summary>
    public LiveVideoQuality Quality { get; init; } = LiveVideoQuality.Adaptive;

    /// <summary>A size must rest this long before the resolution follows it.</summary>
    public TimeSpan ResizeDebounce { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A hidden component stops streaming after this time (and starts again when it is shown).</summary>
    public TimeSpan HiddenStopDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A new stream (resize, quality, reconnect) that does not deliver within this time shows the progress ring.</summary>
    public TimeSpan SlowStartDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Client-wide live video settings, shared by every live video component: "Show video details over the picture", the
/// adaptive frame rate (Settings > This computer, saved in the client settings file) and whether the role may change the
/// camera ("Optics Control").
/// </summary>
public sealed partial class LiveVideoSettings : ObservableObject
{
    public const int MinAdaptiveFps = 1;
    public const int MaxAdaptiveFps = 30;

    private readonly IClientSettingsStore? _store;

    public LiveVideoSettings(IClientSettingsStore? store = null)
    {
        _store = store;
        ShowDetails = store?.Current.ShowVideoDetails ?? false;
        AdaptiveFps = Math.Clamp(store?.Current.AdaptiveFps ?? 20, MinAdaptiveFps, MaxAdaptiveFps);
    }

    /// <summary>The small overlay with codec, resolution, frame rate, bitrate and decoder.</summary>
    [ObservableProperty]
    public partial bool ShowDetails { get; set; }

    /// <summary>Frame rate of Adaptive quality (1..30).</summary>
    [ObservableProperty]
    public partial int AdaptiveFps { get; set; }

    /// <summary>The role may change the camera (zoom, focus, PTZ): "Optics Control".</summary>
    [ObservableProperty]
    public partial bool CanControlOptics { get; set; } = true;

    partial void OnShowDetailsChanged(bool value)
    {
        if (_store is not null)
        {
            _store.Current.ShowVideoDetails = value;
            _store.Save();
        }
    }

    partial void OnAdaptiveFpsChanged(int value)
    {
        var clamped = Math.Clamp(value, MinAdaptiveFps, MaxAdaptiveFps);
        if (clamped != value)
        {
            AdaptiveFps = clamped;
            return;
        }

        if (_store is not null)
        {
            _store.Current.AdaptiveFps = value;
            _store.Save();
        }
    }
}
