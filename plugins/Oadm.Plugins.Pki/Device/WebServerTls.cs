using System.Text;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>Connection policy values of <c>aweb:ConnectionPolicies/aweb:Admin</c>.</summary>
public static class ConnectionPolicy
{
    /// <summary>HTTP only (seen on AXIS OS 6.53 and 11.9: "Http").</summary>
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

    /// <summary>The policy Enable HTTPS sets: HTTP only (or none) becomes HTTP and HTTPS, any other stays.</summary>
    public static string Enabled(string? policy) => IsHttpOnly(policy) || string.IsNullOrEmpty(policy) ? HttpAndHttps : policy;
}

/// <summary>
/// The web server TLS configuration as <c>aweb:GetWebServerTlsConfiguration</c> reports it. <see cref="Policy"/> is the
/// Administrator's connection policy; <see cref="OperatorPolicy"/> and <see cref="ViewerPolicy"/> are set when the device
/// reports them (AXIS OS before 7.10 needs them in the set request as well, else <c>ter:InvalidArgs</c> "occurrence
/// violation in element aweb:ConnectionPolicies").
/// </summary>
public sealed record WebServerTlsConfiguration(
    bool Tls,
    string? Policy,
    IReadOnlyList<string> Ciphers,
    IReadOnlyList<string> Certificates,
    IReadOnlyList<string> CaCertificates,
    IReadOnlyList<string> TrustedCertificates,
    string? OperatorPolicy = null,
    string? ViewerPolicy = null)
{
    /// <summary>The alias of the certificate the web server presents (first entry).</summary>
    public string? CertificateAlias => Certificates.Count > 0 ? Certificates[0] : null;

    /// <summary>The set request names the Operator and Viewer policies too.</summary>
    public bool HasRoles => OperatorPolicy is not null || ViewerPolicy is not null;

    /// <summary>With Operator and Viewer policies (the Administrator's where the device reported none).</summary>
    public WebServerTlsConfiguration WithRoles() => this with
    {
        OperatorPolicy = OperatorPolicy ?? Policy ?? ConnectionPolicy.HttpAndHttps,
        ViewerPolicy = ViewerPolicy ?? Policy ?? ConnectionPolicy.HttpAndHttps,
    };

    /// <summary>Every policy as <paramref name="change"/> makes it (Operator and Viewer only when present).</summary>
    public WebServerTlsConfiguration WithPolicies(Func<string?, string> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return this with
        {
            Policy = change(Policy),
            OperatorPolicy = OperatorPolicy is null ? null : change(OperatorPolicy),
            ViewerPolicy = ViewerPolicy is null ? null : change(ViewerPolicy),
        };
    }
}

/// <summary>
/// SOAP web server service (<c>http://www.axis.com/vapix/ws/webserver</c>) at <c>POST /vapix/services</c>, SOAP 1.2, the same
/// on AXIS OS 6.53 to 12.x (no REST equivalent on 12.11). The set request sends back the cipher list just read (it changes
/// with AXIS OS updates).
/// </summary>
public static class WebServerTls
{
    public const string ServicesPath = DeviceSoap.ServicesPath;
    public const string SoapNs = DeviceSoap.SoapNs;
    public const string WebNs = DeviceSoap.WebNs;
    public const string CertNs = DeviceSoap.CertNs;

    private static readonly XNamespace Web = WebNs;
    private static readonly XNamespace Cert = CertNs;

    public static async Task<WebServerTlsConfiguration> GetAsync(IVapixClient vapix, CancellationToken ct)
    {
        var body = $"<aweb:GetWebServerTlsConfiguration xmlns:aweb=\"{WebNs}\"/>";
        return Parse(await DeviceSoap.SendAsync(vapix, body, "Read web server settings", ct).ConfigureAwait(false));
    }

