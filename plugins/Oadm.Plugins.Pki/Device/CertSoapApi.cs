using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>A certificate set of the SOAP certificate service (<c>acert:CertificateSet</c>): ids only.</summary>
public sealed record CertSet(IReadOnlyList<string> Certificates, IReadOnlyList<string> CaCertificates, IReadOnlyList<string> TrustedCertificates)
{
    public static CertSet Empty { get; } = new([], [], []);
}

/// <summary>
/// The SOAP certificate operations at <c>POST /vapix/services</c> for devices without the released REST certificate API
/// (AXIS OS 6.53 to 11.11.134; research of 2026-10-25 on 6.53, 8.40, 9.80, 10.12, 11.9 and 11.11, fixtures in
/// <c>tests/Oadm.Plugins.Pki.Tests/Fixtures/Legacy</c>): the ONVIF device service (<c>tds</c>) lists, loads and deletes
/// certificates and CA certificates (DER in base64); the Axis certificate service (<c>acertificates</c>) creates a key with a
/// self-signed placeholder (RSA 2048, the service takes no key type), makes the certificate request for it, loads PKCS#12
/// and reads and writes the certificate sets (<c>EAPTLS_WIRED</c>: the 802.1X client certificate and CA certificates). A
/// signed certificate is loaded under a new id (the device finds its key; the id of the key is refused as existing) and the
/// placeholder is deleted after it; the key stays. No operation returns a private key.
/// </summary>
public static class CertSoapApi
{
    /// <summary>The certificate set of wired IEEE 802.1X (EAP-TLS).</summary>
    public const string WiredDot1xSet = "EAPTLS_WIRED";

    /// <summary>The certificate set of the web server.</summary>
    public const string WebServerSet = "WebServer";

    private static readonly XNamespace Tds = DeviceSoap.DeviceNs;
    private static readonly XNamespace Tt = DeviceSoap.SchemaNs;
    private static readonly XNamespace Certificates = DeviceSoap.CertificatesNs;
    private static readonly XNamespace Cert = DeviceSoap.CertNs;

    public static async Task<IReadOnlyList<DeviceCertificate>> ListCertificatesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseCertificates(await DeviceSoap.SendAsync(vapix, "<tds:GetCertificates/>", "Read certificates", ct).ConfigureAwait(false), "NvtCertificate");

    public static async Task<IReadOnlyList<DeviceCertificate>> ListCaCertificatesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseCertificates(await DeviceSoap.SendAsync(vapix, "<tds:GetCACertificates/>", "Read CA certificates", ct).ConfigureAwait(false), "CACertificate");

    /// <summary>A new RSA key with a self-signed placeholder certificate under <paramref name="id"/>.</summary>
    public static Task CreateCertificateAsync(IVapixClient vapix, string id, string commonName, DateTimeOffset notBefore, DateTimeOffset notAfter, CancellationToken ct) =>
        DeviceSoap.SendAsync(vapix, BuildCreate(id, commonName, notBefore, notAfter), "Create key", ct);

    /// <summary>The certificate request (PEM) for the key of <paramref name="id"/>.</summary>
    public static async Task<string> GetCsrAsync(IVapixClient vapix, string id, string commonName, CancellationToken ct) =>
        ParseCsr(await DeviceSoap.SendAsync(vapix, BuildCsr(id, commonName), "Get certificate request", ct).ConfigureAwait(false));

    /// <summary>Loads a certificate for a key the device made, under a new <paramref name="id"/>.</summary>
    public static Task LoadCertificateAsync(IVapixClient vapix, string id, string pem, CancellationToken ct) =>
        DeviceSoap.SendAsync(vapix, BuildLoad("LoadCertificates", "NVTCertificate", id, pem), "Install certificate", ct);

    public static Task LoadCaCertificateAsync(IVapixClient vapix, string id, string pem, CancellationToken ct) =>
        DeviceSoap.SendAsync(vapix, BuildLoad("LoadCACertificates", "CACertificate", id, pem), "Install CA certificate", ct);

    /// <summary>Loads a certificate with its key from PKCS#12 (base64); the password is sent in the body only, never logged. Returns the id.</summary>
    public static async Task<string> LoadPkcs12Async(IVapixClient vapix, string id, string pkcs12Base64, string password, CancellationToken ct)
    {
        var body = await DeviceSoap.SendAsync(vapix, BuildPkcs12(id, pkcs12Base64, password), "Install certificate", ct).ConfigureAwait(false);
        return body.Descendants(Certificates + "Id").FirstOrDefault()?.Value.Trim() is { Length: > 0 } given ? given : id;
    }

    /// <summary>Deletes a certificate (with its key) or a CA certificate; the device refuses one in use.</summary>
    public static Task DeleteAsync(IVapixClient vapix, string id, string action, CancellationToken ct) =>
        DeviceSoap.SendAsync(vapix, BuildDelete(id), action, ct);

    public static string BuildDelete(string id) => $"<tds:DeleteCertificates><tds:CertificateID>{DeviceSoap.Escape(id)}</tds:CertificateID></tds:DeleteCertificates>";

    public static string BuildGetCertSet(string name) =>
        $"<acertificates:GetCertSet><acertificates:CertSetName>{DeviceSoap.Escape(name)}</acertificates:CertSetName></acertificates:GetCertSet>";

    public static async Task<CertSet> GetCertSetAsync(IVapixClient vapix, string name, CancellationToken ct) =>
        ParseCertSet(await DeviceSoap.SendAsync(vapix, BuildGetCertSet(name), "Read certificate settings", ct).ConfigureAwait(false));

