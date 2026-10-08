using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Core.Vapix;

/// <summary>
/// Reads the certificate the device's web server presents over VAPIX, for devices OADM reaches over HTTP only (a port
/// forward of port 80, HTTPS not reachable): SOAP <c>GetWebServerTlsConfiguration</c> at <c>/vapix/services</c> names the
/// certificate and its CA certificates, REST cert v1 (AXIS OS 11.11+) returns them as PEM. Read-only. Verified on 10.0.0.48
/// (AXIS OS 12.11). Without the cert API (older firmware) or HTTPS off the result is null.
/// </summary>
public static class WebServerCertificateReader
{
    public const string ServicesPath = "vapix/services";
    public const string CertBasePath = "config/rest/cert/v1";

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Web = "http://www.axis.com/vapix/ws/webserver";
    private static readonly XNamespace Cert = "http://www.axis.com/vapix/ws/cert";

    private const string GetEnvelope =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\"><SOAP-ENV:Body>" +
        "<aweb:GetWebServerTlsConfiguration xmlns:aweb=\"http://www.axis.com/vapix/ws/webserver\"/></SOAP-ENV:Body></SOAP-ENV:Envelope>";

    /// <summary>The web server certificate described like a presented one, or null when it cannot be read.</summary>
    public static async Task<CertificateInfo?> ReadAsync(VapixClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var (alias, caAliases) = await ReadAliasesAsync(client, ct).ConfigureAwait(false);
        if (alias is null)
        {
            return null;
        }

        var leafPem = await ReadPemAsync(client, "certificates", alias, ct).ConfigureAwait(false);
        if (leafPem is null)
        {
            return null;
        }

        var chain = new List<X509Certificate2>();
        foreach (var caAlias in caAliases)
        {
            if (await ReadPemAsync(client, "ca_certificates", caAlias, ct).ConfigureAwait(false) is { } caPem && TryLoad(caPem) is { } ca)
            {
                chain.Add(ca);
            }
        }

        using var leaf = TryLoad(leafPem);
        if (leaf is null)
        {
            return null;
        }

        try
        {
            var pinning = client.Pinning;
            return CertificateTrustEvaluator.Describe(
                leaf, chain, client.BaseAddress.Host, pinning?.CustomTrustRoots, pinning?.TrustAnchors?.Current.Certificates);
        }
        finally
        {
            foreach (var c in chain)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>The alias of the presented certificate and of its CA certificates from the SOAP answer; null without TLS.</summary>
    public static (string? Alias, IReadOnlyList<string> CaAliases) ParseAliases(string xml)
    {
        var document = DeviceXml.Parse(xml);
        var body = document.Root?.Element(Soap + "Body");
        var configuration = body?.Descendants(Web + "Configuration").FirstOrDefault();
        if (configuration is null)
        {
            return (null, []);
        }

        var set = configuration.Element(Web + "CertificateSet");
        IReadOnlyList<string> Ids(string name) =>
            [.. (set?.Element(Cert + name)?.Elements(Cert + "Id") ?? []).Select(e => e.Value.Trim()).Where(v => v.Length > 0)];
        var certificates = Ids("Certificates");
        return (certificates.Count > 0 ? certificates[0] : null, Ids("CACertificates"));
    }

    /// <summary>The PEM of a REST cert v1 answer (<c>{"status":"success","data":{"certificate":"-----BEGIN ..."}}</c>).</summary>
    public static string? ParsePem(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("certificate", out var pem)
            && pem.ValueKind == JsonValueKind.String
            ? pem.GetString()
            : null;
    }

    private static async Task<(string?, IReadOnlyList<string>)> ReadAliasesAsync(VapixClient client, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ServicesPath)
        {
            Content = new StringContent(GetEnvelope, Encoding.UTF8, "application/soap+xml"),
        };
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, []);
        }

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseAliases(xml);
    }

    private static async Task<string?> ReadPemAsync(VapixClient client, string collection, string alias, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{CertBasePath}/{collection}/{Uri.EscapeDataString(alias)}");
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null; // no cert API (before AXIS OS 11.11) or the alias is gone
        }

        try
        {
            return ParsePem(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static X509Certificate2? TryLoad(string pem)
    {
        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
