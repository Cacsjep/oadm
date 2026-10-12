using Avalonia.Controls;

namespace Oadm.Sdk.Client;

/// <summary>
/// The host's live video for plugin pages (<see cref="ICorePluginClientContext.LiveVideo"/>): the same picture as the
/// Devices page's live view, with adaptive quality, fullscreen, the channel switch and the zoom, focus and PTZ controls.
/// The server streams the camera with the stored login; passwords and stream addresses never reach the plugin.
/// </summary>
public interface ILiveVideoAccess
{
    /// <summary>Creates one live video (call on the UI thread); dispose it when its place goes away.</summary>
    ILiveVideo Create(LiveVideoPlace place);
}

/// <summary>What a live video offers in its place. Every part is on by default, the controls start closed.</summary>
public sealed record LiveVideoPlace
{
    /// <summary>The channel buttons on cameras with several view areas, sensors or encoder channels.</summary>
    public bool ShowSourceSwitch { get; init; } = true;

    /// <summary>The fullscreen button and the double-click.</summary>
    public bool AllowFullscreen { get; init; } = true;

    /// <summary>The Full / Adaptive switch.</summary>
    public bool AllowQualitySwitch { get; init; } = true;

    /// <summary>The button that shows zoom and focus or PTZ (cameras that have them, roles with "Optics Control").</summary>
    public bool ShowControlsButton { get; init; } = true;

    /// <summary>The controls are open from the start.</summary>
    public bool ControlsOpen { get; init; }

    /// <summary>Fullscreen opens with the controls open.</summary>
    public bool FullscreenControlsOpen { get; init; } = true;
}

/// <summary>One live video: <see cref="View"/> goes into the page; <see cref="Start"/> shows a device.</summary>
public interface ILiveVideo : IDisposable
{
    /// <summary>The picture with everything over it. It stops streaming by itself while it is hidden.</summary>
    Control View { get; }

    /// <summary>Shows the device's channel (1-based camera; 0 = the first).</summary>
    void Start(Guid deviceId, int camera = 0);

    /// <summary>Stops the stream and clears the picture (Start shows a device again).</summary>
    void Close();

    /// <summary>The channel shown (1-based), 0 before the camera answered.</summary>
    int Camera { get; }

    /// <summary>The user chose another channel with the channel buttons (1-based camera).</summary>
    event EventHandler<int>? CameraChosen;
}
