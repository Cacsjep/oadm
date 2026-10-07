using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>Connection policy values of <c>aweb:ConnectionPolicies/aweb:Admin</c>.</summary>
public static class ConnectionPolicy
{
    /// <summary>HTTP only ("Http": inferred, no schema is published; to be verified on hardware).</summary>
    public const string Http = "Http";

    public const string HttpAndHttps = "HttpAndHttps";

    /// <summary>HTTPS only.</summary>
    public const string Https = "Https";

    public static bool IsHttpOnly(string? policy) => string.Equals(policy, Http, StringComparison.OrdinalIgnoreCase);

    /// <summary>"HTTP only", "HTTP and HTTPS", "HTTPS only".</summary>
    public static string Describe(string? policy) => policy switch
    {
        _ when string.Equals(policy, Http, StringComparison.OrdinalIgnoreCase) => "HTTP only",
        _ when string.Equals(policy, HttpAndHttps, StringComparison.OrdinalIgnoreCase) => "HTTP and HTTPS",
        _ when string.Equals(policy, Https, StringComparison.OrdinalIgnoreCase) => "HTTPS only",
        null or "" => "unknown connection policy",
        _ => policy!,
    };
}

/// <summary>The web server TLS configuration as <c>aweb:GetWebServerTlsConfiguration</c> reports it.</summary>
public sealed record WebServerTlsConfiguration(
    bool Tls,
    string? Policy,
    IReadOnlyList<string> Ciphers,
    IReadOnlyList<string> Certificates,
    IReadOnlyList<string> CaCertificates,
    IReadOnlyList<string> TrustedCertificates)
{
    /// <summary>The alias of the certificate the web server presents (first entry).</summary>
    public string? CertificateAlias => Certificates.Count > 0 ? Certificates[0] : null;
}

/// <summary>
/// SOAP web server service (<c>http://www.axis.com/vapix/ws/webserver</c>) at <c>POST /vapix/services</c>, SOAP 1.2. There is no
/// REST equivalent on AXIS OS 12.11. The set request sends back the cipher list just read (it changes with AXIS OS updates).
/// </summary>
public static class WebServerTls
{
    public const string ServicesPath = "vapix/services";
    public const string SoapNs = "http://www.w3.org/2003/05/soap-envelope";
    public const string WebNs = "http://www.axis.com/vapix/ws/webserver";
    public const string CertNs = "http://www.axis.com/vapix/ws/cert";

    private static readonly XNamespace Web = WebNs;
    private static readonly XNamespace Cert = CertNs;
    private static readonly XNamespace Soap = SoapNs;

    public static async Task<WebServerTlsConfiguration> GetAsync(IVapixClient vapix, CancellationToken ct)
    {
        var body = $"<aweb:GetWebServerTlsConfiguration xmlns:aweb=\"{WebNs}\"/>";
        var answer = await SendAsync(vapix, body, "Read web server settings", ct).ConfigureAwait(false);
        return Parse(answer);
    }

    /// <summary>Sets certificate and connection policy; everything else as read (<paramref name="current"/>).</summary>
    public static async Task SetAsync(IVapixClient vapix, WebServerTlsConfiguration configuration, CancellationToken ct)
    {
        var answer = await SendAsync(vapix, BuildSet(configuration), "Set web server settings", ct).ConfigureAwait(false);
        _ = Body(answer, "Set web server settings");
    }

