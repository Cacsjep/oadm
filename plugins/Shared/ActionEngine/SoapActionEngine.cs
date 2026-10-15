using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.ActionEngine;

/// <summary>
/// The device's rule engine over SOAP at <c>/vapix/services</c> (action service <c>action1</c>, event service
/// <c>event1</c>), on every AXIS OS with the action engine (verified on AXIS OS 10.12 and 12.11). There is no change
/// method: a rule is changed by adding the new one and removing the old one. Reads return passwords in plain text (admin
/// only), which is why OADM reads rules this way and keeps the passwords on the server. Shared as source by the Action Rule
/// Editor and Device configuration (Configure devices).
/// </summary>
public static class SoapActionEngine
{
    public const string ServicePath = "/vapix/services";

    public const string ActionNs = "http://www.axis.com/vapix/ws/action1";
    public const string EventNs = "http://www.axis.com/vapix/ws/event1";
    private const string TopicDialect = "http://docs.oasis-open.org/wsn/t-1/TopicExpression/Concrete";
    private const string MessageDialect = "http://www.onvif.org/ver10/tev/messageContentFilter/ItemFilter";
    private const string OnvifTopics = "http://www.onvif.org/ver10/topics";
    private const string AxisTopics = "http://www.axis.com/2009/event/topics";
    private const string TopicNs = "http://docs.oasis-open.org/wsn/t-1";

    private static readonly XNamespace Aa = ActionNs;
    private static readonly XNamespace Aev = EventNs;
    private static readonly XNamespace Wstop = TopicNs;

    /// <summary>Rules, templates, recipients, schedules and the device's event list, in seven requests.</summary>
    public static async Task<DeviceRuleSet> ReadAsync(IVapixClient vapix, CancellationToken ct)
    {
        var configurations = ParseConfigurations(await CallAsync(vapix, ActionNs, "GetActionConfigurations", string.Empty, ct).ConfigureAwait(false));
        var rules = ParseRules(await CallAsync(vapix, ActionNs, "GetActionRules", string.Empty, ct).ConfigureAwait(false), configurations);
        var actionTemplates = ParseActionTemplates(await CallAsync(vapix, ActionNs, "GetActionTemplates", string.Empty, ct).ConfigureAwait(false));
        var recipientTemplates = ParseRecipientTemplates(await CallAsync(vapix, ActionNs, "GetRecipientTemplates", string.Empty, ct).ConfigureAwait(false));
        var recipients = ParseRecipients(await CallAsync(vapix, ActionNs, "GetRecipientConfigurations", string.Empty, ct).ConfigureAwait(false));
        var schedules = ParseSchedules(await CallAsync(vapix, EventNs, "GetScheduledEvents", string.Empty, ct).ConfigureAwait(false));
        IReadOnlyDictionary<string, string> names;
        XDocument? events = null;
        try
        {
            events = await CallAsync(vapix, EventNs, "GetEventInstances", string.Empty, ct).ConfigureAwait(false);
            names = ParseTopicNames(events);
        }
        catch (ActionEngineException)
        {
            names = new Dictionary<string, string>(); // names only make the page nicer
        }

        return new DeviceRuleSet(rules, actionTemplates, recipientTemplates, recipients, schedules, names) { Events = events };
    }

    /// <summary>The rules with their actions (passwords in plain text). Rules whose configuration is missing are left out.</summary>
    public static async Task<IReadOnlyList<DeviceRule>> ReadRulesAsync(IVapixClient vapix, CancellationToken ct)
    {
        var configurations = ParseConfigurations(await CallAsync(vapix, ActionNs, "GetActionConfigurations", string.Empty, ct).ConfigureAwait(false));
        return ParseRules(await CallAsync(vapix, ActionNs, "GetActionRules", string.Empty, ct).ConfigureAwait(false), configurations);
    }

