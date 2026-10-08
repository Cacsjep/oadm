using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.MetadataMonitor.Parsing;

/// <summary>A parsed notification before it gets its sequence number.</summary>
internal sealed record ParsedMessage(
    DateTimeOffset? UtcTime,
    string Category,
    string Topic,
    string? Operation,
    IReadOnlyList<MetadataItem> Items,
    string Info,
    string Xml)
{
    public MetadataMessage WithSeq(long seq, DateTimeOffset captureUtc) =>
        new(seq, UtcTime, Category, Topic, captureUtc, Operation, Info, Xml);
}

/// <summary>
/// Turns one <c>tt:MetadataStream</c> document into messages: every <c>wsnt:NotificationMessage</c> becomes one message
/// (topic with resolved prefixes, UtcTime, PropertyOperation, Source / Key / Data SimpleItems in document order, the
/// notification's XML). Malformed XML becomes one message of category Invalid with the raw text. No DTDs, no external
/// resources.
/// </summary>
internal static class MetadataParser
{
    public static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";
    public static readonly XNamespace Wsnt = "http://docs.oasis-open.org/wsn/b-2";

    /// <summary>ONVIF and Axis topic namespaces: their prefixes are dropped from the tree text.</summary>
    public static readonly IReadOnlySet<string> KnownTopicNamespaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "http://www.onvif.org/ver10/topics",
        "http://www.axis.com/2009/event/topics",
    };

    private static readonly XmlReaderSettings ReaderSettings = CreateReaderSettings();

    private static XmlReaderSettings CreateReaderSettings()
    {
        var settings = DeviceXml.CreateReaderSettings();
        settings.IgnoreComments = true;
        return settings;
    }

    public static IReadOnlyList<ParsedMessage> Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            document = DeviceXml.Parse(xml, ReaderSettings);
        }
        catch (XmlException ex)
        {
            return [new ParsedMessage(null, MessageCategories.Invalid, string.Empty, null, [], "Malformed XML: " + ex.Message, Cut(xml))];
        }

        var result = new List<ParsedMessage>();
        foreach (var notification in document.Descendants(Wsnt + "NotificationMessage"))
        {
            result.Add(ParseNotification(notification));
        }

        return result;
    }

    /// <summary>"Device/IO/VirtualInput" from "tns1:Device/tnsaxis:IO/VirtualInput" (prefixes resolved on <paramref name="scope"/>).</summary>
    public static string TopicText(string? expression, XElement scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return string.Empty;
        }

        var parts = expression.Trim().Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var ns = scope.GetNamespaceOfPrefix(part[..colon]);
            if (ns is not null && KnownTopicNamespaces.Contains(ns.NamespaceName))
            {
                parts[i] = part[(colon + 1)..];
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>"[INIT] port = 33; active = 0;"</summary>
    public static string InfoText(string? operation, IEnumerable<MetadataItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        var tag = operation switch
        {
            "Initialized" => "[INIT]",
            "Changed" => "[CHANGED]",
            "Deleted" => "[DELETED]",
            null or "" => null,
            _ => "[" + operation.ToUpperInvariant() + "]",
        };
        if (tag is not null)
        {
            sb.Append(tag);
        }

        foreach (var item in items)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(item.Name).Append(" = ").Append(item.Value).Append(';');
        }

        return sb.ToString();
    }

    private static ParsedMessage ParseNotification(XElement notification)
    {
        var topicElement = notification.Element(Wsnt + "Topic");
        var topic = TopicText(topicElement?.Value, topicElement ?? notification);
        var message = notification.Element(Wsnt + "Message")?.Element(Tt + "Message");
        DateTimeOffset? utc = null;
        string? operation = null;
        var items = new List<MetadataItem>();
        if (message is not null)
        {
            if (DateTimeOffset.TryParse(
                    (string?)message.Attribute("UtcTime"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var time))
            {
                utc = time;
            }

            operation = (string?)message.Attribute("PropertyOperation");
            foreach (var group in new[] { "Source", "Key", "Data" })
            {
                if (message.Element(Tt + group) is not { } element)
                {
                    continue;
                }

                foreach (var item in element.Elements(Tt + "SimpleItem"))
                {
                    items.Add(new MetadataItem((string?)item.Attribute("Name") ?? string.Empty, (string?)item.Attribute("Value") ?? string.Empty));
                }
            }
        }

        return new ParsedMessage(
            utc,
            MessageCategories.Event,
            topic,
            string.IsNullOrEmpty(operation) ? null : operation,
            items,
            InfoText(operation, items),
            Cut(notification.ToString(SaveOptions.DisableFormatting)));
    }

    private static string Cut(string xml) =>
        xml.Length > MetadataMonitorPluginInfo.MaxXmlCharacters ? xml[..MetadataMonitorPluginInfo.MaxXmlCharacters] : xml;
}
