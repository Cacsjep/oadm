using System.Globalization;
using System.Net;
using System.Text;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>A device answered with an error (REST error object, SOAP fault, network_settings.cgi error). Message = user text.</summary>
public sealed class PkiDeviceException : Exception
{
    public PkiDeviceException()
    {
    }

    public PkiDeviceException(string message)
        : base(message)
    {
    }

    public PkiDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>HTTP status of the answer (0 = not known).</summary>
    public int HttpStatus { get; init; }
}

/// <summary>One device answer as text.</summary>
public sealed record DeviceAnswer(HttpStatusCode Status, string Body, DateTimeOffset? Date);

/// <summary>Sends requests through <see cref="IVapixClient.SendAsync"/> and reads the answer as UTF-8 text.</summary>
public static class DeviceHttp
{
    public const string JsonType = "application/json";
    public const string SoapType = "application/soap+xml";

    public static async Task<DeviceAnswer> SendAsync(IVapixClient vapix, HttpMethod method, string path, string? contentType, string? body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? JsonType);
        }

        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new DeviceAnswer(response.StatusCode, Encoding.UTF8.GetString(bytes), response.Headers.Date);
    }

    /// <summary>Readable text for HTTP status codes (device answered, but not with 2xx).</summary>
    public static string DescribeHttp(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Unauthorized - HTTP 401 (check the credentials)",
        HttpStatusCode.Forbidden => "Forbidden - HTTP 403 (administrator rights are required)",
        HttpStatusCode.NotFound => "Not Found - HTTP 404 (API not available on this firmware)",
        HttpStatusCode.BadRequest => "Bad Request - HTTP 400",
        HttpStatusCode.InternalServerError => "Server error - HTTP 500",
        HttpStatusCode.ServiceUnavailable => "Service unavailable - HTTP 503",
        _ => string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)status}"),
    };

    public static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Trim().Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}