    /// <summary>Every rule as listed, also those whose action configuration is missing.</summary>
    public static async Task<IReadOnlyList<RuleListing>> ListRulesAsync(IVapixClient vapix, CancellationToken ct)
    {
        var doc = await CallAsync(vapix, ActionNs, "GetActionRules", string.Empty, ct).ConfigureAwait(false);
        return [.. doc.Descendants(Aa + "ActionRule")
            .Select(r => new RuleListing(
                ((string?)r.Element(Aa + "RuleID"))?.Trim() ?? string.Empty,
                (string?)r.Element(Aa + "Name") ?? string.Empty,
                ((string?)r.Element(Aa + "PrimaryAction"))?.Trim()))
            .Where(r => r.Id.Length > 0)];
    }

    /// <summary>The schedules (iCalendar text, line ends as \n).</summary>
    public static async Task<IReadOnlyList<DeviceSchedule>> ReadSchedulesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseSchedules(await CallAsync(vapix, EventNs, "GetScheduledEvents", string.Empty, ct).ConfigureAwait(false));

    /// <summary>Creates an action configuration; returns its id.</summary>
    public static async Task<string> AddConfigurationAsync(IVapixClient vapix, RuleAction action, string name, CancellationToken ct)
    {
        var parameters = string.Concat(action.Parameters.Select(p => $"<aa:Parameter Name=\"{Attribute(p.Name)}\" Value=\"{Attribute(p.Value)}\"/>"));
        var body = $"<aa:NewActionConfiguration><aa:Name>{Escape(name)}</aa:Name><aa:TemplateToken>{Escape(action.Template)}</aa:TemplateToken><aa:Parameters>{parameters}</aa:Parameters></aa:NewActionConfiguration>";
        var doc = await CallAsync(vapix, ActionNs, "AddActionConfiguration", body, ct).ConfigureAwait(false);
        return doc.Descendants(Aa + "ConfigurationID").FirstOrDefault()?.Value.Trim() is { Length: > 0 } id
            ? id
            : throw new ActionEngineException("The device did not return the new action.");
    }

    /// <summary>Creates a rule pointing to <paramref name="configurationId"/>; returns its id.</summary>
    public static async Task<string> AddRuleAsync(IVapixClient vapix, DeviceRule rule, string configurationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var body = new StringBuilder();
        body.Append("<aa:NewActionRule>")
            .Append(CultureInfo.InvariantCulture, $"<aa:Name>{Escape(rule.Name)}</aa:Name>")
            .Append(CultureInfo.InvariantCulture, $"<aa:Enabled>{(rule.Enabled ? "true" : "false")}</aa:Enabled>");
        if (rule.StartEvent is { } start)
        {
            body.Append("<aa:StartEvent>").Append(Filter(start)).Append("</aa:StartEvent>");
        }

        if (rule.Conditions.Count > 0)
        {
            body.Append("<aa:Conditions>");
            foreach (var condition in rule.Conditions)
            {
                body.Append("<aa:Condition>").Append(Filter(condition)).Append("</aa:Condition>");
            }

            body.Append("</aa:Conditions>");
        }

        if (!string.IsNullOrWhiteSpace(rule.Timeout))
        {
            body.Append(CultureInfo.InvariantCulture, $"<aa:ActivationTimeout>{Escape(rule.Timeout)}</aa:ActivationTimeout>");
        }

        body.Append(CultureInfo.InvariantCulture, $"<aa:PrimaryAction>{Escape(configurationId)}</aa:PrimaryAction></aa:NewActionRule>");
        var doc = await CallAsync(vapix, ActionNs, "AddActionRule", body.ToString(), ct).ConfigureAwait(false);
        return doc.Descendants(Aa + "RuleID").FirstOrDefault()?.Value.Trim() is { Length: > 0 } id
            ? id
            : throw new ActionEngineException("The device did not return the new action rule.");
    }

