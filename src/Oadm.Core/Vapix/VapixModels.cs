using Oadm.Sdk.Devices;

namespace Oadm.Core.Vapix;

/// <summary>Network related device properties shown in the device grid.</summary>
/// <param name="DhcpEnabled">Network.BootProto == dhcp.</param>
/// <param name="HttpsEnabled">HTTPS.Enabled, null when the device does not report it.</param>
/// <param name="Dot1xEnabled">Network.Interface.I0.dot1x.Enabled, null when not reported.</param>
/// <param name="UpnpFriendlyName">Network.UPnP.FriendlyName, null when not reported.</param>
public sealed record NetworkInfo(bool? DhcpEnabled, bool? HttpsEnabled, bool? Dot1xEnabled, string? UpnpFriendlyName);

/// <summary>A picture size in pixels, "WxH" in VAPIX.</summary>
public readonly record struct ImageSize(int Width, int Height)
{
    public double Aspect => Height == 0 ? 0 : (double)Width / Height;

    public static bool TryParse(string? text, out ImageSize size)
    {
        size = default;
        var parts = (text ?? string.Empty).Split('x', 'X');
        if (parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var h)
            && w > 0 && h > 0)
        {
            size = new ImageSize(w, h);
            return true;
        }

        return false;
    }

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Width}x{Height}");
}

/// <summary>
/// One streamable video source: a view area, a sensor of a multisensor camera, or a channel of a
/// video encoder. VAPIX addresses it as <c>camera=N</c> (1-based) and configures it as <c>Image.I(N-1)</c>.
/// </summary>
/// <param name="Camera">1-based VAPIX camera number (media.amp/video.cgi <c>camera=</c>).</param>
/// <param name="Name">Label from Image.I&lt;n&gt;.Name, e.g. "View Area 1" or "Camera 2".</param>
/// <param name="Sensor">0-based image source (Image.I&lt;n&gt;.Source): the sensor or input it shows.</param>
/// <param name="Resolutions">Resolutions of this source (Properties.Image.I&lt;n&gt;.Resolution, else the device list).</param>
public sealed record VideoSourceInfo(int Camera, string Name, int Sensor, IReadOnlyList<ImageSize> Resolutions);

/// <summary>Video capabilities from param.cgi <c>Properties.Image</c> and <c>Image</c>.</summary>
/// <param name="Formats">Lower-case format names, e.g. jpeg, mjpeg, h264, h265, av1.</param>
/// <param name="Resolutions">Supported resolutions, largest first as the device lists them.</param>
/// <param name="Sources">Enabled video sources (view areas, sensors, channels), in camera order; never empty.</param>
public sealed record ImageCapabilities(IReadOnlyList<string> Formats, IReadOnlyList<ImageSize> Resolutions, IReadOnlyList<VideoSourceInfo> Sources);

/// <summary>Result of systemready.cgi (anonymous).</summary>
/// <param name="SystemReady">Device finished booting.</param>
/// <param name="NeedSetup">True when no admin user exists yet (factory default), see <c>needsetup</c>.</param>
/// <param name="PassphrasePolicy">"none", "length" or "complex"; null on firmware that does not report it.</param>
/// <param name="UptimeSeconds">Uptime in seconds, when reported.</param>
/// <param name="BootId">Changes on every boot; useful to detect a completed restart.</param>
public sealed record SystemReadyInfo(bool SystemReady, bool? NeedSetup, string? PassphrasePolicy, long? UptimeSeconds, string? BootId);

/// <summary>Outcome of an anonymous probe against one address.</summary>
/// <param name="Address">Host or IP that was probed.</param>
/// <param name="Scheme">"https" or "http", whichever answered.</param>
/// <param name="Serial">Serial number (MAC, upper hex, no separators).</param>
/// <param name="Model">ProdNbr when the device reveals it anonymously.</param>
/// <param name="FirmwareVersion">Version when the device reveals it anonymously.</param>
/// <param name="IsFactoryDefault">True when the device has no admin password yet (systemready needsetup).</param>
/// <param name="AuthenticationRequired">True when getAllProperties answered 401.</param>
/// <param name="CertificateFingerprint">SHA-256 of the TLS certificate, null for HTTP.</param>
/// <param name="Status">Status derived from the probe (Ok, CredentialsRequired, PasswordNotSet).</param>
/// <param name="ProductType">ProdType ("Dome Camera", "Network Speaker", ...) when revealed anonymously.</param>
public sealed record VapixProbeResult(
    string Address,
    string Scheme,
    string Serial,
    string? Model,
    string? FirmwareVersion,
    bool IsFactoryDefault,
    bool AuthenticationRequired,
    string? CertificateFingerprint,
    DeviceStatus Status,
    string? ProductType = null)
{
    public Uri BaseAddress => VapixClient.BuildBaseAddress(Scheme, Address);
}
