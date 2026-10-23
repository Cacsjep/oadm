using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Est.Device;

/// <summary>A camera answered with an error (REST error object or HTTP status). Message = user text.</summary>
public sealed class CameraApiException : Exception
{
    public CameraApiException()
    {
    }

    public CameraApiException(string message)
        : base(message)
    {
    }

    public CameraApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A certificate in the camera's store (REST cert v1).</summary>
public sealed record CameraCertificate(string Alias, string Pem, string? Keystore = null);

/// <summary>An EST profile on the camera (REST cert-est v1 <c>profiles</c>).</summary>
public sealed record CameraEstProfile(
    string Id,
    string? Server,
    IReadOnlyList<string> CaCertificates,
    string? Certificate,
    IReadOnlyList<string> Services,
    string? Status,
    IReadOnlyList<string> SubjectAlternativeNames);

/// <summary>What <c>GET /config/discover</c> lists of the two APIs the set-up needs (null = not listed).</summary>
public sealed record CameraApis(string? CertVersion, string? CertEstVersion)
{
    public bool HasCert => IsV1(CertVersion);

    public bool HasCertEst => IsV1(CertEstVersion);

    private static bool IsV1(string? version) => version is not null && Version.TryParse(version, out var v) && v.Major == 1;
}

/// <summary>
/// The camera's REST APIs the EST set-up uses, through <see cref="IVapixClient"/> (the device's route and login):
/// <c>/config/discover</c>, certificate management <c>/config/rest/cert/v1</c> and enrollment over secure transport
/// <c>/config/rest/cert-est/v1</c> (AXIS OS 12.10+). Envelope <c>{"data": ...}</c>; answers
/// <c>{"status":"success"|"error","data"?,"error":{code,message}}</c>; ids and aliases percent-encoded in paths.
/// </summary>
public static class CameraEstApi
{
    public const string DiscoverPath = "config/discover";
    public const string CertPath = "config/rest/cert/v1";
    public const string EstPath = "config/rest/cert-est/v1";

    /// <summary>The camera's service names: WEBSERVER (HTTPS), NETAUTH (IEEE 802.1X), MQTT, RTSPS.</summary>
    public static string ServiceOf(ProfileUse use) => use switch
    {
        ProfileUse.Https => "WEBSERVER",
        ProfileUse.Dot1x => "NETAUTH",
        ProfileUse.Mqtt => "MQTT",
        ProfileUse.Rtsps => "RTSPS",
        _ => throw new ArgumentOutOfRangeException(nameof(use)),
    };

    public static async Task<CameraApis> DiscoverAsync(IVapixClient vapix, CancellationToken ct)
    {
        var (status, body) = await SendAsync(vapix, HttpMethod.Get, DiscoverPath, null, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            return new CameraApis(null, null);
        }

        Ensure(status, body, "Read the camera's API list");
        return ParseDiscover(body);
    }

