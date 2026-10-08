using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SnapshotReport;

/// <summary>
/// VAPIX snapshot request (<c>GET /axis-cgi/jpg/image.cgi?camera=N&amp;resolution=WxH</c>, JPEG API) and the
/// mapping of every failure to a short readable text for the tile and the report.
/// </summary>
public static partial class SnapshotRequests
{
    public const string ImagePath = "axis-cgi/jpg/image.cgi";

    /// <summary>Relative request URI: camera (1-based) and resolution, e.g. <c>axis-cgi/jpg/image.cgi?camera=2&amp;resolution=1280x720</c>.</summary>
    public static string BuildUri(int camera, VideoResolution resolution) =>
        string.Create(CultureInfo.InvariantCulture, $"{ImagePath}?camera={Math.Max(1, camera)}&resolution={resolution.Width}x{resolution.Height}");

    /// <summary>The snapshot request with its own timeout (overrides the default VAPIX timeout of 15 s).</summary>
    public static HttpRequestMessage Build(int camera, VideoResolution resolution, TimeSpan timeout)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BuildUri(camera, resolution), UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Options.Set(VapixRequestOptions.Timeout, timeout);
        return request;
    }

    /// <summary>Resolution to request: the largest of the source that fits the box with the sensor aspect.</summary>
    public static VideoResolution ChooseResolution(VideoSource? source, int maxWidth, int maxHeight)
    {
        maxWidth = maxWidth > 0 ? maxWidth : SnapshotReportPluginInfo.GridMaxWidth;
        maxHeight = maxHeight > 0 ? maxHeight : SnapshotReportPluginInfo.GridMaxHeight;
        return VideoResolutions.Choose(source?.Resolutions ?? [], maxWidth, maxHeight);
    }

    /// <summary>A JPEG starts with the SOI marker FF D8.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;

    /// <summary>Width and height from the first SOF marker of a JPEG; false when not found.</summary>
    public static bool TryGetJpegSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (!IsJpeg(data))
        {
            return false;
        }

        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                return false;
            }

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            var length = (data[i + 2] << 8) | data[i + 3];
            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                height = (data[i + 5] << 8) | data[i + 6];
                width = (data[i + 7] << 8) | data[i + 8];
                return width > 0 && height > 0;
            }

            i += 2 + length;
        }

        return false;
    }

    /// <summary>
    /// Text for a device status that makes a snapshot pointless (no request is sent); null when the device may answer.
    /// </summary>
    public static string? StatusError(DeviceStatus status) => status switch
    {
        DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
        DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        _ => null,
    };

    /// <summary>
    /// Text for a response that is not a JPEG: "Unauthorized - HTTP 401", "Forbidden - HTTP 403",
    /// "Bad Request - HTTP 400", or the VAPIX error text the device sent ("Error: ...").
    /// </summary>
    public static string HttpError(HttpStatusCode status, string? contentType, string? body)
    {
        var code = (int)status;
        var text = BodyText(contentType, body);
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return DeviceMessages.Unauthorized;
            case HttpStatusCode.Forbidden:
                return DeviceMessages.Forbidden;
            case HttpStatusCode.NotFound:
                return "No snapshot API (image.cgi) - HTTP 404";
            case HttpStatusCode.OK:
                return string.IsNullOrEmpty(text) ? "The device did not send a JPEG picture" : text;
        }

        if (string.IsNullOrEmpty(text))
        {
            text = ReasonText(status);
        }

        return string.Create(CultureInfo.InvariantCulture, $"{text} - HTTP {code}");
    }

    /// <summary>Text for an exception of the request: timeout, connection failure, rejected credentials or the message.</summary>
    public static string ExceptionError(Exception ex, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (IsTimeout(ex))
        {
            return DeviceMessages.Timeout(timeout);
        }

        if (ex is NotSupportedException)
        {
            return "The server cannot read video sources: " + ex.Message;
        }

        if (ex is KeyNotFoundException)
        {
            return DeviceMessages.Removed;
        }

        // VAPIX client errors carry "HTTP <code>" in the message (e.g. "param.cgi: HTTP 401").
        var http = HttpCodeRegex().Match(ex.Message);
        if (http.Success && int.TryParse(http.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            return HttpError((HttpStatusCode)code, null, null);
        }

        if (Find<SocketException>(ex) is { } socket)
        {
            return DeviceMessages.Unreachable(socket.Message);
        }

        if (Find<HttpRequestException>(ex) is { } request)
        {
            var inner = request.InnerException?.Message ?? request.Message;
            return DeviceMessages.Unreachable(inner);
        }

        return ex.Message;
    }

    private static bool IsTimeout(Exception ex) =>
        Find<TimeoutException>(ex) is not null || ex is TaskCanceledException or OperationCanceledException;

    private static T? Find<T>(Exception? ex)
        where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static string BodyText(string? contentType, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var text = body;
        if ((contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false) || body.TrimStart().StartsWith('<'))
        {
            var title = TitleRegex().Match(body);
            text = title.Success ? title.Groups[1].Value : TagRegex().Replace(body, " ");
            // "400 Bad Request, The request had bad syntax ..." -> "Bad Request"
            text = LeadingCodeRegex().Replace(text.Trim(), string.Empty);
            var comma = text.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                text = text[..comma];
            }
        }

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        line = WhitespaceRegex().Replace(line, " ").Trim();
        return line.Length > 160 ? line[..160] + "..." : line;
    }

    private static string ReasonText(HttpStatusCode status)
    {
        var name = status.ToString();
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return "Request failed";
        }

        var builder = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"HTTP (\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex HttpCodeRegex();

    [GeneratedRegex(@"<title>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\d{3}\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCodeRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