    /// <summary>Creates a recipient preset; returns its id.</summary>
    public static async Task<string> AddRecipientAsync(IVapixClient vapix, DeviceRecipient recipient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var parameters = string.Concat(recipient.Parameters.Select(p => $"<aa:Parameter Name=\"{Attribute(p.Name)}\" Value=\"{Attribute(p.Value)}\"/>"));
        var body = $"<aa:NewRecipientConfiguration><aa:Name>{Escape(recipient.Name)}</aa:Name><aa:TemplateToken>{Escape(recipient.Template)}</aa:TemplateToken><aa:Parameters>{parameters}</aa:Parameters></aa:NewRecipientConfiguration>";
        var doc = await CallAsync(vapix, ActionNs, "AddRecipientConfiguration", body, ct).ConfigureAwait(false);
        return doc.Descendants(Aa + "ConfigurationID").FirstOrDefault()?.Value.Trim() is { Length: > 0 } id
            ? id
            : throw new ActionEngineException("The device did not return the new recipient.");
    }

    public static Task RemoveRecipientAsync(IVapixClient vapix, string recipientId, CancellationToken ct) =>
        CallAsync(vapix, ActionNs, "RemoveRecipientConfiguration", $"<aa:ConfigurationID>{Escape(recipientId)}</aa:ConfigurationID>", ct);

    /// <summary>
    /// Creates a schedule with the given id (AXIS OS 10 to 12 accept an own id; the same id twice is refused "already
    /// exists"); returns the id.
    /// </summary>
    public static async Task<string> AddScheduleAsync(IVapixClient vapix, DeviceSchedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var id = string.IsNullOrEmpty(schedule.Id) ? string.Empty : $"<aev:EventID>{Escape(schedule.Id)}</aev:EventID>";
        var body = $"<aev:NewScheduledEvent><aev:Name>{Escape(schedule.Name)}</aev:Name>{id}<aev:Schedule><aev:ICalendar Dialect=\"http://www.axis.com/vapix/ws/ical1\">{Escape(schedule.Definition)}</aev:ICalendar></aev:Schedule></aev:NewScheduledEvent>";
        var doc = await CallAsync(vapix, EventNs, "AddScheduledEvent", body, ct).ConfigureAwait(false);
        return doc.Descendants(Aev + "EventID").FirstOrDefault()?.Value.Trim() is { Length: > 0 } given
            ? given
            : schedule.Id.Length > 0 ? schedule.Id : throw new ActionEngineException("The device did not return the new schedule.");
    }

    public static Task RemoveScheduleAsync(IVapixClient vapix, string scheduleId, CancellationToken ct) =>
        CallAsync(vapix, EventNs, "RemoveScheduledEvent", $"<aev:EventID>{Escape(scheduleId)}</aev:EventID>", ct);

    public static Task RemoveRuleAsync(IVapixClient vapix, string ruleId, CancellationToken ct) =>
        CallAsync(vapix, ActionNs, "RemoveActionRule", $"<aa:RuleID>{Escape(ruleId)}</aa:RuleID>", ct);

    public static Task RemoveConfigurationAsync(IVapixClient vapix, string configurationId, CancellationToken ct) =>
        CallAsync(vapix, ActionNs, "RemoveActionConfiguration", $"<aa:ConfigurationID>{Escape(configurationId)}</aa:ConfigurationID>", ct);

    /// <summary>
    /// A filter as the device takes it. The topic goes without the <c>//.</c> AXIS OS 10 appends to condition topics: written
    /// back it is refused on a leaf topic ("failed to parse topic expression ...Recurring/Interval//."), and the device adds it
    /// again itself.
    /// </summary>
    private static string Filter(EventFilter filter) =>
        $"<wsnt:TopicExpression Dialect=\"{TopicDialect}\">{Escape(Topic(filter.Topic))}</wsnt:TopicExpression>"
        + (string.IsNullOrEmpty(filter.Message) ? string.Empty : $"<wsnt:MessageContent Dialect=\"{MessageDialect}\">{Escape(filter.Message)}</wsnt:MessageContent>");

    /// <summary>The topic without the <c>//.</c> AXIS OS 10 appends to condition topics.</summary>
    public static string Topic(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var t = topic.Trim();
        while (t.EndsWith("//.", StringComparison.Ordinal))
        {
            t = t[..^3];
        }

        return t;
    }

