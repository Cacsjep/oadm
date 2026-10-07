using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DateAndTime.Vapix;

/// <summary>Reads and writes time settings through <see cref="IVapixClient.SendAsync"/> and <see cref="IVapixClient.ListParametersAsync"/>.</summary>
public static class TimeClient
{
    /// <summary>time-service getDateTimeInfo (one request) with the server time of the answer.</summary>
    public static async Task<CurrentTimeSettings> ReadDateTimeInfoAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CurrentTimeSettings into, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        var api = apis.Require(TimeApis.TimeService, TimeApis.TimeServiceBase);
        var json = await SendForStringAsync(vapix, JsonMethodRequest.Time(api.Version, "getDateTimeInfo"), ct).ConfigureAwait(false);
        return TimeParsers.ParseDateTimeInfo(json, into) with { ServerUtc = time.GetUtcNow() };
    }

    /// <summary>ntp getNTPInfo (one request).</summary>
    public static async Task<CurrentTimeSettings> ReadNtpInfoAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CurrentTimeSettings into, CancellationToken ct)
    {
        var api = apis.Require(TimeApis.Ntp, TimeApis.NtpBase);
        var json = await SendForStringAsync(vapix, JsonMethodRequest.Ntp(api.Version, "getNTPInfo"), ct).ConfigureAwait(false);
        var parsed = TimeParsers.ParseNtpInfo(json, into);
        return parsed with { SupportsNts = TimeApis.SupportsNts(apis) && parsed.NtsEnabled is not null };
    }

    /// <summary>param.cgi list Time (one request) for devices without time-service or ntp.</summary>
    public static async Task<CurrentTimeSettings> ReadParametersAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CurrentTimeSettings into, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        apis.Require(TimeApis.ParamCgi, TimeApis.ParamCgiBase);
        var parameters = await vapix.ListParametersAsync(TimeParsers.TimeGroups, ct).ConfigureAwait(false);
        return TimeParsers.ParseParameters(parameters, into);
    }

    /// <summary>Everything the dialog shows (read-only): getDateTimeInfo + getNTPInfo, or param.cgi Time, as available.</summary>
    public static async Task<CurrentTimeSettings> ReadAllAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(apis);
        var current = new CurrentTimeSettings { Source = TimeApis.Describe(apis) };
        if (!TimeApis.HasTimeService(apis) || !TimeApis.HasNtpApi(apis))
        {
            current = await ReadParametersAsync(vapix, apis, current, ct).ConfigureAwait(false);
        }

        if (TimeApis.HasTimeService(apis))
        {
            current = await ReadDateTimeInfoAsync(vapix, apis, current, time, ct).ConfigureAwait(false);
        }

        if (TimeApis.HasNtpApi(apis))
        {
            current = await ReadNtpInfoAsync(vapix, apis, current, ct).ConfigureAwait(false);
        }

        return current;
    }

    /// <summary>Sends one write request; throws <see cref="TimeApiException"/> when the device rejects it.</summary>
    public static async Task SendAsync(IVapixClient vapix, TimeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = await SendForStringAsync(vapix, request, ct).ConfigureAwait(false);
        switch (request)
        {
            case JsonMethodRequest json:
                using (var doc = ParseJson(text, json.Method))
                {
                    ThrowIfError(doc.RootElement, json.Method);
                }

                break;
            case ParamUpdateRequest:
                var trimmed = text.Trim();
                if (!trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TimeApiException(request.Name, null, FirstLine(trimmed));
                }

                break;
        }
    }

    /// <summary>Throws <see cref="TimeApiException"/> when a time.cgi / ntp.cgi answer carries an error object.</summary>
    public static void ThrowIfError(JsonElement root, string method)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : null;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new TimeApiException(method, code, message);
        }
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

    /// <summary>Readable text for a transport failure ("Timeout after 15 s", "Connection refused").</summary>
    public static string DescribeTransport(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is TaskCanceledException or TimeoutException)
        {
            return "Timeout: the device did not answer";
        }

        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "Connection refused",
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable => "Host unreachable",
                    SocketError.TimedOut => "Timeout: the device did not answer",
                    _ => "Unreachable - " + socket.Message,
                };
            }
        }

        return ex.Message;
    }

    private static JsonDocument ParseJson(string text, string method)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new TimeApiException(method, null, "The device sent an unreadable answer: " + FirstLine(text), ex);
        }
    }

    private static async Task<string> SendForStringAsync(IVapixClient vapix, TimeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Path, UriKind.Relative))
        {
            Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType),
        };
        HttpResponseMessage response;
        try
        {
            response = await vapix.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TimeApiException(request.Name, null, DescribeTransport(ex), ex);
        }

        using (response)
        {
            var text = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = DescribeHttp(response.StatusCode);
                var first = FirstLine(text.Trim());
                throw new TimeApiException(request.Name, (int)response.StatusCode,
                    first.Length > 0 && !first.StartsWith('<') ? $"{detail}: {first}" : detail);
            }

            return text;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}

/// <summary>The device answered a time request with an error, or did not answer.</summary>
public sealed class TimeApiException : Exception
{
    public TimeApiException(string method, int? code, string? deviceMessage, Exception? inner = null)
        : base(Describe(method, code, deviceMessage), inner)
    {
        Method = method;
        Code = code;
        DeviceMessage = deviceMessage;
    }

    public TimeApiException()
    {
        Method = string.Empty;
    }

    public TimeApiException(string message)
        : base(message)
    {
        Method = string.Empty;
    }

    public TimeApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Method = string.Empty;
    }

    public string Method { get; }

    public int? Code { get; }

    public string? DeviceMessage { get; }

    private static string Describe(string method, int? code, string? message)
    {
        var sb = new StringBuilder(method).Append(" failed");
        if (code is { } c && (message is null || !message.Contains(c.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({c})");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            sb.Append(": ").Append(message);
        }

        return sb.ToString();
    }
}
