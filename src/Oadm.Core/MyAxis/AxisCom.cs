using System.Net;
using System.Text.RegularExpressions;

namespace Oadm.Core.MyAxis;

/// <summary>The only addresses the MyAxis session is ever sent to (docs/spec/myaxis.md rule 4).</summary>
public static class AxisDownloadUrls
{
    public const string Host = "www.axis.com";

    /// <summary>The two download folders: AXIS OS of cameras and of access control.</summary>
    public static readonly IReadOnlyList<string> Folders = ["/ftp/pub_soft/MPQT/", "/ftp/pub_soft/PACS/"];

    /// <summary>The MPQT listing the sign-in check starts from.</summary>
    public static readonly Uri MpqtListing = new("https://www.axis.com/ftp/pub_soft/MPQT/");

    /// <summary>
    /// True for an https URL on www.axis.com (port 443, no user info, query or fragment) below one of <see cref="Folders"/>,
    /// without any <c>..</c> or encoded dot or slash.
    /// </summary>
    public static bool IsAllowed(Uri? uri) => Check(uri, folderItself: false);

    /// <summary><see cref="IsAllowed"/> or one of the two folders itself (its listing).</summary>
    internal static bool IsAllowedOrFolder(Uri? uri) => Check(uri, folderItself: true);

    private static bool Check(Uri? uri, bool folderItself)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        var original = uri.OriginalString;
        if (original.Contains("..", StringComparison.Ordinal) || original.Contains('\\', StringComparison.Ordinal)
            || original.Contains("%2e", StringComparison.OrdinalIgnoreCase) || original.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || original.Contains("%5c", StringComparison.OrdinalIgnoreCase) || original.Contains('?', StringComparison.Ordinal)
            || original.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return Folders.Any(f => path.StartsWith(f, StringComparison.Ordinal) && (folderItself || path.Length > f.Length));
    }
}

/// <summary>How axis.com answered a request with a session.</summary>
public enum AxisAnswer
{
    /// <summary>2xx.</summary>
    Ok,

    /// <summary>401, 403 or a redirect to a login page: the session is not (or no longer) valid.</summary>
    SignInNeeded,

    /// <summary>Anything else (the reason says what).</summary>
    Failed,
}

/// <summary>
/// The HTTP calls to www.axis.com with a user's MyAxis session (docs/spec/myaxis.md). The handler keeps no cookies and follows
/// no redirect (the server's <c>AxisComHandler</c>; tests give a fake one); the session goes only as the one <c>Cookie</c>
/// header of requests below <see cref="AxisDownloadUrls.Folders"/>. Never logs a header.
/// </summary>
public sealed partial class AxisComClient : IDisposable
{
    /// <summary>The time axis.com has for the headers of an answer and between two reads of a body.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private const int MaxListingBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    /// <param name="handler">Must have <c>UseCookies = false</c> and <c>AllowAutoRedirect = false</c>.</param>
    public AxisComClient(HttpMessageHandler handler, TimeProvider time, string? userAgent = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
    }

    internal TimeProvider Time => _time;

