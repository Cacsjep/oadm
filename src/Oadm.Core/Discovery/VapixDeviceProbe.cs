using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Sites;

namespace Oadm.Core.Discovery;

/// <summary>Anonymous, read-only check whether an address is an Axis device.</summary>
public interface IDeviceProbe
{
    /// <summary>
    /// Probes one address. Returns null when it is not an Axis device (refused, timeout, no AXIS
    /// realm). Never sends credentials and never changes device state.
    /// </summary>
    Task<DeviceProbeResult?> ProbeAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Probes one address on <paramref name="route"/> (the server's own network or a site). A site that cannot carry the
    /// probe throws <see cref="SiteException"/>; it never means "not a device". Default: the server's own network only.
    /// </summary>
    Task<DeviceProbeResult?> ProbeAsync(IPAddress address, DeviceRoute route, TimeSpan timeout, CancellationToken cancellationToken) =>
        route.SiteId is { } site
            ? throw SiteException.For(site, site.ToString(), SiteProblem.NotSupported)
            : ProbeAsync(address, timeout, cancellationToken);
}

/// <summary>
/// VAPIX based probe. Per address: HTTPS 443 then HTTP 80,
/// <c>POST /axis-cgi/basicdeviceinfo.cgi {"apiVersion":"1.0","method":"getAllProperties"}</c>.
/// <list type="bullet">
/// <item>401 with realm <c>AXIS_&lt;serial&gt;</c> (Digest on HTTP, Basic on HTTPS) =&gt; Axis device.</item>
/// <item>200 with a propertyList =&gt; Axis device with anonymous access.</item>
/// <item>Anything else on both schemes =&gt; not a device.</item>
/// </list>
/// Once identified, two more anonymous read-only calls on the same scheme enrich the result:
/// <c>getAllUnrestrictedProperties</c> (basicdeviceinfo 1.1+, model and firmware without login)
/// and <c>systemready.cgi</c> method <c>systemready</c>, whose <c>needsetup</c> field is "yes" on a
/// factory-default device without administrator password. That field decides
/// <see cref="DiscoveredDeviceStatus.PasswordNotSet"/>; the 401/200 answer alone cannot, because
/// a 200 may also mean anonymous viewer access on a configured device.
/// Self-signed certificates are accepted for this anonymous probe only.
/// Connections open on the probed route (<see cref="DeviceRouter"/>): the route travels with each request
/// (<see cref="DeviceRouter.RouteOption"/>) and the handler's connect callback reads it. Each route has its own handler
/// (connection pool), so two sites with the same address never share a kept connection.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The discovery probe's handler opens every connection through DeviceRouter.")]
public sealed class VapixDeviceProbe : IDeviceProbe, IDisposable
{
    /// <summary>Default per-request timeout (spec: Scan.TimeoutMs = 1500).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    private const string BasicDeviceInfoPath = "/axis-cgi/basicdeviceinfo.cgi";
    private const string SystemReadyPath = "/axis-cgi/systemready.cgi";
    private const string GetAllPropertiesBody = """{"apiVersion":"1.0","method":"getAllProperties"}""";
    private const string GetUnrestrictedBody = """{"apiVersion":"1.1","method":"getAllUnrestrictedProperties"}""";
    private const string SystemReadyBody = """{"apiVersion":"1.0","method":"systemready"}""";

    private readonly HttpClient? _shared;
    private readonly ConcurrentDictionary<DeviceRoute, Lazy<HttpClient>> _byRoute = new();
    private readonly DeviceRouter _router;
    private readonly ILogger _logger;
    private readonly TimeSpan _defaultTimeout;

    /// <summary>Creates a probe with its own handlers (one per route) that accept any server certificate.</summary>
    /// <param name="router">Opens the connections on each route; default the server's own network only.</param>
    public VapixDeviceProbe(TimeSpan? defaultTimeout = null, ILogger<VapixDeviceProbe>? logger = null, DeviceRouter? router = null)
    {
        _router = router ?? DeviceRouter.Local;
        _logger = logger ?? NullLogger<VapixDeviceProbe>.Instance;
        _defaultTimeout = defaultTimeout ?? DefaultTimeout;
    }

    /// <summary>Creates a probe on a caller supplied handler for every route (tests use a fake handler; the route is in each request's options).</summary>
    public VapixDeviceProbe(HttpMessageHandler handler, TimeSpan? defaultTimeout = null, ILogger<VapixDeviceProbe>? logger = null, DeviceRouter? router = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _shared = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _router = router ?? DeviceRouter.Local;
        _logger = logger ?? NullLogger<VapixDeviceProbe>.Instance;
        _defaultTimeout = defaultTimeout ?? DefaultTimeout;
    }

    /// <summary>Probes with the default timeout.</summary>
    public Task<DeviceProbeResult?> ProbeAsync(IPAddress address, CancellationToken cancellationToken)
        => ProbeAsync(address, _defaultTimeout, cancellationToken);

    public Task<DeviceProbeResult?> ProbeAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
        => ProbeAsync(address, DeviceRoute.Local, timeout, cancellationToken);

