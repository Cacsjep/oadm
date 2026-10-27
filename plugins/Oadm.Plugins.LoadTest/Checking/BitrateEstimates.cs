namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>
/// The expected rate of a camera stream before a check (the plan and the confirmation). AN ESTIMATE, not a measurement:
/// typical Axis cameras with their default compression and Zipstream on a scene with some motion, in daylight. The real
/// rate depends on the scene, the compression, Zipstream and the light; the check measures it. This table is the only
/// place of these numbers (docs/spec/load-test.md, "Open before the build").
/// </summary>
internal static class BitrateEstimates
{
    /// <summary>Low light (the exposure at its maximum): more noise, about 2.5 times the rate.</summary>
    public const double LowLightFactor = 2.5;

    /// <summary>The frame rate the table is for.</summary>
    private const double TableFps = 30;

    /// <summary>H.265 needs about 60 % of the H.264 rate.</summary>
    private const double H265Factor = 0.6;

    /// <summary>Up to this many pixels: Mbit/s of H.264 at 30 fps.</summary>
    private static readonly (long MaxPixels, double Mbps)[] H264At30Fps =
    [
        (640L * 480, 1.0),       // VGA and smaller
        (1280L * 960, 2.0),      // 720p, 1.2 MP
        (1920L * 1200, 4.0),     // 1080p, 2.3 MP
        (2688L * 1520, 6.0),     // 4 MP
        (3072L * 2048, 8.0),     // 5 to 6 MP
        (3840L * 2160, 12.0),    // 4K, 8 MP
        (long.MaxValue, 16.0),   // larger sensors and panoramas
    ];

    /// <summary>The expected Mbit/s of one stream in normal light.</summary>
    public static double Mbps(int width, int height, int fps, string codec)
    {
        var pixels = (long)Math.Max(1, width) * Math.Max(1, height);
        var rate = Array.Find(H264At30Fps, r => pixels <= r.MaxPixels).Mbps;
        var fpsFactor = Math.Clamp(Math.Max(1, fps) / TableFps, 0.1, 2);
        var codecFactor = string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase) ? H265Factor : 1;
        return rate * fpsFactor * codecFactor;
    }
}