    /// <summary>
    /// The sign-in check: the headers of the <c>latest/</c> .bin of the first product in the MPQT listing, with
    /// <paramref name="session"/>. Throws <see cref="HttpRequestException"/> or <see cref="TimeoutException"/> when axis.com
    /// cannot be reached.
    /// </summary>
    public async Task<AxisAnswer> CheckAsync(string session, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(session);
        var (answer, products) = await ListAsync(AxisDownloadUrls.MpqtListing, session, ct).ConfigureAwait(false);
        if (answer != AxisAnswer.Ok)
        {
            return answer;
        }

        var product = products.FirstOrDefault(u => u.AbsolutePath.EndsWith('/'))
            ?? throw new HttpRequestException("The MPQT listing of axis.com has no product.");
        var (latestAnswer, files) = await ListAsync(new Uri(product, "latest/"), session, ct).ConfigureAwait(false);
        if (latestAnswer != AxisAnswer.Ok)
        {
            return latestAnswer;
        }

        var bin = files.FirstOrDefault(u => u.AbsolutePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            ?? throw new HttpRequestException("The first product on axis.com has no firmware file in latest/.");
        using var request = Request(HttpMethod.Head, bin, session);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        return Classify(response);
    }

    /// <summary>
    /// GET of <paramref name="uri"/> (allowed by <see cref="AxisDownloadUrls.IsAllowed"/>) with the session; the answer after
    /// its headers. The caller disposes it and reads the body with <see cref="ReadWithIdleTimeoutAsync"/>.
    /// </summary>
    internal async Task<HttpResponseMessage> GetAsync(Uri uri, string session, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, uri, session);
        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>One read of a body; <see cref="TimeoutException"/> when nothing came for <see cref="IdleTimeout"/>.</summary>
    internal async Task<int> ReadWithIdleTimeoutAsync(Stream body, Memory<byte> buffer, CancellationToken ct)
    {
        using var idle = new CancellationTokenSource(IdleTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        try
        {
            return await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("axis.com did not answer in time.");
        }
    }

    /// <summary>How axis.com answered: 2xx, a sign-in needed (401, 403, any redirect to a login page) or failed.</summary>
    public static AxisAnswer Classify(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var code = (int)response.StatusCode;
        if (code is >= 200 and < 300)
        {
            return AxisAnswer.Ok;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return AxisAnswer.SignInNeeded;
        }

        return code is >= 300 and < 400 && IsLoginPage(response.Headers.Location) ? AxisAnswer.SignInNeeded : AxisAnswer.Failed;
    }

    /// <summary>A redirect target that is a login page (a path with /login, a host login.*, *.login.* or auth.*).</summary>
    public static bool IsLoginPage(Uri? location)
    {
        if (location is null)
        {
            return false;
        }

        var absolute = location.IsAbsoluteUri ? location : new Uri(AxisDownloadUrls.MpqtListing, location);
        var host = absolute.Host;
        return absolute.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("login.", StringComparison.OrdinalIgnoreCase)
            || host.Contains(".login.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("auth.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A short reason for the user ("axis.com answered 404").</summary>
    public static string Reason(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return "axis.com answered " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(AxisAnswer Answer, IReadOnlyList<Uri> Links)> ListAsync(Uri listing, string session, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, listing, session);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var answer = Classify(response);
        if (answer == AxisAnswer.SignInNeeded)
        {
            return (answer, []);
        }

        if (answer == AxisAnswer.Failed)
        {
            throw new HttpRequestException(Reason(response));
        }

        var body = response.Content.Headers.ContentLength is > MaxListingBytes
            ? throw new HttpRequestException("The listing of axis.com is too large.")
            : await ReadListingAsync(response, ct).ConfigureAwait(false);
        var links = new List<Uri>();
        foreach (Match match in Href().Matches(body))
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (href.Length == 0 || href.StartsWith('?') || href.StartsWith('#') || href.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            if (Uri.TryCreate(listing, href, out var link) && AxisDownloadUrls.IsAllowed(link)
                && link.AbsolutePath.StartsWith(listing.AbsolutePath, StringComparison.Ordinal)
                && link.AbsolutePath.Length > listing.AbsolutePath.Length)
            {
                links.Add(link);
            }
        }

        return (answer, links);
    }

    private async Task<string> ReadListingAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await ReadWithIdleTimeoutAsync(body, chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxListingBytes)
                {
                    throw new HttpRequestException("The listing of axis.com is too large.");
                }

                buffer.Write(chunk, 0, read);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string session)
    {
        if (!AxisDownloadUrls.IsAllowedOrFolder(uri))
        {
            throw new ArgumentException("Only files below the download folders of www.axis.com can be fetched.", nameof(uri));
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Cookie", session);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var idle = new CancellationTokenSource(IdleTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("axis.com did not answer in time.");
        }
    }

    public void Dispose() => _http.Dispose();

    public override string ToString() => nameof(AxisComClient);

    [GeneratedRegex("""href\s*=\s*["']([^"'<>]*)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Href();
}
