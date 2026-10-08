using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Sdk.Vapix;

namespace Oadm.Core.Vapix;

/// <summary>Connection settings for one device.</summary>
public sealed record VapixConnectionOptions
{
    /// <summary>Host name or IP address, optionally with port ("10.0.0.48", "cam.local:8443", "fe80::1").</summary>
    public required string Address { get; init; }

    /// <summary>"https" (default) or "http".</summary>
    public string Scheme { get; init; } = Uri.UriSchemeHttps;

    /// <summary>Credentials, or null for anonymous calls (probe, first password).</summary>
    public NetworkCredential? Credentials { get; init; }

    /// <summary>Pinned SHA-256 certificate fingerprint, or null to trust on first use.</summary>
    public string? PinnedCertificateFingerprint { get; init; }

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Extra trust anchors for rating the device certificate (<see cref="VapixClient.ObservedCertificate"/>); null = OS store only.</summary>
    public TrustAnchorRegistry? TrustAnchors { get; init; }

    /// <summary>
    /// Also offer Basic authentication over plain HTTP (Digest stays preferred when the device offers it).
    /// Only the add page's automatic login sets this, and only for a device that passed the anonymous
    /// Axis check (user decision, see "Production hardening" in CLAUDE.md); everything else keeps Basic HTTPS only.
    /// </summary>
    public bool AllowBasicOverHttp { get; init; }
}

/// <summary>
/// VAPIX access to one Axis device: one method per endpoint. Owns its <see cref="HttpClient"/>,
/// so dispose it when done. Thread-safe for concurrent calls.
/// </summary>
/// <remarks>
/// Authentication: AXIS OS 12 offers Digest on HTTP and Basic on HTTPS by default
/// (Network.HTTP.AuthenticationPolicy=recommended). The credential cache therefore offers
/// Digest only for http:// and Basic or Digest for https://, so Basic never travels in clear text,
/// except for the add page's login to a verified Axis device (<see cref="VapixConnectionOptions.AllowBasicOverHttp"/>).
/// </remarks>
public sealed class VapixClient : IVapixClient, IDisposable
{
    /// <summary>Parameters read by <see cref="GetNetworkInfoAsync"/>.</summary>
    public static readonly IReadOnlyList<string> NetworkInfoParameters =
    [
        "Network.BootProto",
        "Network.UPnP.FriendlyName",
        "Network.Interface.I0.dot1x.Enabled",
        "HTTPS.Enabled",
    ];

    internal const string BasicDeviceInfoPath = "axis-cgi/basicdeviceinfo.cgi";
    internal const string ParamPath = "axis-cgi/param.cgi";
    internal const string SystemReadyPath = "axis-cgi/systemready.cgi";
    internal const string PwdgrpPath = "axis-cgi/pwdgrp.cgi";
    internal const string ApiDiscoveryPath = "axis-cgi/apidiscovery.cgi";
    internal const string RestartPath = "axis-cgi/restart.cgi";

    /// <summary>Largest answer accepted from a device (16 MB); uploads are limited on the request side only.</summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    private const string JsonMediaType = "application/json";

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a client over a custom handler (tests, or a handler built by <see cref="CreateHandler"/>).</summary>
    public VapixClient(Uri baseAddress, HttpMessageHandler handler, CertificatePinning? pinning = null, TimeSpan? timeout = null, bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(handler);
        BaseAddress = baseAddress;
        Pinning = pinning;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);

        // The timeout is applied per request in SendAsync so plugins can extend it (VapixRequestOptions.Timeout).
        _http = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = baseAddress,
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    /// <summary>Default per-request timeout; one request can override it with <see cref="VapixRequestOptions.Timeout"/>.</summary>
    public TimeSpan Timeout => _timeout;

    public Uri BaseAddress { get; }

    /// <summary>Certificate pinning state; null for HTTP or custom handlers.</summary>
    public CertificatePinning? Pinning { get; }

    /// <summary>SHA-256 fingerprint of the certificate the device presented last (HTTPS only).</summary>
    public string? CertificateFingerprint => Pinning?.ObservedFingerprint;

