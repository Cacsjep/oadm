using System.Xml;
using System.Xml.Linq;

namespace Oadm.Sdk.Vapix;

/// <summary>
/// The only way OADM and its plugins parse XML that came from a device: documents of at most
/// <see cref="MaxCharacters"/> characters (1 MB), DTD processing prohibited, no external resolver.
/// Too large, a DTD or malformed XML throw <see cref="XmlException"/>.
/// </summary>
public static class DeviceXml
{
    /// <summary>Largest XML document parsed from a device (1 MB of characters).</summary>
    public const int MaxCharacters = 1024 * 1024;

    /// <summary>New reader settings with the limits (callers may add options such as <c>IgnoreComments</c>).</summary>
    public static XmlReaderSettings CreateReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharacters,
    };

    /// <summary>Parses a document from a device.</summary>
    /// <exception cref="XmlException">Malformed, larger than 1 MB or with a DTD.</exception>
    public static XDocument Parse(string xml, LoadOptions options = LoadOptions.None) => Parse(xml, CreateReaderSettings(), options);

    /// <summary>Parses a document from a device with adjusted settings; the limits are enforced again.</summary>
    /// <exception cref="XmlException">Malformed, larger than 1 MB or with a DTD.</exception>
    public static XDocument Parse(string xml, XmlReaderSettings settings, LoadOptions options = LoadOptions.None)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(settings);
        if (xml.Length > MaxCharacters)
        {
            throw new XmlException("The XML document from the device is larger than 1 MB.");
        }

        var safe = settings.Clone();
        safe.DtdProcessing = DtdProcessing.Prohibit;
        safe.XmlResolver = null;
        safe.MaxCharactersInDocument = MaxCharacters;
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, safe);
        return XDocument.Load(reader, options);
    }

    /// <summary>The root element of a document from a device.</summary>
    /// <exception cref="XmlException">Malformed, larger than 1 MB, with a DTD or without a root element.</exception>
    public static XElement ParseElement(string xml, LoadOptions options = LoadOptions.None) =>
        Parse(xml, options).Root ?? throw new XmlException("The XML document from the device has no root element.");
}