    /// <summary>The SetWebServerTlsConfiguration body (Axis doc layout).</summary>
    public static string BuildSet(WebServerTlsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = new StringBuilder();
        builder.Append($"<aweb:SetWebServerTlsConfiguration xmlns:aweb=\"{WebNs}\" xmlns:acert=\"{CertNs}\"><Configuration>");
        builder.Append("<Tls>").Append(configuration.Tls ? "true" : "false").Append("</Tls>");
        builder.Append("<aweb:ConnectionPolicies><aweb:Admin>").Append(Escape(configuration.Policy ?? ConnectionPolicy.HttpAndHttps)).Append("</aweb:Admin></aweb:ConnectionPolicies>");
        builder.Append("<aweb:Ciphers>");
        foreach (var cipher in configuration.Ciphers)
        {
            builder.Append("<acert:Cipher>").Append(Escape(cipher)).Append("</acert:Cipher>");
        }

        builder.Append("</aweb:Ciphers><aweb:CertificateSet>");
        AppendIds(builder, "Certificates", configuration.Certificates);
        AppendIds(builder, "CACertificates", configuration.CaCertificates);
        AppendIds(builder, "TrustedCertificates", configuration.TrustedCertificates);
        builder.Append("</aweb:CertificateSet></Configuration></aweb:SetWebServerTlsConfiguration>");
        return builder.ToString();
    }

    public static WebServerTlsConfiguration Parse(string xml)
    {
        var body = Body(xml, "Read web server settings");
        var configuration = body.Descendants(Web + "Configuration").FirstOrDefault()
            ?? throw new PkiDeviceException("Read web server settings: the device sent no configuration.");
        var set = configuration.Element(Web + "CertificateSet");
        IReadOnlyList<string> Ids(string name) =>
            [.. (set?.Element(Cert + name)?.Elements(Cert + "Id") ?? []).Select(e => e.Value.Trim()).Where(v => v.Length > 0)];
        return new WebServerTlsConfiguration(
            string.Equals(Child(configuration, "Tls")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            configuration.Element(Web + "ConnectionPolicies")?.Element(Web + "Admin")?.Value.Trim(),
            [.. (configuration.Element(Web + "Ciphers")?.Elements(Cert + "Cipher") ?? []).Select(e => e.Value.Trim()).Where(v => v.Length > 0)],
            Ids("Certificates"),
            Ids("CACertificates"),
            Ids("TrustedCertificates"));
    }

    /// <summary>The SOAP body element; a fault becomes a <see cref="PkiDeviceException"/> with its reason text.</summary>
    public static XElement Body(string xml, string action)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new PkiDeviceException($"{action}: the device sent an unreadable answer: {DeviceHttp.FirstLine(xml)}", ex);
        }

        var body = document.Root?.Element(Soap + "Body") ?? throw new PkiDeviceException($"{action}: the device sent no SOAP body.");
        if (body.Element(Soap + "Fault") is { } fault)
        {
            var reason = fault.Element(Soap + "Reason")?.Elements(Soap + "Text").FirstOrDefault()?.Value.Trim();
            var subcode = fault.Element(Soap + "Code")?.Element(Soap + "Subcode")?.Element(Soap + "Value")?.Value.Trim();
            throw new PkiDeviceException($"{action}: the device refused: {reason ?? "SOAP fault"}{(subcode is null ? string.Empty : $" ({subcode})")}");
        }

        return body;
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static void AppendIds(StringBuilder builder, string name, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            builder.Append("<acert:").Append(name).Append("/>");
            return;
        }

        builder.Append("<acert:").Append(name).Append('>');
        foreach (var id in ids)
        {
            builder.Append("<acert:Id>").Append(Escape(id)).Append("</acert:Id>");
        }

        builder.Append("</acert:").Append(name).Append('>');
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

    private static async Task<string> SendAsync(IVapixClient vapix, string bodyContent, string action, CancellationToken ct)
    {
        var envelope = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><SOAP-ENV:Envelope xmlns:SOAP-ENV=\"{SoapNs}\"><SOAP-ENV:Body>{bodyContent}</SOAP-ENV:Body></SOAP-ENV:Envelope>";
        var answer = await DeviceHttp.SendAsync(vapix, HttpMethod.Post, ServicesPath, DeviceHttp.SoapType, envelope, ct).ConfigureAwait(false);
        if ((int)answer.Status is < 200 or >= 300 && !answer.Body.Contains("Fault", StringComparison.Ordinal))
        {
            throw new PkiDeviceException($"{action}: {DeviceHttp.DescribeHttp(answer.Status)}") { HttpStatus = (int)answer.Status };
        }

        return answer.Body;
    }
}
