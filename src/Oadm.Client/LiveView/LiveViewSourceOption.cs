using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Client.LiveView;

/// <summary>One video source (view area, sensor or encoder channel) in the live view source switch.</summary>
public sealed partial class LiveViewSourceOption(int camera, string name) : ObservableObject
{
    /// <summary>1-based VAPIX camera number.</summary>
    public int Camera { get; } = camera;

    /// <summary>Device label, e.g. "View Area 2" (tooltip).</summary>
    public string Name { get; } = name;

    /// <summary>Short label on the segmented button: the camera number.</summary>
    public string Label => Camera.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