    /// <summary>Sets certificate and connection policy; everything else as read.</summary>
    public static async Task SetAsync(IVapixClient vapix, WebServerTlsConfiguration configuration, CancellationToken ct) =>
        _ = await DeviceSoap.SendAsync(vapix, BuildSet(configuration), "Set web server settings", ct).ConfigureAwait(false);

    /// <summary>
    /// The SetWebServerTlsConfiguration body. Every element is namespace qualified like the device's own Get answer
    /// (aweb:Configuration name="WebServer", aweb:Tls): AXIS OS 12.11 refuses unqualified ones with ter:TagMismatch. Operator
    /// and Viewer follow Admin when <see cref="WebServerTlsConfiguration.HasRoles"/>.
    /// </summary>
    public static string BuildSet(WebServerTlsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = new StringBuilder();
        builder.Append($"<aweb:SetWebServerTlsConfiguration xmlns:aweb=\"{WebNs}\" xmlns:acert=\"{CertNs}\"><aweb:Configuration name=\"WebServer\">");
        builder.Append("<aweb:Tls>").Append(configuration.Tls ? "true" : "false").Append("</aweb:Tls>");
        builder.Append("<aweb:ConnectionPolicies><aweb:Admin>").Append(Escape(configuration.Policy ?? ConnectionPolicy.HttpAndHttps)).Append("</aweb:Admin>");
        if (configuration.HasRoles)
        {
            var roles = configuration.WithRoles();
            builder.Append("<aweb:Operator>").Append(Escape(roles.OperatorPolicy!)).Append("</aweb:Operator>");
            builder.Append("<aweb:Viewer>").Append(Escape(roles.ViewerPolicy!)).Append("</aweb:Viewer>");
        }

        builder.Append("</aweb:ConnectionPolicies>");
        builder.Append("<aweb:Ciphers>");
        foreach (var cipher in configuration.Ciphers)
        {
            builder.Append("<acert:Cipher>").Append(Escape(cipher)).Append("</acert:Cipher>");
        }

        builder.Append("</aweb:Ciphers><aweb:CertificateSet>");
        AppendIds(builder, "Certificates", configuration.Certificates);
        AppendIds(builder, "CACertificates", configuration.CaCertificates);
        AppendIds(builder, "TrustedCertificates", configuration.TrustedCertificates);
        builder.Append("</aweb:CertificateSet></aweb:Configuration></aweb:SetWebServerTlsConfiguration>");
        return builder.ToString();
    }

    public static WebServerTlsConfiguration Parse(string xml) => Parse(Body(xml, "Read web server settings"));

    /// <summary>The SOAP body element; a fault becomes a <see cref="PkiDeviceException"/> with its reason text.</summary>
    public static XElement Body(string xml, string action) => DeviceSoap.Body(xml, action);

    private static WebServerTlsConfiguration Parse(XElement body)
    {
        var configuration = body.Descendants(Web + "Configuration").FirstOrDefault()
            ?? throw new PkiDeviceException("Read web server settings: the device sent no configuration.");
        var set = configuration.Element(Web + "CertificateSet");
        IReadOnlyList<string> Ids(string name) =>
            [.. (set?.Element(Cert + name)?.Elements(Cert + "Id") ?? []).Select(e => e.Value.Trim()).Where(v => v.Length > 0)];
        var policies = configuration.Element(Web + "ConnectionPolicies");
        string? Role(string name) => policies?.Element(Web + name)?.Value.Trim() is { Length: > 0 } value ? value : null;
        return new WebServerTlsConfiguration(
            string.Equals(Child(configuration, "Tls")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            Role("Admin"),
            [.. (configuration.Element(Web + "Ciphers")?.Elements(Cert + "Cipher") ?? []).Select(e => e.Value.Trim()).Where(v => v.Length > 0)],
            Ids("Certificates"),
            Ids("CACertificates"),
            Ids("TrustedCertificates"),
            Role("Operator"),
            Role("Viewer"));
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

    private static string Escape(string text) => DeviceSoap.Escape(text);
}
