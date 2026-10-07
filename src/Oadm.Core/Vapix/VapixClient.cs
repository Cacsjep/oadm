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
}

/// <summary>
/// VAPIX access to one Axis device: one method per endpoint. Owns its <see cref="HttpClient"/>,
/// so dispose it when done. Thread-safe for concurrent calls.
/// </summary>
/// <remarks>
/// Authentication: AXIS OS 12 offers Digest on HTTP and Basic on HTTPS by default
/// (Network.HTTP.AuthenticationPolicy=recommended). The credential cache therefore offers
/// Digest only for http:// and Basic or Digest for https://, so Basic never travels in clear text.
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

    private const string JsonMediaType = "application/json";

    private readonly HttpClient _http;

    /// <summary>Creates a client over a custom handler (tests, or a handler built by <see cref="CreateHandler"/>).</summary>
    public VapixClient(Uri baseAddress, HttpMessageHandler handler, CertificatePinning? pinning = null, TimeSpan? timeout = null, bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(handler);
        BaseAddress = baseAddress;
        Pinning = pinning;
        _http = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
    }

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
        var pinning = new CertificatePinning(options.PinnedCertificateFingerprint);
        var handler = CreateHandler(baseAddress, options.Credentials, pinning);
        return new VapixClient(baseAddress, handler, pinning, options.Timeout);
    }

    /// <summary>
    /// Builds the HTTP handler: Digest for http, Basic or Digest for https, TOFU certificate
    /// pinning, no redirects, no cookies.
    /// </summary>
    public static HttpClientHandler CreateHandler(Uri baseAddress, NetworkCredential? credentials, CertificatePinning pinning)
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
            if (baseAddress.Scheme == Uri.UriSchemeHttps)
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

    /// <inheritdoc />
    /// <remarks>
    /// Relative URIs resolve against <see cref="BaseAddress"/>. The response is returned as-is
    /// (no status check); a pinned certificate mismatch throws <see cref="CertificateChangedException"/>.
    /// </remarks>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
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