    public static IReadOnlyDictionary<string, RuleAction> ParseConfigurations(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var result = new Dictionary<string, RuleAction>(StringComparer.Ordinal);
        foreach (var c in doc.Descendants(Aa + "ActionConfiguration"))
        {
            var id = ((string?)c.Element(Aa + "ConfigurationID"))?.Trim();
            var template = ((string?)c.Element(Aa + "TemplateToken"))?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(template) || result.ContainsKey(id))
            {
                continue;
            }

            result[id] = new RuleAction(template, Parameters(c))
            {
                ConfigurationId = id,
                Name = (string?)c.Element(Aa + "Name"),
            };
        }

        return result;
    }

    public static IReadOnlyList<DeviceRule> ParseRules(XDocument doc, IReadOnlyDictionary<string, RuleAction> configurations)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(configurations);
        var result = new List<DeviceRule>();
        foreach (var rule in doc.Descendants(Aa + "ActionRule"))
        {
            var id = ((string?)rule.Element(Aa + "RuleID"))?.Trim();
            var actionId = ((string?)rule.Element(Aa + "PrimaryAction"))?.Trim();
            if (string.IsNullOrEmpty(id) || actionId is null || !configurations.TryGetValue(actionId, out var action))
            {
                continue;
            }

            var timeout = ((string?)rule.Element(Aa + "ActivationTimeout"))?.Trim();
            result.Add(new DeviceRule(
                id,
                (string?)rule.Element(Aa + "Name") ?? string.Empty,
                !string.Equals(((string?)rule.Element(Aa + "Enabled"))?.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                ReadFilter(rule.Element(Aa + "StartEvent")),
                [.. (rule.Element(Aa + "Conditions")?.Elements() ?? []).Select(ReadFilter).OfType<EventFilter>()],
                string.IsNullOrEmpty(timeout) ? null : timeout,
                action));
        }

        return result;
    }

    public static IReadOnlyList<ActionTemplate> ParseActionTemplates(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return [.. doc.Descendants(Aa + "ActionTemplate")
            .Select(t => new ActionTemplate(
                ((string?)t.Element(Aa + "TemplateToken"))?.Trim() ?? string.Empty,
                ((string?)t.Element(Aa + "RecipientTemplate"))?.Trim() is { Length: > 0 } r ? r : null,
                ParameterNames(t)))
            .Where(t => t.Token.Length > 0)
            .DistinctBy(t => t.Token)];
    }

    public static IReadOnlyList<RecipientTemplate> ParseRecipientTemplates(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return [.. doc.Descendants(Aa + "RecipientTemplate")
            .Where(t => t.Element(Aa + "TemplateToken") is not null)
            .Select(t => new RecipientTemplate(((string?)t.Element(Aa + "TemplateToken"))!.Trim(), ParameterNames(t)))
            .DistinctBy(t => t.Token)];
    }

    public static IReadOnlyList<DeviceRecipient> ParseRecipients(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return [.. doc.Descendants(Aa + "RecipientConfiguration")
            .Select(r => new DeviceRecipient(
                ((string?)r.Element(Aa + "ConfigurationID"))?.Trim() ?? string.Empty,
                (string?)r.Element(Aa + "Name") ?? string.Empty,
                ((string?)r.Element(Aa + "TemplateToken"))?.Trim() ?? string.Empty,
                Parameters(r)))
            .Where(r => r.Id.Length > 0)];
    }

    public static IReadOnlyList<DeviceSchedule> ParseSchedules(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return [.. doc.Descendants(Aev + "ScheduledEvent")
            .Select(e => new DeviceSchedule(
                ((string?)e.Element(Aev + "EventID"))?.Trim() ?? string.Empty,
                (string?)e.Element(Aev + "Name") ?? string.Empty,
                ((string?)e.Element(Aev + "Schedule")?.Element(Aev + "ICalendar") ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim()))
            .Where(s => s.Id.Length > 0)];
    }

    /// <summary>
    /// The device's names of its event topics (<c>GetEventInstances</c>, attribute <c>NiceName</c>): "Virtual input",
    /// "Schedule: Scheduled event". Keyed by the topic as rules write it (<c>tns1:Device/tnsaxis:IO/VirtualInput</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseTopicNames(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TopicSet") is not { } set)
        {
            return result;
        }

        foreach (var child in set.Elements())
        {
            Walk(child, string.Empty, null, result);
        }

        return result;
    }

    private static void Walk(XElement element, string parentPath, string? parentNice, Dictionary<string, string> names)
    {
        if (element.Name.Namespace == Aev)
        {
            return; // MessageInstance and its children describe the data, not topics
        }

        var prefix = element.Name.NamespaceName switch
        {
            OnvifTopics => "tns1:",
            AxisTopics => "tnsaxis:",
            _ => string.Empty,
        };
        var path = (parentPath.Length == 0 ? string.Empty : parentPath + "/") + prefix + element.Name.LocalName;
        var nice = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "NiceName")?.Value.Trim();
        if (string.Equals((string?)element.Attribute(Wstop + "topic"), "true", StringComparison.OrdinalIgnoreCase))
        {
            names[path] = nice is { Length: > 0 }
                ? parentNice is { Length: > 0 } ? parentNice + ": " + nice : nice
                : parentNice ?? string.Empty;
        }

        foreach (var child in element.Elements())
        {
            Walk(child, path, nice is { Length: > 0 } ? nice : parentNice, names);
        }
    }

    private static IReadOnlyList<NameValue> Parameters(XElement owner) =>
        [.. (owner.Element(Aa + "Parameters")?.Elements(Aa + "Parameter") ?? [])
            .Select(p => new NameValue((string?)p.Attribute("Name") ?? string.Empty, (string?)p.Attribute("Value") ?? string.Empty))
            .Where(p => p.Name.Length > 0)];

    private static IReadOnlyList<string> ParameterNames(XElement owner) =>
        [.. (owner.Element(Aa + "Parameters")?.Elements(Aa + "Parameter") ?? [])
            .Select(p => (string?)p.Attribute("Name") ?? string.Empty)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    private static EventFilter? ReadFilter(XElement? filter)
    {
        var topic = filter?.Elements().FirstOrDefault(e => e.Name.LocalName == "TopicExpression")?.Value.Trim();
        if (string.IsNullOrEmpty(topic))
        {
            return null;
        }

        var message = filter!.Elements().FirstOrDefault(e => e.Name.LocalName == "MessageContent")?.Value.Trim();
        return new EventFilter(topic, string.IsNullOrEmpty(message) ? null : message);
    }

    /// <summary>One SOAP call; a SOAP fault becomes <see cref="ActionEngineException"/> with the device's reason.</summary>
    private static async Task<XDocument> CallAsync(IVapixClient vapix, string ns, string method, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var prefix = ns == ActionNs ? "aa" : "aev";
        var envelope = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:aa=\"" + ActionNs + "\" xmlns:aev=\"" + EventNs
            + "\" xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\" xmlns:tns1=\"" + OnvifTopics + "\""
            + " xmlns:tnsaxis=\"" + AxisTopics + "\"><soap:Body>"
            + $"<{prefix}:{method}>{body}</{prefix}:{method}>"
            + "</soap:Body></soap:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, ServicePath) { Content = new StringContent(envelope, Encoding.UTF8) };
        request.Content.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", $"application/soap+xml; action={ns}/{method}; charset=utf-8");
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        XDocument doc;
        try
        {
            doc = DeviceXml.Parse(text);
        }
        catch (XmlException ex)
        {
            throw new ActionEngineException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? "The device sent an unreadable answer.", ex);
        }

        if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault") is { } fault)
        {
            var reason = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value.Trim();
            throw new ActionEngineException(string.IsNullOrEmpty(reason) ? "The device refused the request." : "The device refused: " + reason);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ActionEngineException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? $"HTTP {(int)response.StatusCode}");
        }

        return doc;
    }

    private static string Escape(string text) => SecurityElement.Escape(text);

    /// <summary>An attribute value: line breaks and tabs as character references (a raw one would become a space).</summary>
    private static string Attribute(string text) =>
        Escape(text).Replace("\r", "&#xD;", StringComparison.Ordinal).Replace("\n", "&#xA;", StringComparison.Ordinal).Replace("\t", "&#x9;", StringComparison.Ordinal);
}
