using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>
/// The wired 802.1X state of <c>getNetworkInfo</c> (<c>data.devices[].wired.8021X</c>), of the parameters
/// <c>Network.Interface.I0.dot1x</c>, with the certificates of the SOAP certificate set where the device keeps them there
/// (<see cref="Dot1xSettings"/>). <paramref name="ReportsCertificates"/>: <c>getNetworkInfo</c> names the client and CA
/// certificates (network settings 1.38, AXIS OS 12.11; before, the device ignores them in the set request).
/// </summary>
public sealed record Wired8021X(
    string DeviceName,
    bool Enabled,
    string? Status,
    string? Mode,
    string? Identity,
    string? EapolVersion,
    string? CertClient,
    IReadOnlyList<string> CertsCa,
    bool ReportsCertificates = false);

/// <summary>What the PKI tasks need from <c>getNetworkInfo</c>: names and addresses for the certificate, and 802.1X.</summary>
public sealed record DeviceNetworkInfo(
    string? HostName,
    string? DomainName,
    IReadOnlyList<string> Addresses,
    Wired8021X? Dot1x)
{
    /// <summary>"host.domain" when both are known.</summary>
    public string? Fqdn => string.IsNullOrWhiteSpace(HostName) || string.IsNullOrWhiteSpace(DomainName) ? null : $"{HostName}.{DomainName.Trim('.')}";
}

/// <summary>
/// network-settings 1.x (<c>POST /axis-cgi/network_settings.cgi</c>): <c>getNetworkInfo</c> and <c>setWired8021XConfiguration</c>.
/// Error replies are HTTP 200 with an <c>error</c> object.
/// </summary>
public static class NetworkInfoApi
{
    public const string ApiId = "network-settings";
    public const string Path = "axis-cgi/network_settings.cgi";
    public const string EapTlsMode = "WPA-Enterprise-EAPTLS";

