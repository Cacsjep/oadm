using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Client.LiveView;

/// <summary>One video source (view area, sensor or encoder channel) in the live view source switch.</summary>
public sealed partial class LiveViewSourceOption(int camera, string name, int sensor = 0) : ObservableObject
{
    /// <summary>0-based image source (sensor) of the source: its lens for zoom and focus.</summary>
    public int Sensor { get; } = sensor;

    /// <summary>1-based VAPIX camera number.</summary>
    public int Camera { get; } = camera;

    /// <summary>Device label, e.g. "View Area 2" (tooltip).</summary>
    public string Name { get; } = name;

    /// <summary>Short label on the segmented button: the camera number.</summary>
    public string Label => Camera.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