    public static CameraApis ParseDiscover(string json)
    {
        try
        {
            var apis = JsonNode.Parse(json)?["apis"];
            return new CameraApis(apis?["cert"]?["v1"]?["version"]?.GetValue<string>(), apis?["cert-est"]?["v1"]?["version"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new CameraApis(null, null);
        }
    }

    /// <summary>The services the camera can give an enrolled certificate to, and its EST profiles.</summary>
    public static async Task<(IReadOnlyList<string> Services, IReadOnlyList<CameraEstProfile> Profiles)> ReadEstAsync(IVapixClient vapix, CancellationToken ct)
    {
        var data = await GetDataAsync(vapix, EstPath, "Read the camera's EST settings", ct).ConfigureAwait(false);
        return ParseEst(data);
    }

    public static (IReadOnlyList<string> Services, IReadOnlyList<CameraEstProfile> Profiles) ParseEst(JsonNode? data) =>
        (Strings(data?["supportedServices"]), [.. (data?["profiles"] as JsonArray ?? []).OfType<JsonObject>().Select(ParseProfile)]);

    public static async Task<IReadOnlyList<CameraEstProfile>> ReadProfilesAsync(IVapixClient vapix, CancellationToken ct) =>
        [.. (await GetDataAsync(vapix, EstPath + "/profiles", "Read the camera's EST profiles", ct).ConfigureAwait(false) as JsonArray ?? [])
            .OfType<JsonObject>().Select(ParseProfile)];

    public static Task AddProfileAsync(IVapixClient vapix, JsonObject profile, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Post, EstPath + "/profiles", profile, "Add EST profile", ct);

    /// <summary>
    /// Changes a profile in place (PATCH): the camera enrolls again under the same name. Deleting and adding it again does
    /// not work while its previous certificate is still on the camera (seen on 10.0.0.48: the new one is never stored).
    /// </summary>
    public static Task RemoveProfileAsync(IVapixClient vapix, string id, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Delete, $"{EstPath}/profiles/{Uri.EscapeDataString(id)}", null, "Remove EST profile", ct);

    public static async Task<IReadOnlyList<CameraCertificate>> ListCertificatesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseCertificates(await GetDataAsync(vapix, CertPath + "/certificates", "Read the camera's certificates", ct).ConfigureAwait(false));

    /// <summary>One certificate by alias; null when the camera has none of that name.</summary>
    public static async Task<CameraCertificate?> ReadCertificateAsync(IVapixClient vapix, string alias, CancellationToken ct)
    {
        var (status, body) = await SendAsync(vapix, HttpMethod.Get, CertPath + "/certificates/" + Uri.EscapeDataString(alias), null, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        return ReadData(status, body, "read the certificate " + alias) is JsonObject data && ParseCertificates(new JsonArray(data.DeepClone())) is { Count: > 0 } list ? list[0] : null;
    }

    /// <summary>
    /// Deletes a certificate; false when the camera keeps it because a use (web server, network access, MQTT) still holds
    /// it ("Certificate is in use", seen on 10.0.0.48 also after its EST profile was removed) or it is not there.
    /// </summary>
    public static async Task<bool> TryDeleteCertificateAsync(IVapixClient vapix, string alias, CancellationToken ct)
    {
        var (status, body) = await SendAsync(vapix, HttpMethod.Delete, CertPath + "/certificates/" + Uri.EscapeDataString(alias), null, ct).ConfigureAwait(false);
        try
        {
            ReadData(status, body, "delete the certificate " + alias);
            return true;
        }
        catch (CameraApiException)
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<CameraCertificate>> ListCaCertificatesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseCertificates(await GetDataAsync(vapix, CertPath + "/ca_certificates", "Read the camera's CA certificates", ct).ConfigureAwait(false));

    public static Task AddCaCertificateAsync(IVapixClient vapix, string alias, string pem, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Post, CertPath + "/ca_certificates", new JsonObject { ["alias"] = alias, ["certificate"] = pem }, "Install CA certificate", ct);

    public static IReadOnlyList<CameraCertificate> ParseCertificates(JsonNode? data) =>
        [.. (data as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => c["alias"] is not null && c["certificate"] is not null)
            .Select(c => new CameraCertificate(c["alias"]!.GetValue<string>(), c["certificate"]!.GetValue<string>(), c["keystore"]?.GetValue<string>()))];

    private static CameraEstProfile ParseProfile(JsonObject p) => new(
        p["id"]?.GetValue<string>() ?? string.Empty,
        p["server"]?.GetValue<string>(),
        Strings(p["caCertificates"]),
        p["certificate"]?.GetValue<string>(),
        Strings(p["services"]),
        p["status"]?.GetValue<string>(),
        Strings(p["subjectAlternativeNames"]));

    private static List<string> Strings(JsonNode? node) =>
        [.. (node as JsonArray ?? []).Where(n => n is not null).Select(n => n!.GetValue<string>())];

    private static async Task<JsonNode?> GetDataAsync(IVapixClient vapix, string path, string action, CancellationToken ct)
    {
        var (status, body) = await SendAsync(vapix, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return ReadData(status, body, action);
    }

    private static async Task<JsonNode?> SendDataAsync(IVapixClient vapix, HttpMethod method, string path, JsonObject? data, string action, CancellationToken ct)
    {
        var body = data is null ? null : new JsonObject { ["data"] = data }.ToJsonString();
        var (status, answer) = await SendAsync(vapix, method, path, body, ct).ConfigureAwait(false);
        return ReadData(status, answer, action);
    }

    /// <summary>The <c>data</c> of a success answer; a REST error or a failed HTTP status throws with the camera's message.</summary>
    public static JsonNode? ReadData(HttpStatusCode status, string body, string action)
    {
        JsonNode? root = null;
        try
        {
            root = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // not JSON: the HTTP status decides below
        }

        if (root?["status"]?.GetValue<string>() is "error")
        {
            var message = root["error"]?["message"]?.GetValue<string>() ?? "error";
            var code = root["error"]?["code"]?.ToString();
            throw new CameraApiException(string.Create(CultureInfo.InvariantCulture, $"{action}: the camera answered \"{message}\"{(code is null ? "" : $" ({code})")}."));
        }

        Ensure(status, body, action);
        return root?["data"];
    }

    private static void Ensure(HttpStatusCode status, string body, string action)
    {
        if ((int)status is < 200 or > 299)
        {
            var text = DeviceMessages.ForHttpStatus((int)status) ?? string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)status}");
            throw new CameraApiException($"{action}: {text}.");
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(IVapixClient vapix, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, Encoding.UTF8.GetString(bytes));
    }
}