    public static async Task<DeviceNetworkInfo> GetAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        var version = apis.FindApi(ApiId, 1)?.Version ?? "1.0";
        var json = await SendAsync(vapix, version, "getNetworkInfo", null, "Read network settings", ct).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>
    /// Enables EAP-TLS with the identity and EAPOL version and, when given, the client certificate and CA certificates (null:
    /// left out, the device's certificate set holds them).
    /// </summary>
    public static Task EnableEapTlsAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, string deviceName, string identity, int eapolVersion, string? certClient, IReadOnlyList<string>? certsCa, CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["deviceName"] = deviceName,
            ["enabled"] = true,
            ["mode"] = EapTlsMode,
            ["identity"] = identity,
            ["eapolVersion"] = EapolName(eapolVersion),
        };
        if (certClient is not null)
        {
            parameters["certClient"] = certClient;
        }

        if (certsCa is not null)
        {
            parameters["certsCA"] = new JsonArray([.. certsCa.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        }

        return SendAsync(vapix, Version(apis), "setWired8021XConfiguration", parameters, "Set 802.1X configuration", ct);
    }

    /// <summary>
    /// Sets only the CA certificates the device checks the RADIUS server with; everything else of the EAP-TLS configuration
    /// (on or off, identity, EAPOL version, client certificate) is written back as <paramref name="current"/> has it.
    /// </summary>
    public static Task SetCaCertificatesAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, Wired8021X current, IReadOnlyList<string> certsCa, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        var parameters = new JsonObject
        {
            ["deviceName"] = current.DeviceName,
            ["enabled"] = current.Enabled,
            ["mode"] = EapTlsMode,
            ["certClient"] = current.CertClient,
            ["certsCA"] = new JsonArray([.. certsCa.Select(c => (JsonNode)JsonValue.Create(c)!)]),
        };
        if (current.Identity is { } identity)
        {
            parameters["identity"] = identity;
        }

        if (current.EapolVersion is { } eapol)
        {
            parameters["eapolVersion"] = eapol;
        }

        return SendAsync(vapix, Version(apis), "setWired8021XConfiguration", parameters, "Set the RADIUS server CA", ct);
    }

    /// <summary>Turns 802.1X off; certificates and the rest of the configuration stay.</summary>
    public static Task DisableAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, string deviceName, CancellationToken ct) =>
        SendAsync(vapix, Version(apis), "setWired8021XConfiguration", new JsonObject { ["deviceName"] = deviceName, ["enabled"] = false }, "Set 802.1X off", ct);

    /// <summary>1 -> "EAPoLv1".</summary>
    public static string EapolName(int version) => string.Create(CultureInfo.InvariantCulture, $"EAPoLv{version}");

    public static DeviceNetworkInfo Parse(string json)
    {
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(json)?["data"];
        }
        catch (JsonException ex)
        {
            throw new PkiDeviceException("Read network settings: the device sent an unreadable answer.", ex);
        }

        var system = data?["system"];
        var hostName = Text(system?["hostname"]?["hostname"]);
        var domain = Text(system?["resolver"]?["domainName"]);
        var addresses = new List<string>();
        Wired8021X? dot1x = null;
        foreach (var device in (data?["devices"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (var family in new[] { "IPv4", "IPv6" })
            {
                foreach (var address in (device[family]?["addresses"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var text = Text(address["address"]);
                    var scope = Text(address["scope"]);
                    if (text is not null && !string.Equals(scope, "link", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) && !addresses.Contains(text))
                    {
                        addresses.Add(text);
                    }
                }
            }

            if (dot1x is null && device["wired"]?["8021X"] is JsonObject wired)
            {
                var eapTls = (wired["configurations"] as JsonArray ?? []).OfType<JsonObject>()
                    .FirstOrDefault(c => string.Equals(Text(c["mode"]), EapTlsMode, StringComparison.Ordinal))?["params"];
                dot1x = new Wired8021X(
                    Text(device["name"]) ?? "eth0",
                    wired["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && on,
                    Text(wired["status"]),
                    Text(wired["mode"]),
                    Text(eapTls?["identity"]),
                    Text(eapTls?["eapolVersion"]),
                    Text(eapTls?["certClient"]),
                    [.. (eapTls?["certsCA"] as JsonArray ?? []).Select(Text).OfType<string>()],
                    eapTls is JsonObject p && (p.ContainsKey("certClient") || p.ContainsKey("certsCA")));
            }
        }

        return new DeviceNetworkInfo(hostName, domain, addresses, dot1x);
    }

    private static string Version(IReadOnlyList<DeviceApi> apis) => apis.FindApi(ApiId, 1)?.Version ?? "1.0";

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static async Task<string> SendAsync(IVapixClient vapix, string version, string method, JsonObject? parameters, string action, CancellationToken ct)
    {
        var request = new JsonObject { ["apiVersion"] = version, ["context"] = "oadm", ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        var answer = await DeviceHttp.SendAsync(vapix, HttpMethod.Post, Path, DeviceHttp.JsonType, request.ToJsonString(), ct).ConfigureAwait(false);
        if ((int)answer.Status is < 200 or >= 300)
        {
            throw new PkiDeviceException($"{action}: {DeviceHttp.DescribeHttp(answer.Status)}") { HttpStatus = (int)answer.Status };
        }

        try
        {
            if (JsonNode.Parse(answer.Body)?["error"] is JsonObject error)
            {
                var message = Text(error["message"]) ?? "Unknown error";
                var code = error["code"]?.ToJsonString();
                throw new PkiDeviceException(code is null ? $"{action}: the device refused: {message}" : $"{action}: the device refused: {message} (code {code})");
            }
        }
        catch (JsonException ex)
        {
            throw new PkiDeviceException($"{action}: the device sent an unreadable answer: {DeviceHttp.FirstLine(answer.Body)}", ex);
        }

        return answer.Body;
    }
}

/// <summary>The device clock: time-service <c>getDateTimeInfo</c> when listed, else the HTTP Date header of that request.</summary>
public static class DeviceClock
{
    public const string TimeServiceId = Oadm.Plugins.DeviceClocks.CameraClock.TimeServiceId;
    public const string TimePath = Oadm.Plugins.DeviceClocks.CameraClock.TimePath;

    /// <summary>The device's current UTC time, or null when it cannot be read (<see cref="Oadm.Plugins.DeviceClocks.CameraClock.ReadAsync"/>).</summary>
    public static Task<DateTimeOffset?> ReadAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct) =>
        Oadm.Plugins.DeviceClocks.CameraClock.ReadAsync(vapix, apis, ct);
}
