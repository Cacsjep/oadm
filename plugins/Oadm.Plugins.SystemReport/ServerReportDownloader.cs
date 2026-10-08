using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SystemReport;

/// <summary>Result of one device: the report's size and the mode it was taken with, or the error text.</summary>
public sealed record DownloadResult(long Bytes, string Mode, string? Error)
{
    public bool IsOk => Error is null;
}

/// <summary>
/// The VAPIX server report (<c>GET /axis-cgi/serverreport.cgi?mode=...</c>, read-only, administrator account) and the
/// mapping of every failure to a short readable text. Verified on AXIS P3265-V (AXIS OS 12.11): <c>zip_with_image</c>
/// answers <c>application/zip</c> (chunked, about 0.3 MB in 37 s: <c>serverreport_cgi.txt</c> + <c>serverreport_image.jpg</c>),
/// <c>zip</c> the same without the picture (about 0.16 MB in 31 s).
/// </summary>
public static partial class ServerReportRequests
{
    public const string ReportPath = "axis-cgi/serverreport.cgi";

    /// <summary>Report and a snapshot in one ZIP, what Axis support asks for; only on video products with application support.</summary>
    public const string ModeZipWithImage = "zip_with_image";

    /// <summary>Report (product information, parameters, system logs) as ZIP; every device.</summary>
    public const string ModeZip = "zip";

    /// <summary>Relative request URI of a mode.</summary>
    public static string BuildUri(string mode) => ReportPath + "?mode=" + Uri.EscapeDataString(mode);

    /// <summary>The report request: streamed answer (written to disk, not buffered) with its own timeout until the headers.</summary>
    public static HttpRequestMessage Build(string mode, TimeSpan timeout)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BuildUri(mode), UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Options.Set(VapixRequestOptions.Timeout, timeout);
        request.Options.Set(VapixRequestOptions.StreamResponse, true);
        return request;
    }

    /// <summary>Modes to try in order: with the picture first for video devices, then the plain report.</summary>
    public static IReadOnlyList<string> ModesFor(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.HasVideo ? [ModeZipWithImage, ModeZip] : [ModeZip];
    }

    /// <summary>A ZIP archive starts with a local file header (PK 03 04) or, when empty, the end record (PK 05 06).</summary>
    public static bool IsZip(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'P' && data[1] == (byte)'K' && ((data[2] == 3 && data[3] == 4) || (data[2] == 5 && data[3] == 6));

    /// <summary>
    /// Text for a device status that makes a request pointless (nothing is sent); null when the device may answer.
    /// The same texts as the snapshot report and the Metadata Monitor.
    /// </summary>
    public static string? StatusError(DeviceStatus status) => status switch
    {
        DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
        DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        _ => null,
    };

    /// <summary>True when a failed <see cref="ModeZipWithImage"/> is worth a second try with <see cref="ModeZip"/>.</summary>
    public static bool TryPlainReport(HttpStatusCode status) =>
        status is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.InternalServerError or HttpStatusCode.NotImplemented;

    /// <summary>
    /// Text for an answer that is not a report: "Unauthorized - HTTP 401", "Forbidden - HTTP 403 ...", or the text the
    /// device sent ("Error: ...") with the code.
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
                return "No server report API (serverreport.cgi) - HTTP 404";
            case HttpStatusCode.OK:
                return string.IsNullOrEmpty(text) ? "The device did not send a ZIP file" : "The device did not send a ZIP file: " + text;
        }

        if (string.IsNullOrEmpty(text))
        {
            text = "Request failed";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{text} - HTTP {code}");
    }

    /// <summary>Text for an exception of the request: timeout, connection failure, or the message.</summary>
    public static string ExceptionError(Exception ex, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is ReportTooLargeException)
        {
            return ex.Message;
        }

        if (Find<TimeoutException>(ex) is not null || ex is OperationCanceledException)
        {
            return DeviceMessages.Timeout(timeout);
        }

        if (ex is KeyNotFoundException)
        {
            return DeviceMessages.Removed;
        }

        if (ex is IOException && Find<HttpRequestException>(ex) is null && Find<SocketException>(ex) is null)
        {
            return "The report could not be saved on the server: " + ex.Message;
        }

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
            text = LeadingCodeRegex().Replace(text.Trim(), string.Empty);
        }

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        line = WhitespaceRegex().Replace(line, " ").Trim();
        return line.Length > 160 ? line[..160] + "..." : line;
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

