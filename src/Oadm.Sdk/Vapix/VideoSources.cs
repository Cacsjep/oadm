using System.Globalization;

namespace Oadm.Sdk.Vapix;

/// <summary>A video resolution in pixels.</summary>
public readonly record struct VideoResolution(int Width, int Height)
{
    public double Aspect => Height == 0 ? 0 : (double)Width / Height;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");
}

/// <summary>
/// One video source of a device: a view area, a sensor of a multisensor camera or a channel of a video
/// encoder. VAPIX addresses it as <c>camera=N</c> (1-based), e.g. <c>/axis-cgi/jpg/image.cgi?camera=2</c>.
/// </summary>
/// <param name="Camera">1-based VAPIX camera number.</param>
/// <param name="Name">Label from <c>Image.I&lt;n&gt;.Name</c>, e.g. "View Area 1" or "Camera 2"; "Camera" for a device with one source.</param>
/// <param name="Sensor">0-based image source (sensor or input) it shows.</param>
/// <param name="Resolutions">Resolutions of this source, largest first as the device lists them; may be empty.</param>
public sealed record VideoSource(int Camera, string Name, int Sensor, IReadOnlyList<VideoResolution> Resolutions);

/// <summary>Resolution choice shared by the live view and plugins (snapshots).</summary>
public static class VideoResolutions
{
    /// <summary>
    /// Largest supported resolution that fits into <paramref name="maxWidth"/> x <paramref name="maxHeight"/> and
    /// keeps the sensor aspect ratio (the aspect of the largest resolution). Falls back to the smallest of that
    /// aspect, and to the requested box itself when the device reported no resolutions.
    /// </summary>
    public static VideoResolution Choose(IReadOnlyList<VideoResolution> supported, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(supported);
        if (supported.Count == 0)
        {
            return new VideoResolution(maxWidth, maxHeight);
        }

        var largest = supported.MaxBy(s => (long)s.Width * s.Height);
        var sameAspect = supported.Where(s => Math.Abs(s.Aspect - largest.Aspect) < 0.02).ToList();
        var fitting = sameAspect.Where(s => s.Width <= maxWidth && s.Height <= maxHeight).ToList();
        return fitting.Count > 0
            ? fitting.MaxBy(s => (long)s.Width * s.Height)
            : sameAspect.MinBy(s => (long)s.Width * s.Height);
    }
}
