using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>
/// SOAP 1.2 at <c>POST /vapix/services</c> (AXIS OS 6.53 to 12.x): the web server TLS service and the certificate services
/// (<see cref="CertSoapApi"/>). Answers are parsed with <see cref="DeviceXml"/> (no DTD, at most 1 MB); a fault becomes a
/// <see cref="PkiDeviceException"/> with the device's reason.
/// </summary>
public static class DeviceSoap
{
    public const string ServicesPath = "vapix/services";
    public const string SoapNs = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>ONVIF device service (<c>tds</c>): certificates, CA certificates, delete.</summary>
    public const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";

    /// <summary>ONVIF schema (<c>tt</c>).</summary>
    public const string SchemaNs = "http://www.onvif.org/ver10/schema";

    /// <summary>Axis certificate service (<c>acertificates</c>): keys, requests, PKCS#12, certificate sets.</summary>
    public const string CertificatesNs = "http://www.axis.com/vapix/ws/certificates";

    /// <summary>Axis certificate types (<c>acert</c>).</summary>
    public const string CertNs = "http://www.axis.com/vapix/ws/cert";

    /// <summary>Axis web server service (<c>aweb</c>).</summary>
    public const string WebNs = "http://www.axis.com/vapix/ws/webserver";

    private static readonly XNamespace Soap = SoapNs;

    /// <summary>The envelope with every prefix the request bodies use declared on it.</summary>
    public static string Envelope(string bodyContent) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><SOAP-ENV:Envelope xmlns:SOAP-ENV=\"{SoapNs}\" xmlns:tds=\"{DeviceNs}\" xmlns:tt=\"{SchemaNs}\" xmlns:acertificates=\"{CertificatesNs}\" xmlns:acert=\"{CertNs}\" xmlns:aweb=\"{WebNs}\"><SOAP-ENV:Body>{bodyContent}</SOAP-ENV:Body></SOAP-ENV:Envelope>";

    /// <summary>Sends <paramref name="bodyContent"/> and returns the answer's SOAP body; a fault or an HTTP error throws.</summary>
    public static async Task<XElement> SendAsync(IVapixClient vapix, string bodyContent, string action, CancellationToken ct)
    {
        var answer = await DeviceHttp.SendAsync(vapix, HttpMethod.Post, ServicesPath, DeviceHttp.SoapType, Envelope(bodyContent), ct).ConfigureAwait(false);
        var failed = (int)answer.Status is < 200 or >= 300;
        if (failed && !answer.Body.Contains("Fault", StringComparison.Ordinal))
        {
            throw new PkiDeviceException($"{action}: {DeviceHttp.DescribeHttp(answer.Status)}") { HttpStatus = (int)answer.Status };
        }

        // A SOAP fault throws with the device's reason; an HTTP error without a fault element is never a success.
        var body = Body(answer.Body, action);
        return failed
            ? throw new PkiDeviceException($"{action}: {DeviceHttp.DescribeHttp(answer.Status)}") { HttpStatus = (int)answer.Status }
            : body;
    }

    /// <summary>The SOAP body element; a fault becomes a <see cref="PkiDeviceException"/> with its reason text.</summary>
    public static XElement Body(string xml, string action)
    {
        XDocument document;
        try
        {
            document = DeviceXml.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
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

    /// <summary>XML text escaping for element content.</summary>
    public static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? string.Empty;
}