    /// <summary>
    /// Subject, issuer, validity and chain trust of the certificate from the last TLS handshake
    /// (HTTPS only, null before the first handshake). Pooled connections are reused, so this
    /// reflects the most recent new connection.
    /// </summary>
    public CertificateInfo? ObservedCertificate => Pinning?.ObservedCertificate;

    /// <summary>Creates a client with a real <see cref="HttpClientHandler"/> for the given options.</summary>
    public static VapixClient Create(VapixConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseAddress = BuildBaseAddress(options.Scheme, options.Address);
        var pinning = new CertificatePinning(options.PinnedCertificateFingerprint) { TrustAnchors = options.TrustAnchors };
        var handler = CreateHandler(baseAddress, options.Credentials, pinning, options.AllowBasicOverHttp);
        return new VapixClient(baseAddress, handler, pinning, options.Timeout);
    }

    /// <summary>
    /// Builds the HTTP handler: Digest for http (plus Basic when <paramref name="allowBasicOverHttp"/>), Basic or
    /// Digest for https, TOFU certificate pinning, no redirects, no cookies. With both schemes cached the handler
    /// answers the strongest challenge the device offers (Digest before Basic).
    /// </summary>
    public static HttpClientHandler CreateHandler(Uri baseAddress, NetworkCredential? credentials, CertificatePinning pinning, bool allowBasicOverHttp = false)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(pinning);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PreAuthenticate = true,
            ServerCertificateCustomValidationCallback = pinning.ValidateCallback,
        };

        if (credentials is not null)
        {
            var cache = new CredentialCache();
            var prefix = new Uri(baseAddress.GetLeftPart(UriPartial.Authority) + "/");
            cache.Add(prefix, "Digest", credentials);
            if (baseAddress.Scheme == Uri.UriSchemeHttps || allowBasicOverHttp)
            {
                cache.Add(prefix, "Basic", credentials);
            }

            handler.Credentials = cache;
        }

        return handler;
    }

    /// <summary>Builds "scheme://host[:port]/" and brackets bare IPv6 addresses.</summary>
    public static Uri BuildBaseAddress(string scheme, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (scheme != Uri.UriSchemeHttps && scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException($"Unsupported scheme '{scheme}'.", nameof(scheme));
        }

        var host = address.Trim();
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 && !host.StartsWith('['))
        {
            host = $"[{ip}]";
        }

        return new Uri($"{scheme}://{host}/", UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        var json = await PostJsonAsync(ApiDiscoveryPath, """{"apiVersion":"1.0","method":"getApiList"}""", ct).ConfigureAwait(false);
        return VapixParsers.ParseApiList(json);
    }

    public async Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
    {
        var json = await PostJsonAsync(BasicDeviceInfoPath, """{"apiVersion":"1.0","method":"getAllProperties"}""", ct).ConfigureAwait(false);
        return VapixParsers.ParseBasicDeviceInfo(json);
    }

    /// <summary>basicdeviceinfo getAllUnrestrictedProperties: works anonymously, no SocSerialNumber/Architecture.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetUnrestrictedPropertiesAsync(CancellationToken ct)
    {
        var json = await PostJsonAsync(BasicDeviceInfoPath, """{"apiVersion":"1.0","method":"getAllUnrestrictedProperties"}""", ct).ConfigureAwait(false);
        return VapixParsers.ParseBasicDeviceInfoProperties(json);
    }

    /// <inheritdoc />
    /// <remarks>Keys come back without the "root." prefix. Unknown groups are silently skipped.</remarks>
    public async Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var list = string.Join(',', groups.Select(Uri.EscapeDataString));
        if (list.Length == 0)
        {
            throw new ArgumentException("At least one parameter group is required.", nameof(groups));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ParamPath}?action=list&group={list}");
        var text = await SendForStringAsync(request, ct).ConfigureAwait(false);
        return VapixParsers.ParseParameterList(text);
    }

    /// <summary>DHCP, HTTPS, 802.1X and UPnP friendly name via param.cgi (verified on AXIS OS 12.11).</summary>
    public async Task<NetworkInfo> GetNetworkInfoAsync(CancellationToken ct)
    {
        var parameters = await ListParametersAsync(NetworkInfoParameters, ct).ConfigureAwait(false);
        return VapixParsers.ParseNetworkInfo(parameters);
    }

    /// <summary>systemready.cgi (anonymous): readiness and whether the device still needs its first admin user.</summary>
    public async Task<SystemReadyInfo> GetSystemReadyAsync(CancellationToken ct)
    {
        var json = await PostJsonAsync(SystemReadyPath, """{"apiVersion":"1.0","method":"systemready","params":{"timeout":5}}""", ct).ConfigureAwait(false);
        return VapixParsers.ParseSystemReady(json);
    }

    /// <summary>
    /// Sets the first root password on a factory-default device via pwdgrp.cgi (anonymous, form
    /// body so the password never appears in a URL). Refuses to run over plain HTTP unless
    /// <paramref name="allowPlainHttp"/> is set, and refuses when systemready reports the device
    /// already has an admin user.
    /// </summary>
    public async Task SetInitialRootPasswordAsync(string password, CancellationToken ct, bool allowPlainHttp = false)
    {
        ValidatePassword(password);
        if (BaseAddress.Scheme != Uri.UriSchemeHttps && !allowPlainHttp)
        {
            throw new InvalidOperationException("Refusing to send the initial password over plain HTTP.");
        }

        var ready = await GetSystemReadyAsync(ct).ConfigureAwait(false);
        if (ready.NeedSetup == false)
        {
            throw new InvalidOperationException("The device already has an admin password.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, PwdgrpPath)
        {
            Content = new FormUrlEncodedContent(
            [
                new("action", "add"),
                new("user", "root"),
                new("pwd", password),
                new("grp", "root"),
                new("sgrp", "admin:operator:viewer:ptz"),
            ]),
        };

        var body = await SendForStringAsync(request, ct).ConfigureAwait(false);
        if (!VapixParsers.IsPwdgrpSuccess(body))
        {
            // The body never echoes the password, but keep the message short anyway.
            throw new VapixException("pwdgrp.cgi refused the initial password: " + FirstLine(body));
        }
    }

    /// <summary>Password rule from the spec: 1-64 printable ASCII characters.</summary>
    public static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 1 or > 64 || password.Any(c => c is < (char)0x20 or > (char)0x7E))
        {
            throw new ArgumentException("Password must be 1-64 printable ASCII characters.", nameof(password));
        }
    }

    /// <inheritdoc />
    public async Task RestartAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RestartPath);
        _ = await SendForStringAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Image formats, resolutions and video sources (view areas, sensors, channels) via param.cgi groups Properties.Image and Image.</summary>
    public async Task<ImageCapabilities> GetImageCapabilitiesAsync(CancellationToken ct)
    {
        var parameters = await ListParametersAsync(["Properties.Image", "Image"], ct).ConfigureAwait(false);
        return VapixParsers.ParseImageCapabilities(parameters);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Sdk.Vapix.VideoSource>> GetVideoSourcesAsync(CancellationToken ct)
    {
        var image = await GetImageCapabilitiesAsync(ct).ConfigureAwait(false);
        return [.. image.Sources.Select(s => new Sdk.Vapix.VideoSource(
            s.Camera,
            s.Name,
            s.Sensor,
            [.. s.Resolutions.Select(r => new Sdk.Vapix.VideoResolution(r.Width, r.Height))]))];
    }

    /// <inheritdoc />
    /// <remarks>
    /// Relative URIs resolve against <see cref="BaseAddress"/>. The response is returned as-is
    /// (no status check); a pinned certificate mismatch throws <see cref="CertificateChangedException"/>.
    /// The request body is streamed as given (never buffered here). The timeout is
    /// <see cref="Timeout"/> unless the request sets <see cref="VapixRequestOptions.Timeout"/>; it covers
    /// sending the body and reading the response, and surfaces like an HttpClient timeout
    /// (<see cref="TaskCanceledException"/> with an inner <see cref="TimeoutException"/>).
    /// </remarks>
    /// <exception cref="ArgumentException">The request would go to another host, port or scheme than the device's.</exception>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureSameDevice(request.RequestUri);
        var timeout = request.Options.TryGetValue(VapixRequestOptions.Timeout, out var requested) ? requested : _timeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            // Streamed answers (VapixRequestOptions.StreamResponse) are not buffered, so MaxResponseBytes does not apply:
            // the caller reads the body itself and enforces its own limit.
            var completion = request.Options.TryGetValue(VapixRequestOptions.StreamResponse, out var stream) && stream
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead;
            var response = await _http.SendAsync(request, completion, timeoutCts.Token).ConfigureAwait(false);
            NormalizeCharset(response.Content);
            return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The device did not answer within {timeout.TotalSeconds:0} seconds."),
                new TimeoutException(ex.Message, ex));
        }
        catch (HttpRequestException ex) when (IsTooLarge(ex))
        {
            throw new VapixResponseTooLargeException(ex);
        }
        catch (HttpRequestException ex) when (Pinning?.MismatchFingerprint is { } actual)
        {
            throw new CertificateChangedException(Pinning.PinnedFingerprint ?? string.Empty, actual.Length == 0 ? null : actual, ex);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>
    /// Every request goes to the device itself: an absolute URI, or a relative one that resolves to another
    /// authority ("//host/x", "/\host/x"), is refused before anything is sent.
    /// </summary>
    private void EnsureSameDevice(Uri? uri)
    {
        if (uri is null)
        {
            return;
        }

        // Like HttpClient: "/path" parsed as an implicit file URI (Unix) is a path relative to the base address.
        var relative = !uri.IsAbsoluteUri || (uri.IsFile && uri.OriginalString.StartsWith('/'));
        var target = relative ? new Uri(BaseAddress, uri.OriginalString) : uri;
        if ((relative && uri.OriginalString.Contains('\\', StringComparison.Ordinal))
            || !string.Equals(target.Scheme, BaseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.IdnHost, BaseAddress.IdnHost, StringComparison.OrdinalIgnoreCase)
            || target.Port != BaseAddress.Port
            || !string.IsNullOrEmpty(target.UserInfo))
        {
            throw new ArgumentException(
                $"Refused: the request must go to the device {BaseAddress.Authority} itself, not to {target.GetLeftPart(UriPartial.Authority)}.",
                nameof(uri));
        }
    }

    private static bool IsTooLarge(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded
        || ex.InnerException is HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded };

    private async Task<string> PostJsonAsync(string path, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, JsonMediaType),
        };
        return await SendForStringAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<string> SendForStringAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var path = request.RequestUri?.OriginalString ?? string.Empty;
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            path = path[..q];
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new VapixAuthenticationException($"{path}: HTTP {(int)response.StatusCode}", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new VapixException($"{path}: HTTP {(int)response.StatusCode}", response.StatusCode);
        }

        return await ReadStringAsync(response.Content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// AXIS OS sends <c>charset=utf8</c> (and older firmware other names .NET does not know), and
    /// <see cref="HttpContent.ReadAsStringAsync()"/> then throws "The character set provided in ContentType is invalid".
    /// Every response leaves <see cref="SendAsync"/> with a charset .NET can read: utf8 and unknown names become utf-8,
    /// so plugins can read answers as text without their own workaround.
    /// </summary>
    public static void NormalizeCharset(HttpContent? content)
    {
        var contentType = content?.Headers.ContentType;
        var charset = contentType?.CharSet?.Trim('"', ' ');
        if (contentType is null || string.IsNullOrEmpty(charset))
        {
            return;
        }

        try
        {
            _ = Encoding.GetEncoding(charset);
            if (!string.Equals(charset, contentType.CharSet, StringComparison.Ordinal))
            {
                contentType.CharSet = charset; // quoted "utf-8" is not accepted by ReadAsStringAsync either
            }
        }
        catch (ArgumentException)
        {
            contentType.CharSet = "utf-8";
        }
    }

    /// <summary>
    /// Reads a response body as text. AXIS OS answers some APIs (apidiscovery) with
    /// <c>charset=utf8</c>, which .NET does not know; anything unknown or missing is read as UTF-8.
    /// </summary>
    internal static async Task<string> ReadStringAsync(HttpContent content, CancellationToken ct)
    {
        var bytes = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(charset) && !charset.Replace("-", string.Empty, StringComparison.Ordinal).Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }

        var text = encoding.GetString(bytes);
        return text.TrimStart((char)0xFEFF);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}
