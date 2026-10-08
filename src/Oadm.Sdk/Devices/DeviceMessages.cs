using System.Globalization;

namespace Oadm.Sdk.Devices;

/// <summary>
/// The shared user texts for device problems, so the host and every plugin say the same thing: HTTP errors, timeouts,
/// unreachable devices, a changed certificate and a removed device. Short texts, no trailing period except
/// <see cref="Removed"/> and <see cref="CertificateChanged"/> (full sentences).
/// </summary>
public static class DeviceMessages
{
    /// <summary>A device that is no longer in the device table.</summary>
    public const string Removed = "The device was removed from OADM.";

    /// <summary>
    /// The device presents another certificate than the one OADM pinned. OADM has no accept action: removing the device
    /// and adding it again pins the new certificate.
    /// </summary>
    public const string CertificateChanged = "Certificate changed. Remove the device and add it again to trust the new certificate.";

    /// <summary>HTTP 401.</summary>
    public const string Unauthorized = "Unauthorized - HTTP 401 (check the credentials)";

    /// <summary>HTTP 403.</summary>
    public const string Forbidden = "Forbidden - HTTP 403 (administrator rights are required)";

    /// <summary>HTTP 404.</summary>
    public const string NotFound = "Not Found - HTTP 404 (API not available on this firmware)";

    /// <summary>HTTP 400.</summary>
    public const string BadRequest = "Bad Request - HTTP 400";

    /// <summary>A timeout whose length is not known to the caller.</summary>
    public const string TimedOut = "Timeout - the device did not answer in time";

    /// <summary>No route to the device (host or network unreachable).</summary>
    public const string HostUnreachable = "Unreachable - no route to the device";

    /// <summary>"Server error - HTTP 500".</summary>
    public static string ServerError(int status) => string.Create(CultureInfo.InvariantCulture, $"Server error - HTTP {status}");

    /// <summary>The text of an HTTP error status (400, 401, 403, 404, 5xx), or null for other codes.</summary>
    public static string? ForHttpStatus(int status) => status switch
    {
        400 => BadRequest,
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        >= 500 and <= 599 => ServerError(status),
        _ => null,
    };

    /// <summary>"Timeout after 15 s".</summary>
    public static string Timeout(TimeSpan timeout) =>
        string.Create(CultureInfo.InvariantCulture, $"Timeout after {timeout.TotalSeconds:0.#} s");

    /// <summary>"Unreachable - Connection refused" (a trailing period of the detail is removed).</summary>
    public static string Unreachable(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return "Unreachable - " + detail.Trim().TrimEnd('.');
    }
}
