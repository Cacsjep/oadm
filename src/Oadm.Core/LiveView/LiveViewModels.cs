using Oadm.Core.Vapix;

namespace Oadm.Core.LiveView;

/// <summary>Video codecs of the live view.</summary>
public enum VideoCodecKind
{
    H264 = 1,
    H265 = 2,
}

/// <summary>Identifies one shared upstream camera connection.</summary>
/// <param name="Camera">1-based VAPIX camera (view area, sensor or encoder channel).</param>
public sealed record LiveStreamKey(Guid DeviceId, VideoCodecKind Codec, ImageSize Size, int Fps, int Camera = 1);

/// <summary>
/// One access unit (Annex B) from a camera. Instances are shared between viewers and never modified.
/// </summary>
/// <param name="CodecConfig">Parameter sets (Annex B), set on keyframes.</param>
/// <param name="RtpTimestamp">90 kHz RTP timestamp.</param>
public sealed record LiveVideoFrame(
    VideoCodecKind Codec,
    byte[] Data,
    bool IsKeyframe,
    int Width,
    int Height,
    DateTimeOffset Captured,
    byte[]? CodecConfig = null,
    long RtpTimestamp = 0);

/// <summary>What a camera can stream, as far as the live view cares.</summary>
/// <param name="Sources">Streamable sources; null or empty means one source (camera 1) with the device resolutions.</param>
public sealed record LiveViewCapabilities(IReadOnlyList<VideoCodecKind> Codecs, IReadOnlyList<ImageSize> Resolutions, IReadOnlyList<VideoSourceInfo>? Sources = null)
{
    /// <summary>The sources, at least camera 1.</summary>
    public IReadOnlyList<VideoSourceInfo> EffectiveSources =>
        Sources is { Count: > 0 } ? Sources : [new VideoSourceInfo(1, "Camera", 0, Resolutions)];

    /// <summary>The requested source (1-based camera), or the first one for 0. Null when that camera does not exist.</summary>
    public VideoSourceInfo? FindSource(int camera) =>
        camera <= 0 ? EffectiveSources[0] : EffectiveSources.FirstOrDefault(s => s.Camera == camera);

    /// <summary>Maps VAPIX format names (Properties.Image.Format) to codecs. Unknown or empty: H.264 (every Axis camera).</summary>
    public static LiveViewCapabilities FromImageCapabilities(ImageCapabilities image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var codecs = new List<VideoCodecKind>();
        foreach (var format in image.Formats)
        {
            VideoCodecKind? codec = format switch
            {
                "h264" => VideoCodecKind.H264,
                "h265" => VideoCodecKind.H265,
                _ => null,
            };
            if (codec is { } c && !codecs.Contains(c))
            {
                codecs.Add(c);
            }
        }

        if (image.Formats.Count == 0)
        {
            codecs.Add(VideoCodecKind.H264);
        }

        return new LiveViewCapabilities(codecs, image.Resolutions, image.Sources);
    }
}

/// <summary>Codec and resolution choice for a live view request.</summary>
public static class LiveViewNegotiation
{
    public const int DefaultWidth = 640;
    public const int DefaultHeight = 360;
    public const int DefaultFps = 10;
    public const int MaxFps = 30;

    /// <summary>Preference order: H.265, then H.264.</summary>
    public static readonly IReadOnlyList<VideoCodecKind> Preference = [VideoCodecKind.H265, VideoCodecKind.H264];

    /// <summary>
    /// Codecs to try in order: the preference list filtered by what both the camera and the client
    /// support. An empty client list means H.264 only. Empty result: no common codec.
    /// </summary>
    public static IReadOnlyList<VideoCodecKind> Order(IEnumerable<VideoCodecKind> camera, IEnumerable<VideoCodecKind> client)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(client);
        var cameraSet = camera.ToHashSet();
        var clientSet = client.ToHashSet();
        if (clientSet.Count == 0)
        {
            clientSet.Add(VideoCodecKind.H264);
        }

        return Preference.Where(c => cameraSet.Contains(c) && clientSet.Contains(c)).ToList();
    }

    /// <summary>
    /// Largest supported resolution that fits into maxWidth x maxHeight and keeps the sensor aspect
    /// ratio (the aspect of the largest resolution). Falls back to the smallest of that aspect, and
    /// to the requested box itself when the camera reported no resolutions.
    /// </summary>
    public static ImageSize ChooseResolution(IReadOnlyList<ImageSize> supported, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(supported);
        maxWidth = maxWidth > 0 ? maxWidth : DefaultWidth;
        maxHeight = maxHeight > 0 ? maxHeight : DefaultHeight;
        if (supported.Count == 0)
        {
            return new ImageSize(maxWidth, maxHeight);
        }

        var largest = supported.MaxBy(s => (long)s.Width * s.Height);
        var sameAspect = supported.Where(s => Math.Abs(s.Aspect - largest.Aspect) < 0.02).ToList();
        var fitting = sameAspect.Where(s => s.Width <= maxWidth && s.Height <= maxHeight).ToList();
        if (fitting.Count > 0)
        {
            return fitting.MaxBy(s => (long)s.Width * s.Height);
        }

        return sameAspect.MinBy(s => (long)s.Width * s.Height);
    }

    public static int ClampFps(int fps) => fps <= 0 ? DefaultFps : Math.Min(fps, MaxFps);
}
