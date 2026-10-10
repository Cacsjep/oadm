using System.Text;
using System.Xml.Linq;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers.Device;

/// <summary>
/// The events a device offers: <c>aev:GetEventInstances</c> of the event service (read-only). Verified on an AXIS
/// P3265-V with AXIS OS 12.11: a <c>wstop:TopicSet</c> tree whose leaves hold one <c>aev:MessageInstance</c>
/// (<c>aev:isProperty="true"</c> = on/off) with Source / Key / Data instances; the data item with
/// <c>isPropertyState="true"</c> is the state. Apps add their events only while they run. Topics marked
/// <c>isApplicationData="true"</c> (on the topic or a parent: internal data such as Object Analytics drawing frames, ONVIF
/// duplicates of the I/O events) are left out, like the device's own event rule page does.
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
            names.Add(child.Attribute(Aev + "NiceName")?.Value is { Length: > 0 } nice ? nice.Trim() : child.Name.LocalName);
            var internalData = applicationData || string.Equals(child.Attribute("isApplicationData")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            Walk(child, path, names, internalData, events);
            path.RemoveAt(path.Count - 1);
            names.RemoveAt(names.Count - 1);
        }
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
        return new DeviceEvent(string.Join('/', path), string.Join(" › ", names), stateful, stateful ? stateItem : null, sources, data);
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
            items.Add(new EventItem(name, item.Attribute(Aev + "NiceName")?.Value, values));
        }

        return items;
    }
}
