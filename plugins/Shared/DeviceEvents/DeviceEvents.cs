using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceEvents;

/// <summary>
/// The events a device offers: <c>aev:GetEventInstances</c> of the event service (read-only). Verified on an AXIS
/// P3265-V with AXIS OS 12.11: a <c>wstop:TopicSet</c> tree whose leaves hold one <c>aev:MessageInstance</c>
/// (<c>aev:isProperty="true"</c> = on/off) with Source / Key / Data instances; the data item with
/// <c>isPropertyState="true"</c> is the state. Apps add their events only while they run. Topics marked
/// <c>isApplicationData="true"</c> (on the topic or a parent: internal data such as Object Analytics drawing frames, ONVIF
/// duplicates of the I/O events) are left out, like the device's own event rule page does. Shared as source by Device
/// triggers and the Action Rule Editor.
/// </summary>
public static class EventDeclarations
{
    public const string ServicePath = "/vapix/services";

    private const string SoapAction = "http://www.axis.com/vapix/ws/event1/GetEventInstances";

    private static readonly XNamespace Aev = "http://www.axis.com/vapix/ws/event1";
    private static readonly XNamespace Wstop = "http://docs.oasis-open.org/wsn/t-1";

    /// <summary>Topic prefixes as the event stream writes them.</summary>
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.Ordinal)
    {
        ["http://www.onvif.org/ver10/topics"] = "tns1",
        ["http://www.axis.com/2009/event/topics"] = "tnsaxis",
    };

    public static string RequestBody =>
        """<?xml version="1.0" encoding="UTF-8"?><soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:m="http://www.axis.com/vapix/ws/event1"><soap:Body><m:GetEventInstances/></soap:Body></soap:Envelope>""";

    /// <summary>Reads the device's events. Throws <see cref="InvalidOperationException"/> with a user text when it cannot.</summary>
    public static async Task<IReadOnlyList<DeviceEvent>> ReadAsync(IVapixClient vapix, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var request = new HttpRequestMessage(HttpMethod.Post, ServicePath)
        {
            Content = new StringContent(RequestBody, Encoding.UTF8),
        };
        request.Content.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", $"application/soap+xml; action={SoapAction}; charset=utf-8");
        request.Options.Set(VapixRequestOptions.Timeout, timeout);
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
        }

        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Every event of a GetEventInstances answer, in document order. Internal app data is left out.</summary>
    public static IReadOnlyList<DeviceEvent> Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = DeviceXml.Parse(xml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            throw new InvalidOperationException("The device sent an event list OADM cannot read.", ex);
        }

        return Parse(doc);
    }

    /// <summary>Every event of a GetEventInstances answer read already.</summary>
    public static IReadOnlyList<DeviceEvent> Parse(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var topicSet = doc.Descendants(Wstop + "TopicSet").FirstOrDefault()
            ?? throw new InvalidOperationException("The device sent no event list.");
        var events = new List<DeviceEvent>();
        Walk(topicSet, [], [], false, events);
        return events;
    }

    private static void Walk(XElement element, List<string> path, List<string> names, bool applicationData, List<DeviceEvent> events)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name.Namespace == Aev)
            {
                if (child.Name.LocalName == "MessageInstance" && !applicationData && Event(child, path, names) is { } found)
                {
                    events.Add(found);
                }

                continue;
            }

            path.Add(TopicPart(child));
            names.Add(child.Attribute(Aev + "NiceName")?.Value is { Length: > 0 } nice ? nice.Trim() : "\u0000" + child.Name.LocalName);
            var internalData = applicationData || string.Equals(child.Attribute("isApplicationData")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            Walk(child, path, names, internalData, events);
            path.RemoveAt(path.Count - 1);
            names.RemoveAt(names.Count - 1);
        }
    }

    /// <summary>
    /// The device's names of the levels ("Device › Input ports › Virtual input"). When the event itself has a name, levels
    /// without one are left out (apps: "Image Health Analytics - Blur", not "CameraApplicationPlatform › ..."); otherwise
    /// they appear with their topic name ("Device › MQTT › ClientStatus"). Unnamed levels are marked with a leading NUL.
    /// </summary>
    private static string DisplayName(List<string> names)
    {
        var named = names[^1].Length > 0 && names[^1][0] != '\0';
        return string.Join(" › ", names.Where(n => !named || n[0] != '\0').Select(n => n.TrimStart('\0')));
    }

    private static string TopicPart(XElement element)
    {
        var uri = element.Name.NamespaceName;
        if (uri.Length == 0)
        {
            return element.Name.LocalName;
        }

        var prefix = Prefixes.GetValueOrDefault(uri) ?? element.GetPrefixOfNamespace(element.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : $"{prefix}:{element.Name.LocalName}";
    }

    private static DeviceEvent? Event(XElement message, List<string> path, List<string> names)
    {
        if (path.Count == 0)
        {
            return null;
        }

        var sources = Items(message.Element(Aev + "SourceInstance"), out _);
        var data = Items(message.Element(Aev + "DataInstance"), out var stateItem);
        var stateful = string.Equals(message.Attribute(Aev + "isProperty")?.Value, "true", StringComparison.OrdinalIgnoreCase) && stateItem is not null;
        return new DeviceEvent(string.Join('/', path), DisplayName(names), stateful, stateful ? stateItem : null, sources, data);
    }

    private static List<EventItem> Items(XElement? instance, out string? stateItem)
    {
        stateItem = null;
        var items = new List<EventItem>();
        foreach (var item in instance?.Elements(Aev + "SimpleItemInstance") ?? [])
        {
            var name = item.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name) || items.Exists(i => i.Name == name))
            {
                continue;
            }

            if (stateItem is null && string.Equals(item.Attribute("isPropertyState")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                stateItem = name;
            }

            var values = item.Elements(Aev + "Value")
                .Select(v => new EventValue(v.Value.Trim(), v.Attribute(Aev + "NiceName")?.Value))
                .Where(v => v.Value.Length > 0)
                .DistinctBy(v => v.Value)
                .OrderBy(v => int.TryParse(v.Value, out var n) ? n : int.MaxValue)
                .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            items.Add(new EventItem(name, item.Attribute(Aev + "NiceName")?.Value, values) { Kind = KindOf(item.Attribute("Type")?.Value) });
        }

        return items;
    }

    /// <summary>The kind of an item from its declared XML schema type ("xsd:int"); anything else is text.</summary>
    internal static EventValueKind KindOf(string? type)
    {
        var name = type is null ? string.Empty : type[(type.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        return name.ToUpperInvariant() switch
        {
            "BOOLEAN" => EventValueKind.YesNo,
            "INT" or "INTEGER" or "LONG" or "SHORT" or "BYTE" or "UNSIGNEDINT" or "UNSIGNEDLONG" or "UNSIGNEDSHORT" or "UNSIGNEDBYTE"
                or "NONNEGATIVEINTEGER" or "POSITIVEINTEGER" or "NONPOSITIVEINTEGER" or "NEGATIVEINTEGER"
                or "DOUBLE" or "FLOAT" or "DECIMAL" => EventValueKind.Number,
            _ => EventValueKind.Text,
        };
    }
}

/// <summary>A value an event item can have, with the device's display name for it.</summary>
public sealed record EventValue(string Value, string? DisplayName);

/// <summary>A source or data item of an event ("port", "Port number", values 0..63).</summary>
public sealed record EventItem(string Name, string? DisplayName, IReadOnlyList<EventValue> Values)
{
    /// <summary>The declared kind of the value (<c>Type</c> of the item: xsd:boolean = yes / no, numbers, else text).</summary>
    public EventValueKind Kind { get; init; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>
/// An event a device offers (from its event list). <see cref="Topic"/> as the stream names it
/// ("tns1:Device/tnsaxis:IO/VirtualInput"), <see cref="Name"/> the device's display path ("Device › Input ports ›
/// Virtual input"). Stateful events are on or off: <see cref="StateItem"/> is the data item holding the state.
/// </summary>
public sealed record DeviceEvent(string Topic, string Name, bool Stateful, string? StateItem, IReadOnlyList<EventItem> Sources, IReadOnlyList<EventItem> Data)
{
    /// <summary>The group of the event picker ("Input ports", "Device status"); every app event (Object Analytics, Video
    /// Motion Detection, Image Health Analytics, ...) is in "Applications".</summary>
    [JsonIgnore]
    public string Group => GroupAndTitle(Topic, Name).Group;

    /// <summary>The event's own name in the picker ("Virtual input", "Image Health Analytics - Blur").</summary>
    [JsonIgnore]
    public string Title => GroupAndTitle(Topic, Name).Title;

    /// <summary>
    /// Apps publish their events below these topics: ACAP events (<c>tnsaxis:CameraApplicationPlatform</c>), their ONVIF
    /// copies (<c>tns1:AcapOnvifEvent</c>) and the analytics rules (<c>tns1:RuleEngine</c>, e.g. VMD 3).
    /// </summary>
    public static bool IsAppTopic(string topic) =>
        topic.Contains("CameraApplicationPlatform", StringComparison.Ordinal)
        || topic.Contains("AcapOnvifEvent", StringComparison.Ordinal)
        || topic.StartsWith("tns1:RuleEngine/", StringComparison.Ordinal);

    internal static (string Group, string Title) GroupAndTitle(string topic, string name)
    {
        // Like the device's own event page: all apps in one group, the event with its own name; otherwise the section
        // below the top level ("Device status", "Input ports"), or the top level for two levels ("Video source").
        var parts = name.Split(" › ");
        if (IsAppTopic(topic))
        {
            return (AppsGroup, parts[^1]);
        }

        if (parts.Length > 2)
        {
            return (parts[1], parts[^1]);
        }

        if (parts.Length == 2)
        {
            return (parts[0], parts[1]);
        }

        return ("Other", name);
    }

    public const string AppsGroup = "Applications";
}