    public static Task SetCertSetAsync(IVapixClient vapix, string name, CertSet set, string action, CancellationToken ct) =>
        DeviceSoap.SendAsync(vapix, BuildSetCertSet(name, set), action, ct);

    /// <summary>The CreateCertificate2 body (dates without a zone, like the recorded requests).</summary>
    public static string BuildCreate(string id, string commonName, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        $"<acertificates:CreateCertificate2><acertificates:Id>{DeviceSoap.Escape(id)}</acertificates:Id><acertificates:Subject><acert:CN>{DeviceSoap.Escape(commonName)}</acert:CN></acertificates:Subject>"
        + $"<acertificates:ValidNotBefore>{Date(notBefore)}</acertificates:ValidNotBefore><acertificates:ValidNotAfter>{Date(notAfter)}</acertificates:ValidNotAfter></acertificates:CreateCertificate2>";

    public static string BuildCsr(string id, string commonName) =>
        $"<acertificates:GetPkcs10Request2><acertificates:Id>{DeviceSoap.Escape(id)}</acertificates:Id><acertificates:Subject><acert:CN>{DeviceSoap.Escape(commonName)}</acert:CN></acertificates:Subject></acertificates:GetPkcs10Request2>";

    /// <summary>LoadCertificates / LoadCACertificates with one certificate as base64 DER.</summary>
    public static string BuildLoad(string operation, string element, string id, string pem) =>
        $"<tds:{operation}><tds:{element}><tt:CertificateID>{DeviceSoap.Escape(id)}</tt:CertificateID><tt:Certificate><tt:Data>{Convert.ToBase64String(Der(pem))}</tt:Data></tt:Certificate></tds:{element}></tds:{operation}>";

    public static string BuildPkcs12(string id, string pkcs12Base64, string password) =>
        $"<acertificates:LoadPkcs12><acertificates:Pkcs12><acert:Id>{DeviceSoap.Escape(id)}</acert:Id><acert:Certificate>{DeviceSoap.Escape(pkcs12Base64)}</acert:Certificate><acert:Password>{DeviceSoap.Escape(password)}</acert:Password></acertificates:Pkcs12></acertificates:LoadPkcs12>";

    public static string BuildSetCertSet(string name, CertSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var builder = new StringBuilder("<acertificates:SetCertSet><acertificates:CertSetName>").Append(DeviceSoap.Escape(name)).Append("</acertificates:CertSetName><acertificates:CertSet>");
        AppendIds(builder, "Certificates", set.Certificates);
        AppendIds(builder, "CACertificates", set.CaCertificates);
        AppendIds(builder, "TrustedCertificates", set.TrustedCertificates);
        return builder.Append("</acertificates:CertSet></acertificates:SetCertSet>").ToString();
    }

    /// <summary>The certificates of a GetCertificates (<c>NvtCertificate</c>) or GetCACertificates (<c>CACertificate</c>) answer as PEM.</summary>
    public static IReadOnlyList<DeviceCertificate> ParseCertificates(XElement body, string element)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = new List<DeviceCertificate>();
        foreach (var item in body.Descendants(Tds + element))
        {
            var id = item.Element(Tt + "CertificateID")?.Value.Trim();
            var data = item.Element(Tt + "Certificate")?.Element(Tt + "Data")?.Value;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            // An entry that cannot be decoded keeps its id (it can still be in use or deleted); its PEM stays empty.
            result.Add(new DeviceCertificate(id, Pem("CERTIFICATE", data) ?? string.Empty));
        }

        return result;
    }

    public static IReadOnlyList<DeviceCertificate> ParseCertificates(string xml, string element) => ParseCertificates(DeviceSoap.Body(xml, "Read certificates"), element);

    public static string ParseCsr(XElement body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var data = body.Descendants(Certificates + "Pkcs10Request").FirstOrDefault()?.Value;
        return Pem("CERTIFICATE REQUEST", data) ?? throw new PkiDeviceException("The device sent no certificate request.");
    }

    public static string ParseCsr(string xml) => ParseCsr(DeviceSoap.Body(xml, "Get certificate request"));

    public static CertSet ParseCertSet(XElement body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var set = body.Descendants(Certificates + "CertSet").FirstOrDefault();
        IReadOnlyList<string> Ids(string name) =>
            [.. (set?.Element(Cert + name)?.Elements(Cert + "Id") ?? []).Select(e => e.Value.Trim()).Where(v => v.Length > 0)];
        return set is null ? CertSet.Empty : new CertSet(Ids("Certificates"), Ids("CACertificates"), Ids("TrustedCertificates"));
    }

    public static CertSet ParseCertSet(string xml) => ParseCertSet(DeviceSoap.Body(xml, "Read certificate settings"));

    /// <summary>The DER of a PEM certificate.</summary>
    public static byte[] Der(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var fields = PemEncoding.Find(pem);
        return Convert.FromBase64String(pem[fields.Base64Data]);
    }

    private static string? Pem(string label, string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var der = Convert.FromBase64String(string.Concat(base64.Where(c => !char.IsWhiteSpace(c))));
            return new string(PemEncoding.Write(label, der)) + "\n";
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Date(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

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
            builder.Append("<acert:Id>").Append(DeviceSoap.Escape(id)).Append("</acert:Id>");
        }

        builder.Append("</acert:").Append(name).Append('>');
    }
}
