using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>A certificate in the device's store (REST cert v1 <c>certificates</c> or <c>ca_certificates</c>).</summary>
public sealed record DeviceCertificate(string Alias, string Pem, string? Keystore = null);

/// <summary>The REST <c>cert</c> API as <c>GET /config/discover</c> lists it.</summary>
public sealed record CertApiSupport(string? Version, string? State)
{
    /// <summary>Released (or unlabelled) v1 at version 1.0 or later.</summary>
    public bool IsSupported =>
        Version is not null
        && System.Version.TryParse(Version, out var v) && v.Major == 1
        && (State is null || string.Equals(State, "released", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// REST certificate management <c>/config/rest/cert/v1</c> (AXIS OS 11.11+; research: tests/Oadm.Plugins.Pki.Tests/Fixtures). JSON envelope
/// <c>{"data": ...}</c>, replies <c>{"status":"success"|"error","data"?,"error":{code,message}}</c>. Aliases are percent-encoded
/// in the path. Keys are created on the device; no operation ever returns a private key.
/// </summary>
public static class CertApi
{
    public const string DiscoverPath = "config/discover";
    public const string BasePath = "config/rest/cert/v1";

    /// <summary>The key type OADM creates (user decision).</summary>
    public const string KeyType = "RSA-2048";

    /// <summary>Message of devices without the REST certificate API.</summary>
    public const string NeedsNewerFirmware = "Needs AXIS OS 11.11 or later. Nothing was changed.";

    /// <summary><c>GET /config/discover</c>: the cert v1 entry (null version when the API is not listed or discover is missing).</summary>
    public static async Task<CertApiSupport> DiscoverAsync(IVapixClient vapix, CancellationToken ct)
    {
        var answer = await DeviceHttp.SendAsync(vapix, HttpMethod.Get, DiscoverPath, null, null, ct).ConfigureAwait(false);
        if (answer.Status == HttpStatusCode.NotFound)
        {
            return new CertApiSupport(null, null);
        }

        EnsureSuccess(answer, "Read the device's API list");
        return ParseDiscover(answer.Body);
    }

    public static CertApiSupport ParseDiscover(string json)
    {
        try
        {
            var cert = JsonNode.Parse(json)?["apis"]?["cert"]?["v1"];
            return new CertApiSupport(cert?["version"]?.GetValue<string>(), cert?["state"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new CertApiSupport(null, null);
        }
    }

    public static async Task<IReadOnlyList<DeviceCertificate>> ListCertificatesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseList(await GetAsync(vapix, BasePath + "/certificates", ct).ConfigureAwait(false));

    public static async Task<IReadOnlyList<DeviceCertificate>> ListCaCertificatesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseList(await GetAsync(vapix, BasePath + "/ca_certificates", ct).ConfigureAwait(false));

    /// <summary>Creates a key pair (RSA 2048) and a self-signed placeholder certificate in the device's default keystore.</summary>
    public static Task CreateCertificateAsync(IVapixClient vapix, string alias, string subject, IReadOnlyList<string> subjectAltNames, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Post, BasePath + "/create_certificate", new JsonObject
        {
            ["alias"] = alias,
            ["key_type"] = KeyType,
            ["subject"] = subject,
            ["subject_alt_names"] = new JsonArray([.. subjectAltNames.Select(s => (JsonNode)JsonValue.Create(s)!)]),
        }, "Create key", ct);

    /// <summary>Certificate signing request (PEM) for the key of <paramref name="alias"/>.</summary>
    public static async Task<string> GetCsrAsync(IVapixClient vapix, string alias, string subject, IReadOnlyList<string> subjectAltNames, CancellationToken ct)
    {
        var data = await SendDataAsync(vapix, HttpMethod.Post, $"{BasePath}/certificates/{Escape(alias)}/get_csr", new JsonObject
        {
            ["subject"] = subject,
            ["subject_alt_names"] = new JsonArray([.. subjectAltNames.Select(s => (JsonNode)JsonValue.Create(s)!)]),
        }, "Get certificate request", ct).ConfigureAwait(false);
        var pem = data switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject obj => obj["csr"]?.GetValue<string>() ?? obj[alias]?.GetValue<string>() ?? obj.Select(p => p.Value).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var t) ? t : null).FirstOrDefault(t => t?.Contains("CERTIFICATE REQUEST", StringComparison.Ordinal) == true),
            _ => null,
        };
        return pem ?? throw new PkiDeviceException("The device sent no certificate request.");
    }

    /// <summary>Replaces the certificate of <paramref name="alias"/> with one for the same key (the CA-signed one).</summary>
    public static Task PatchCertificateAsync(IVapixClient vapix, string alias, string certificatePem, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Patch, $"{BasePath}/certificates/{Escape(alias)}", new JsonObject { ["certificate"] = certificatePem }, "Install certificate", ct);

    public static Task AddCaCertificateAsync(IVapixClient vapix, string alias, string certificatePem, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Post, BasePath + "/ca_certificates", new JsonObject { ["alias"] = alias, ["certificate"] = certificatePem }, "Install CA certificate", ct);

    /// <summary>Installs a certificate with its key from PKCS#12 (base64). The passphrase is sent in the body only, never logged.</summary>
    public static Task InstallFromPkcs12Async(IVapixClient vapix, string alias, string pkcs12Base64, string passphrase, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Post, BasePath + "/install_from_pkcs12", new JsonObject { ["alias"] = alias, ["pkcs12"] = pkcs12Base64, ["passphrase"] = passphrase }, "Install certificate", ct);

    /// <summary>Deletes a certificate and its key (securely wiped in hardware keystores).</summary>
    public static Task DeleteCertificateAsync(IVapixClient vapix, string alias, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Delete, $"{BasePath}/certificates/{Escape(alias)}", null, "Delete certificate", ct);

    public static Task DeleteCaCertificateAsync(IVapixClient vapix, string alias, CancellationToken ct) =>
        SendDataAsync(vapix, HttpMethod.Delete, $"{BasePath}/ca_certificates/{Escape(alias)}", null, "Delete CA certificate", ct);

    /// <summary>Percent-encodes an alias for the URL path (spaces, parentheses, slashes).</summary>
    public static string Escape(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        return Uri.EscapeDataString(alias).Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
    }

    public static IReadOnlyList<DeviceCertificate> ParseList(JsonNode? data)
    {
        var result = new List<DeviceCertificate>();
        if (data is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var alias = item["alias"]?.GetValue<string>();
                var pem = item["certificate"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(alias) && !string.IsNullOrEmpty(pem))
                {
                    result.Add(new DeviceCertificate(alias, pem, item["keystore"]?.GetValue<string>()));
                }
            }
        }

        return result;
    }

    /// <summary>The "data" of a successful reply; throws <see cref="PkiDeviceException"/> with the device's error text.</summary>
    public static JsonNode? ReadData(DeviceAnswer answer, string action)
    {
        ArgumentNullException.ThrowIfNull(answer);
        JsonNode? root = null;
        try
        {
            root = string.IsNullOrWhiteSpace(answer.Body) ? null : JsonNode.Parse(answer.Body);
        }
        catch (JsonException)
        {
            // handled below
        }

        var status = root?["status"]?.GetValue<string>();
        if ((int)answer.Status is >= 200 and < 300 && !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
        {
            return root?["data"];
        }

        if (root?["error"] is JsonObject error)
        {
            var message = error["message"]?.GetValue<string>() ?? "Unknown error";
            var code = error["code"]?.ToJsonString();
            throw new PkiDeviceException(
                code is null ? $"{action}: the device refused: {message}" : string.Create(CultureInfo.InvariantCulture, $"{action}: the device refused: {message} (code {code})"))
            {
                HttpStatus = (int)answer.Status,
            };
        }

        throw new PkiDeviceException($"{action}: {DeviceHttp.DescribeHttp(answer.Status)}") { HttpStatus = (int)answer.Status };
    }

    private static void EnsureSuccess(DeviceAnswer answer, string action)
    {
        if ((int)answer.Status is < 200 or >= 300)
        {
            throw new PkiDeviceException($"{action}: {DeviceHttp.DescribeHttp(answer.Status)}") { HttpStatus = (int)answer.Status };
        }
    }

    private static async Task<JsonNode?> GetAsync(IVapixClient vapix, string path, CancellationToken ct) =>
        ReadData(await DeviceHttp.SendAsync(vapix, HttpMethod.Get, path, null, null, ct).ConfigureAwait(false), "Read certificates");

    private static async Task<JsonNode?> SendDataAsync(IVapixClient vapix, HttpMethod method, string path, JsonObject? data, string action, CancellationToken ct)
    {
        var body = data is null ? null : new JsonObject { ["data"] = data }.ToJsonString();
        return ReadData(await DeviceHttp.SendAsync(vapix, method, path, DeviceHttp.JsonType, body, ct).ConfigureAwait(false), action);
    }
}