/// <summary>A report larger than <see cref="SystemReportPluginInfo.MaxReportBytes"/>.</summary>
public sealed class ReportTooLargeException(long limit)
    : IOException(string.Create(CultureInfo.InvariantCulture, $"The report is larger than {limit / (1024 * 1024)} MB and was not saved."))
{
}

/// <summary>
/// Downloads the server report of one device with the stored credentials straight into a file (streamed, never held in
/// memory, so the 16 MB limit of buffered VAPIX answers does not apply; <see cref="MaxBytes"/> bounds it instead).
/// Never throws for device problems: they end up in <see cref="DownloadResult.Error"/>.
/// </summary>
public sealed class ServerReportDownloader
{
    private const int ErrorBodyBytes = 64 * 1024;

    private readonly IVapixClientFactory _vapix;

    public ServerReportDownloader(IVapixClientFactory vapix, TimeSpan? timeout = null, long maxBytes = SystemReportPluginInfo.MaxReportBytes)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        _vapix = vapix;
        Timeout = timeout ?? SystemReportPluginInfo.DeviceTimeout;
        MaxBytes = maxBytes;
    }

    /// <summary>Time one device may take, all modes together.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Largest report that is kept.</summary>
    public long MaxBytes { get; }

    /// <summary>Writes the device's report to <paramref name="path"/>; on failure the file does not exist.</summary>
    public async Task<DownloadResult> DownloadAsync(IDeviceInfo device, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var modes = ServerReportRequests.ModesFor(device);
        if (ServerReportRequests.StatusError(device.Status) is { } refused)
        {
            return new DownloadResult(0, modes[0], refused);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var mode = modes[0];
        try
        {
            var client = await _vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
            string? error = null;
            foreach (var m in modes)
            {
                mode = m;
                var (bytes, failure, tryNext) = await TryModeAsync(client, m, path, timeout.Token).ConfigureAwait(false);
                if (failure is null)
                {
                    return new DownloadResult(bytes, m, null);
                }

                error = failure;
                if (!tryNext)
                {
                    break;
                }
            }

            return new DownloadResult(0, mode, error);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            TryDelete(path);
            return new DownloadResult(0, mode, ServerReportRequests.ExceptionError(ex, Timeout));
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private async Task<(long Bytes, string? Error, bool TryNext)> TryModeAsync(IVapixClient client, string mode, string path, CancellationToken ct)
    {
        using var request = ServerReportRequests.Build(mode, Timeout);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var withImage = mode == ServerReportRequests.ModeZipWithImage;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var text = await ReadTextAsync(response.Content, ct).ConfigureAwait(false);
            return (0, ServerReportRequests.HttpError(response.StatusCode, contentType, text), withImage && ServerReportRequests.TryPlainReport(response.StatusCode));
        }

        var bytes = await CopyToFileAsync(response.Content, path, ct).ConfigureAwait(false);
        var head = new byte[4];
        int read;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
        {
            read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        }

        if (ServerReportRequests.IsZip(head.AsSpan(0, read)))
        {
            return (bytes, null, false);
        }

        var body = await ReadFileTextAsync(path, ct).ConfigureAwait(false);
        TryDelete(path);
        return (0, ServerReportRequests.HttpError(HttpStatusCode.OK, contentType, body), withImage);
    }

    private async Task<long> CopyToFileAsync(HttpContent content, string path, CancellationToken ct)
    {
        await using var body = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                throw new ReportTooLargeException(MaxBytes);
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return total;
    }

    private static async Task<string> ReadTextAsync(HttpContent content, CancellationToken ct)
    {
        await using var body = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[ErrorBodyBytes];
        var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async Task<string> ReadFileTextAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var buffer = new byte[ErrorBodyBytes];
        var read = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