    /// <inheritdoc />
    public async Task<DeviceProbeResult?> ProbeAsync(IPAddress address, DeviceRoute route, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        foreach (var scheme in (string[])["https", "http"])
        {
            var baseUri = BuildBaseUri(scheme, address);
            using var response = await PostAsync(route, baseUri, BasicDeviceInfoPath, GetAllPropertiesBody, timeout, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                continue;
            }

            DeviceProbeResult? result = null;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var serial = AxisRealm.TryGetSerial(GetChallenges(response));
                if (serial is not null)
                {
                    result = new DeviceProbeResult(address, serial, scheme, null, null, null, DiscoveredDeviceStatus.CredentialsRequired, false);
                }
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                var props = await ReadPropertiesAsync(response, cancellationToken).ConfigureAwait(false);
                if (props is not null && SerialNumber.Normalize(props.GetValueOrDefault("SerialNumber")) is { } serial)
                {
                    result = new DeviceProbeResult(
                        address,
                        serial,
                        scheme,
                        props.GetValueOrDefault("ProdNbr"),
                        props.GetValueOrDefault("ProdFullName"),
                        props.GetValueOrDefault("Version"),
                        DiscoveredDeviceStatus.AnonymousAccess,
                        true,
                        props.GetValueOrDefault("ProdType"));
                }
            }

            if (result is null)
            {
                DiscoveryLog.ProbeNotAxis(_logger, address, scheme, response.StatusCode);
                continue;
            }

            result = await EnrichAsync(route, result, baseUri, timeout, cancellationToken).ConfigureAwait(false);
            DiscoveryLog.ProbeAxis(_logger, address, result.Serial, result.Model, result.Status, scheme);
            return result;
        }

        return null;
    }

    public void Dispose()
    {
        _shared?.Dispose();
        foreach (var client in _byRoute.Values.Where(c => c.IsValueCreated))
        {
            client.Value.Dispose();
        }
    }

    /// <summary>
    /// Parses a basicdeviceinfo response body. Returns the propertyList as strings, or null when
    /// the body is not a successful basicdeviceinfo answer.
    /// </summary>
    public static Dictionary<string, string>? ParseProperties(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("propertyList", out var list)
                || list.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in list.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    result[p.Name] = p.Value.GetString()!;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a systemready.cgi response. Returns true/false for needsetup "yes"/"no", null when
    /// the field is missing or the body is not a systemready answer.
    /// </summary>
    public static bool? ParseNeedSetup(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("needsetup", out var needSetup)
                && needSetup.ValueKind == JsonValueKind.String)
            {
                return needSetup.GetString() switch
                {
                    "yes" => true,
                    "no" => false,
                    _ => null,
                };
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<DeviceProbeResult> EnrichAsync(DeviceRoute route, DeviceProbeResult result, Uri baseUri, TimeSpan timeout, CancellationToken ct)
    {
        if (result.Model is null)
        {
            using var response = await PostAsync(route, baseUri, BasicDeviceInfoPath, GetUnrestrictedBody, timeout, ct).ConfigureAwait(false);
            if (response?.StatusCode == HttpStatusCode.OK)
            {
                var props = await ReadPropertiesAsync(response, ct).ConfigureAwait(false);
                if (props is not null)
                {
                    result = result with
                    {
                        Model = props.GetValueOrDefault("ProdNbr"),
                        ProductName = props.GetValueOrDefault("ProdFullName"),
                        FirmwareVersion = props.GetValueOrDefault("Version"),
                        ProductType = props.GetValueOrDefault("ProdType"),
                    };
                }
            }
        }

        using (var response = await PostAsync(route, baseUri, SystemReadyPath, SystemReadyBody, timeout, ct).ConfigureAwait(false))
        {
            if (response?.StatusCode == HttpStatusCode.OK)
            {
                var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
                if (body is not null && ParseNeedSetup(body) == true)
                {
                    result = result with { Status = DiscoveredDeviceStatus.PasswordNotSet };
                }
            }
        }

        return result;
    }

    /// <remarks>A <see cref="SiteException"/> (the site cannot carry the probe) is not "no device" and passes through.</remarks>
    private async Task<HttpResponseMessage?> PostAsync(DeviceRoute route, Uri baseUri, string path, string body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_router.Timeout(route, timeout));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Options.Set(DeviceRouter.RouteOption, route);
        request.Options.Set(DeviceRouter.ConnectTimeoutOption, timeout);
        try
        {
            var response = await ClientFor(route).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                // Buffer the (small) body inside the timeout window.
                await response.Content.LoadIntoBufferAsync(64 * 1024, cts.Token).ConfigureAwait(false);
            }

            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // per-request timeout
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && SiteErrors.Find(ex) is { } site)
        {
            // Also a link that drops after the connect: an IOException with the SiteException a few levels deep.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(site).Throw();
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or InvalidOperationException)
        {
            return null; // refused, reset, TLS failure, oversized body
        }
    }

    private HttpClient ClientFor(DeviceRoute route) =>
        _shared ?? _byRoute.GetOrAdd(route, r => new Lazy<HttpClient>(() => new HttpClient(CreateHandler(_router), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan })).Value;

    private static async Task<Dictionary<string, string>?> ReadPropertiesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        return body is null ? null : ParseProperties(body);
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> GetChallenges(HttpResponseMessage response)
        => response.Headers.TryGetValues("WWW-Authenticate", out var values) ? values : [];

    private static Uri BuildBaseUri(string scheme, IPAddress address)
        => new($"{scheme}://{(address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())}");

    private static SocketsHttpHandler CreateHandler(DeviceRouter router) => new()
    {
        ConnectCallback = router.CreateRequestRoutedCallback(DefaultTimeout),
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        MaxResponseHeadersLength = 64,
        SslOptions =
        {
            // Anonymous discovery only: no credentials are ever sent through this handler, so
            // accepting self-signed device certificates here is safe. Trust-on-first-use pinning
            // happens later in the VAPIX client.
#pragma warning disable CA5359 // Do not disable certificate validation
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
#pragma warning restore CA5359
        },
    };
}
