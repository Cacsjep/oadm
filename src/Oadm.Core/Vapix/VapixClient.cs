using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Core.Sites;
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
    /// Axis check (see "Security and access" in CLAUDE.md); everything else keeps Basic HTTPS only.
    /// </summary>
    public bool AllowBasicOverHttp { get; init; }

    /// <summary>
    /// The way to the device: the server's own network (default) or a site (docs/sites.md). Built only by
    /// <see cref="DeviceConnections"/>, so every connection to a device carries its route.
    /// </summary>
    public DeviceRoute Route { get; init; }
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
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The routed VAPIX client: its SocketsHttpHandler opens every connection through DeviceRouter, ClientWebSocket.ConnectAsync goes through that handler.")]
public sealed partial class VapixClient : IVapixClient, IDisposable
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

    /// <summary>The way to the device; every timeout through a site gets the route's extra (<see cref="DeviceRouter.ExtraTimeout"/>).</summary>
    public DeviceRoute Route { get; init; }

    /// <summary>The route handler that adds the round trips of <see cref="Route"/> to timeouts.</summary>
    public DeviceRouter Router { get; init; } = DeviceRouter.Local;

    /// <summary>The login of the handler, for the one place the handler cannot answer: WebSocket upgrades (relay).</summary>
    internal NetworkCredential? Credentials { get; init; }

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

    /// <summary>
    /// Creates a client with a real <see cref="SocketsHttpHandler"/> for the given options: its connections open on
    /// <see cref="VapixConnectionOptions.Route"/> through <paramref name="router"/> (default: the server's own network only).
    /// </summary>
    public static VapixClient Create(VapixConnectionOptions options, DeviceRouter? router = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        router ??= DeviceRouter.Local;
        var baseAddress = BuildBaseAddress(options.Scheme, options.Address);
        var pinning = new CertificatePinning(options.PinnedCertificateFingerprint) { TrustAnchors = options.TrustAnchors };
        var handler = CreateHandler(baseAddress, options.Credentials, pinning, options.AllowBasicOverHttp, options.Route, router, options.Timeout);
        return new VapixClient(baseAddress, handler, pinning, options.Timeout) { Credentials = options.Credentials, Route = options.Route, Router = router };
    }

    /// <summary>
    /// Builds the HTTP handler: Digest for http (plus Basic when <paramref name="allowBasicOverHttp"/>, never through a
    /// site), Basic or Digest for https, TOFU certificate pinning, no redirects, no cookies. With both schemes cached the
    /// handler answers the strongest challenge the device offers (Digest before Basic). Every connection opens on
    /// <paramref name="route"/> through <paramref name="router"/>; TLS, pinning and the login stay here, end to end.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Uri baseAddress, NetworkCredential? credentials, CertificatePinning pinning, bool allowBasicOverHttp = false,
        DeviceRoute route = default, DeviceRouter? router = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(pinning);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PreAuthenticate = true,
            SslOptions = { RemoteCertificateValidationCallback = pinning.ValidateSslCallback },
        };
        (router ?? DeviceRouter.Local).Configure(handler, route, timeout ?? DeviceConnections.DefaultTimeout);

        if (credentials is not null)
        {
            var cache = new CredentialCache();
            var prefix = new Uri(baseAddress.GetLeftPart(UriPartial.Authority) + "/");
            cache.Add(prefix, "Digest", credentials);
            if (baseAddress.Scheme == Uri.UriSchemeHttps || (allowBasicOverHttp && route.IsLocal))
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
    /// <summary>
    /// <c>apidiscovery.cgi getApiList</c>; on AXIS OS before 8.50 (404) the list derived from the <c>param.cgi</c> Properties
    /// (<see cref="LegacyApiList"/>).
    /// </summary>
    public async Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        string json;
        try
        {
            json = await PostJsonAsync(ApiDiscoveryPath, """{"apiVersion":"1.0","method":"getApiList"}""", ct).ConfigureAwait(false);
        }
        catch (VapixException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex is not VapixAuthenticationException)
        {
            return LegacyApiList.FromProperties(await ListParametersAsync(LegacyApiList.Groups, ct).ConfigureAwait(false));
        }

        var apis = VapixParsers.ParseApiList(json);
        if (LegacyApiList.PropertyIds.All(id => apis.Any(a => a.Id == id)))
        {
            return apis;
        }

        // Some APIs are identified by a parameter (VAPIX "Identification"), not by the API list: the Application API by
        // Properties.EmbeddedDevelopment.Version (AXIS OS 11.x may not list it), parameter management and user management
        // by Properties.API. One read of the group Properties adds those the list left out.
        try
        {
            var properties = await ListParametersAsync(LegacyApiList.Groups, ct).ConfigureAwait(false);
            var missing = LegacyApiList.FromProperties(properties).Where(d => LegacyApiList.PropertyIds.Contains(d.Id) && !apis.Any(a => a.Id == d.Id));
            return [.. apis, .. missing];
        }
        catch (Exception ex) when (ex is VapixException or HttpRequestException or System.Text.Json.JsonException or FormatException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // The list itself was read: a failed extra read (timeout, refused, 401) never fails it.
            return apis;
        }
    }

    public async Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
    {
        var json = await PostJsonAsync(BasicDeviceInfoPath, """{"apiVersion":"1.0","method":"getAllProperties"}""", ct).ConfigureAwait(false);
        return VapixParsers.ParseBasicDeviceInfo(json);
    }

    /// <summary>basicdeviceinfo getAllProperties with its own timeout (the status poll: a device that needs longer counts as not answering).</summary>
    public async Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BasicDeviceInfoPath)
        {
            Content = new StringContent("""{"apiVersion":"1.0","method":"getAllProperties"}""", Encoding.UTF8, JsonMediaType),
        };
        request.Options.Set(VapixRequestOptions.Timeout, timeout);
        return VapixParsers.ParseBasicDeviceInfo(await SendForStringAsync(request, ct).ConfigureAwait(false));
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
        var deviceTimeout = request.Options.TryGetValue(VapixRequestOptions.Timeout, out var requested) ? requested : _timeout;
        request.Options.Set(DeviceRouter.ConnectTimeoutOption, deviceTimeout);
        var streamed = request.Options.TryGetValue(VapixRequestOptions.StreamResponse, out var stream) && stream;

        // Through a site at most a few large transfers run at once (docs/sites.md): the wait is not part of the device's
        // timeout. A streamed answer keeps its slot until the caller disposes the response.
        var slot = !Route.IsLocal && (streamed || IsLargeBody(request.Content))
            ? await Router.EnterLargeTransferAsync(Route, ct).ConfigureAwait(false)
            : null;
        var timeout = Router.Timeout(Route, deviceTimeout);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            // Streamed answers (VapixRequestOptions.StreamResponse) are not buffered, so MaxResponseBytes does not apply:
            // the caller reads the body itself and enforces its own limit.
            var completion = streamed ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
            var response = await _http.SendAsync(request, completion, timeoutCts.Token).ConfigureAwait(false);
            NormalizeCharset(response.Content);
            if (slot is not null && streamed)
            {
                response.Content = new SlotContent(response.Content, slot);
                slot = null;
            }

            return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The device did not answer within {timeout.TotalSeconds:0} seconds."),
                new TimeoutException(ex.Message, ex));
        }
        catch (HttpRequestException ex) when (SiteFailure(ex) is { } site)
        {
            // The site could not carry the connection: a site error, never a device error (docs/sites.md).
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(site).Throw();
            throw;
        }
        catch (HttpRequestException ex) when (IsTooLarge(ex))
        {
            throw new VapixResponseTooLargeException(ex);
        }
        catch (HttpRequestException ex) when (Pinning?.MismatchFingerprint is { } actual)
        {
            throw new CertificateChangedException(Pinning.PinnedFingerprint ?? string.Empty, actual.Length == 0 ? null : actual, ex);
        }
        finally
        {
            slot?.Dispose();
        }
    }

    /// <summary>Request bodies above this size (or of unknown size) are large transfers through a site.</summary>
    public const long LargeBodyBytes = 1024 * 1024;

    private static bool IsLargeBody(HttpContent? content) =>
        content is not null && (content.Headers.ContentLength is not { } length || length > LargeBodyBytes);

    /// <summary>A streamed answer that frees its site slot when the response is disposed.</summary>
    private sealed class SlotContent : HttpContent
    {
        private readonly HttpContent _inner;
        private IDisposable? _slot;

        public SlotContent(HttpContent inner, IDisposable slot)
        {
            _inner = inner;
            _slot = slot;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _inner.CopyToAsync(stream, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => _inner.ReadAsStreamAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                Interlocked.Exchange(ref _slot, null)?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Session token for one WebSocket login (VAPIX, valid once and for 15 s).</summary>
    internal const string WsSessionPath = "axis-cgi/wssession.cgi";

    /// <inheritdoc />
    /// <remarks>
    /// The upgrade goes through this client's handler, so the certificate pin applies to wss exactly like to requests;
    /// a pin mismatch throws <see cref="CertificateChangedException"/>.
    /// </remarks>
    public async Task<System.Net.WebSockets.WebSocket> ConnectWebSocketAsync(string pathAndQuery, TimeSpan keepAlive, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        if (!pathAndQuery.StartsWith('/') || pathAndQuery.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must start with one /.", nameof(pathAndQuery));
        }

        EnsureSameDevice(new Uri(pathAndQuery, UriKind.Relative));
        string token;
        using (var request = new HttpRequestMessage(HttpMethod.Get, WsSessionPath))
        {
            try
            {
                token = (await SendForStringAsync(request, ct).ConfigureAwait(false)).Trim();
            }
            catch (VapixAuthenticationException ex)
            {
                throw new UnauthorizedAccessException(WebSocketRefusal(ex.StatusCode), ex);
            }
        }

        if (token.Length == 0 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '~')))
        {
            throw new VapixException("The device sent no usable WebSocket session token.", HttpStatusCode.OK);
        }

        var target = new Uri(BaseAddress, pathAndQuery);
        var builder = new UriBuilder(target)
        {
            Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = target.Port,
            Query = (target.Query.Length > 1 ? target.Query[1..] + "&" : string.Empty) + "wssession=" + token,
        };
        var socket = new System.Net.WebSockets.ClientWebSocket();
        socket.Options.KeepAliveInterval = keepAlive;
        socket.Options.KeepAliveTimeout = keepAlive;
        socket.Options.CollectHttpResponseDetails = true; // a refused upgrade (401, 403) is told apart from a broken connection
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Router.Timeout(Route, _timeout));
        try
        {
            await socket.ConnectAsync(builder.Uri, _http, timeoutCts.Token).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex)
        {
            var upgradeStatus = socket.HttpStatusCode;
            socket.Dispose();
            if (SiteFailure(ex) is { } site)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(site).Throw();
            }

            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new TaskCanceledException(
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The device did not answer within {_timeout.TotalSeconds:0} seconds."),
                    new TimeoutException(ex.Message, ex));
            }

            if (Pinning?.MismatchFingerprint is { } actual)
            {
                throw new CertificateChangedException(Pinning.PinnedFingerprint ?? string.Empty, actual.Length == 0 ? null : actual, ex);
            }

            var status = upgradeStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? upgradeStatus
                : ex is System.Net.WebSockets.WebSocketException { InnerException: HttpRequestException http } ? http.StatusCode
                : null;
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException(WebSocketRefusal(status), new VapixAuthenticationException($"{pathAndQuery}: HTTP {(int)status}", status.Value));
            }

            throw;
        }
    }

    /// <summary>
    /// A WebSocket of the device's own web interface (Web View relay): the upgrade goes through this client's handler, so
    /// the stored login answers the device's challenge and the certificate pin applies. Unlike
    /// <see cref="ConnectWebSocketAsync"/> no session token is added: the page brings its own. <paramref name="headers"/>
    /// are the page's headers (Origin, Cookie, User-Agent); the WebSocket headers themselves are set by the socket.
    /// </summary>
    /// <exception cref="ArgumentException">The path does not stay on the device.</exception>
    public async Task<System.Net.WebSockets.ClientWebSocket> ConnectRelayWebSocketAsync(
        string pathAndQuery, IReadOnlyList<string> subProtocols, IEnumerable<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        ArgumentNullException.ThrowIfNull(subProtocols);
        ArgumentNullException.ThrowIfNull(headers);
        if (!pathAndQuery.StartsWith('/') || pathAndQuery.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must start with one /.", nameof(pathAndQuery));
        }

        EnsureSameDevice(new Uri(pathAndQuery, UriKind.Relative));
        var target = new Uri(BaseAddress, pathAndQuery);
        var builder = new UriBuilder(target) { Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Port = target.Port };
        var headerList = headers.ToList();
        var (socket, challenges) = await TryConnectAsync(builder.Uri, subProtocols, headerList, null, ct).ConfigureAwait(false);
        if (socket is not null)
        {
            return socket;
        }

        // 401: the handler does not answer login challenges of WebSocket upgrades; answer it with the stored login.
        var authorization = Credentials is null ? null
            : WebSocketLogin.Answer(challenges, target.PathAndQuery, Credentials, target.Scheme == Uri.UriSchemeHttps);
        if (authorization is null)
        {
            throw new VapixAuthenticationException($"{pathAndQuery}: HTTP 401", HttpStatusCode.Unauthorized);
        }

        (socket, _) = await TryConnectAsync(builder.Uri, subProtocols, headerList, authorization, ct).ConfigureAwait(false);
        return socket ?? throw new VapixAuthenticationException($"{pathAndQuery}: HTTP 401", HttpStatusCode.Unauthorized);
    }

    /// <summary>One upgrade; on 401 null and the device's challenges, other failures throw.</summary>
    private async Task<(System.Net.WebSockets.ClientWebSocket? Socket, IReadOnlyList<string> Challenges)> TryConnectAsync(
        Uri uri, IReadOnlyList<string> subProtocols, IReadOnlyList<KeyValuePair<string, string>> headers, string? authorization, CancellationToken ct)
    {
        var socket = new System.Net.WebSockets.ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.CollectHttpResponseDetails = true;
        foreach (var protocol in subProtocols)
        {
            socket.Options.AddSubProtocol(protocol);
        }

        foreach (var (name, value) in headers)
        {
            try
            {
                socket.Options.SetRequestHeader(name, value);
            }
            catch (ArgumentException)
            {
                // a header the socket sets itself
            }
        }

        if (authorization is not null)
        {
            socket.Options.SetRequestHeader("Authorization", authorization);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Router.Timeout(Route, _timeout));
        try
        {
            await socket.ConnectAsync(uri, _http, timeoutCts.Token).ConfigureAwait(false);
            return (socket, []);
        }
        catch (Exception ex)
        {
            var status = socket.HttpStatusCode;
            var challenges = socket.HttpResponseHeaders is { } responseHeaders
                && responseHeaders.TryGetValue("WWW-Authenticate", out var values) ? values.ToList() : [];
            socket.Dispose();
            if (SiteFailure(ex) is { } site)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(site).Throw();
            }

            if (Pinning?.MismatchFingerprint is { } actual)
            {
                throw new CertificateChangedException(Pinning.PinnedFingerprint ?? string.Empty, actual.Length == 0 ? null : actual, ex);
            }

            if (status == HttpStatusCode.Unauthorized)
            {
                return (null, challenges);
            }

            throw;
        }
    }

    private IReadOnlyList<string>? _relayChallenges;
    private int _relayNonceCount;

    /// <summary>
    /// A request of the device's web interface (Web View relay), logged in like a browser: the login goes with the first
    /// try (Basic over HTTPS, Digest with the device's last challenge), because some pages of the web interface hand out a
    /// session cookie with every 401 and reject a second try with the old one (<c>login.cgi</c>). On a 401 the device's new
    /// cookies and challenge are taken and the request is sent once more. <paramref name="build"/> creates the request
    /// (again for the second try; its body must be rewindable); <paramref name="pageCookie"/> is the page's Cookie header.
    /// Returns the answer and the cookies the device set on the refused try (the page gets them too).
    /// </summary>
    public async Task<(HttpResponseMessage Response, IReadOnlyList<string> SetCookies)> SendRelayAsync(
        Func<HttpRequestMessage> build, string? pageCookie, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);
        var https = BaseAddress.Scheme == Uri.UriSchemeHttps;
        var first = build();
        var target = new Uri(BaseAddress, first.RequestUri!).PathAndQuery;
        SetRelayHeaders(first, Preemptive(first.Method.Method, target, https), pageCookie);
        var response = await SendAsync(first, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || Credentials is null)
        {
            return (response, []);
        }

        var challenges = response.Headers.WwwAuthenticate.Select(h => h.ToString()).ToList();
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        var authorization = WebSocketLogin.Answer(challenges, target, Credentials, https, method: first.Method.Method);
        if (authorization is null)
        {
            return (response, []);
        }

        _relayChallenges = challenges;
        Volatile.Write(ref _relayNonceCount, 1);
        response.Dispose();
        var second = build();
        SetRelayHeaders(second, authorization, MergeCookies(pageCookie, setCookies));
        return (await SendAsync(second, ct).ConfigureAwait(false), setCookies);
    }

    /// <summary>The login for the first try: Digest with the last challenge (next nonce count), else Basic over HTTPS.</summary>
    private string? Preemptive(string method, string target, bool https)
    {
        if (Credentials is null)
        {
            return null;
        }

        if (_relayChallenges is { } challenges)
        {
            return WebSocketLogin.Answer(challenges, target, Credentials, https, method: method, nonceCount: Interlocked.Increment(ref _relayNonceCount));
        }

        return https ? WebSocketLogin.Answer(["Basic"], target, Credentials, https) : null;
    }

    private static void SetRelayHeaders(HttpRequestMessage request, string? authorization, string? cookie)
    {
        request.Headers.Remove("Cookie");
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
    }

    /// <summary>The page's cookies with the ones the device just set (same name: the new value wins).</summary>
    internal static string MergeCookies(string? pageCookie, IReadOnlyList<string> setCookies)
    {
        var cookies = new List<KeyValuePair<string, string>>();
        foreach (var part in (pageCookie ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            cookies.Add(eq > 0 ? KeyValuePair.Create(part[..eq], part[(eq + 1)..]) : KeyValuePair.Create(part, string.Empty));
        }

        foreach (var set in setCookies)
        {
            var pair = set.Split(';', 2)[0].Trim();
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = pair[..eq];
            cookies.RemoveAll(c => c.Key == name);
            cookies.Add(KeyValuePair.Create(name, pair[(eq + 1)..]));
        }

        return string.Join("; ", cookies.Select(c => c.Key + "=" + c.Value));
    }

    /// <summary>The shared refusal text of a WebSocket login, like other requests: 403 Forbidden, otherwise Unauthorized.</summary>
    private static string WebSocketRefusal(HttpStatusCode? status) =>
        status == HttpStatusCode.Forbidden ? Oadm.Sdk.Devices.DeviceMessages.Forbidden : Oadm.Sdk.Devices.DeviceMessages.Unauthorized;

    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>The <see cref="SiteException"/> a connect through a site failed with (HttpClient and ClientWebSocket wrap it), else null.</summary>
    internal static SiteException? SiteFailure(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SiteException site)
            {
                return site;
            }
        }

        return null;
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
            throw new VapixAuthenticationException($"{path}: HTTP {(int)response.StatusCode}", response.StatusCode)
            {
                RealmSerial = Discovery.AxisRealm.TryGetSerial(response.Headers.WwwAuthenticate.Select(h => h.ToString())),
            };
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
