using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Network.Vapix;

/// <summary>Reads and writes network settings through <see cref="IVapixClient.SendAsync"/> and <see cref="IVapixClient.ListParametersAsync"/>.</summary>
public static class NetworkSettingsClient
{
    /// <summary>
    /// Reads the current settings (read-only). network-settings getNetworkInfo when available, completed with
    /// the IPv6 address mode from param.cgi; the legacy param.cgi Network group otherwise.
    /// </summary>
    public static async Task<CurrentNetworkSettings> ReadAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, string? connectionAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(apis);
        if (NetworkApis.UseJsonApi(apis))
        {
            var settings = await ReadNetworkInfoAsync(vapix, apis, connectionAddress, ct).ConfigureAwait(false);
            return ReadsIpv6ModeSeparately(apis)
                ? await ReadIpv6ModeAsync(vapix, settings, ct).ConfigureAwait(false)
                : settings;
        }

        return await ReadParametersAsync(vapix, apis, ct).ConfigureAwait(false);
    }

    /// <summary>True when the JSON API is used and the IPv6 address mode comes from a second request (param.cgi).</summary>
    public static bool ReadsIpv6ModeSeparately(IReadOnlyList<DeviceApi> apis) =>
        NetworkApis.UseJsonApi(apis) && apis.Supports(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);

    /// <summary>network-settings getNetworkInfo (one request).</summary>
    public static async Task<CurrentNetworkSettings> ReadNetworkInfoAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, string? connectionAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(apis);
        var api = apis.Require(NetworkApis.NetworkSettings, NetworkApis.NetworkSettingsBase);
        var request = new JsonMethodRequest(api.Version, "getNetworkInfo", "{}");
        var json = await SendForStringAsync(vapix, request, ct).ConfigureAwait(false);
        return NetworkInfoParser.ParseGetNetworkInfo(json, connectionAddress);
    }

    /// <summary>Completes <paramref name="settings"/> with the IPv6 address mode from param.cgi (one request).</summary>
    public static async Task<CurrentNetworkSettings> ReadIpv6ModeAsync(IVapixClient vapix, CurrentNetworkSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var ipv6 = await vapix.ListParametersAsync(NetworkInfoParser.Ipv6Groups, ct).ConfigureAwait(false);
        return NetworkInfoParser.WithIpv6Parameters(settings, ipv6);
    }

    /// <summary>The legacy param.cgi Network group for devices without network-settings (one request).</summary>
    public static async Task<CurrentNetworkSettings> ReadParametersAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(apis);
        apis.Require(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);
        var parameters = await vapix.ListParametersAsync(NetworkInfoParser.LegacyGroups, ct).ConfigureAwait(false);
        return NetworkInfoParser.ParseParameters(parameters);
    }

    /// <summary>Sends one write request; throws <see cref="NetworkApiException"/> when the device rejects it.</summary>
    public static async Task SendAsync(IVapixClient vapix, NetworkRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = await SendForStringAsync(vapix, request, ct).ConfigureAwait(false);
        switch (request)
        {
            case JsonMethodRequest json:
                using (var doc = JsonDocument.Parse(text))
                {
                    ThrowIfError(doc.RootElement, json.Method);
                }

                break;
            case ParamUpdateRequest:
                var trimmed = text.Trim();
                if (!trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NetworkApiException("param.cgi update", null, FirstLine(trimmed), null);
                }

                break;
        }
    }

    /// <summary>Throws <see cref="NetworkApiException"/> when a network_settings.cgi response carries an error object.</summary>
    public static void ThrowIfError(JsonElement root, string method)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : null;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            int? subCode = error.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("subCode", out var s) && s.TryGetInt32(out var si) ? si : null;
            throw new NetworkApiException(method, code, message, subCode);
        }
    }

    private static async Task<string> SendForStringAsync(IVapixClient vapix, NetworkRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Path, UriKind.Relative))
        {
            Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType),
        };
        using var response = await vapix.SendAsync(message, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new NetworkApiException(request.Path, (int)response.StatusCode, "The device rejected the credentials (administrator rights are required).", null);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NetworkApiException(request.Path, (int)response.StatusCode, "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), null);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}

/// <summary>The device answered a network request with an error.</summary>
public sealed class NetworkApiException : Exception
{
    public NetworkApiException(string method, int? code, string? deviceMessage, int? subCode)
        : base(Describe(method, code, deviceMessage, subCode))
    {
        Method = method;
        Code = code;
        SubCode = subCode;
    }

    public NetworkApiException()
    {
        Method = string.Empty;
    }

    public NetworkApiException(string message)
        : base(message)
    {
        Method = string.Empty;
    }

    public NetworkApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Method = string.Empty;
    }

    public string Method { get; }

    public int? Code { get; }

    public int? SubCode { get; }

    private static string Describe(string method, int? code, string? message, int? subCode)
    {
        var sb = new StringBuilder(method).Append(" failed");
        if (code is { } c)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({c}");
            if (subCode is { } s)
            {
                sb.Append(CultureInfo.InvariantCulture, $"/{s}");
            }

            sb.Append(')');
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            sb.Append(": ").Append(message);
        }

        return sb.ToString();
    }
}
