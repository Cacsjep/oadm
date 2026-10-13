using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// The device's action rules, their action configurations and its schedules (SOAP at <c>/vapix/services</c>: the action
/// service <c>action1</c> and the event service <c>event1</c>; reads verified on an AXIS P3265-V, AXIS OS 12.11). A rule on
/// the device points to an action configuration by id; ADM copies the configuration into the rule, and so does OADM.
/// </summary>
public static class ActionEngineApi
{
    public const string ServicePath = "/vapix/services";

    private const string ActionNs = "http://www.axis.com/vapix/ws/action1";
    private const string EventNs = "http://www.axis.com/vapix/ws/event1";
    private const string TopicDialect = "http://docs.oasis-open.org/wsn/t-1/TopicExpression/Concrete";
    private const string MessageDialect = "http://www.onvif.org/ver10/tev/messageContentFilter/ItemFilter";

    private static readonly XNamespace Aa = ActionNs;
    private static readonly XNamespace Aev = EventNs;

    /// <summary>The rules with their actions copied in. Rules whose configuration is missing are left out.</summary>
    public static async Task<IReadOnlyList<ConfigActionRule>> ReadRulesAsync(IVapixClient vapix, CancellationToken ct)
    {
        var configurations = ParseConfigurations(await CallAsync(vapix, ActionNs, "GetActionConfigurations", string.Empty, ct).ConfigureAwait(false));
        return ParseRules(await CallAsync(vapix, ActionNs, "GetActionRules", string.Empty, ct).ConfigureAwait(false), configurations);
    }

    public static async Task<IReadOnlyList<ConfigSchedule>> ReadSchedulesAsync(IVapixClient vapix, CancellationToken ct) =>
        ParseSchedules(await CallAsync(vapix, EventNs, "GetScheduledEvents", string.Empty, ct).ConfigureAwait(false));

    /// <summary>The device's rules as (rule id, name, configuration id), for replacing a rule of the same name.</summary>
    public static async Task<IReadOnlyList<(string RuleId, string Name, string? ConfigurationId)>> ListRuleIdsAsync(IVapixClient vapix, CancellationToken ct)
    {
        var doc = await CallAsync(vapix, ActionNs, "GetActionRules", string.Empty, ct).ConfigureAwait(false);
        return [.. doc.Descendants(Aa + "ActionRule").Select(r => (
            ((string?)r.Element(Aa + "RuleID") ?? string.Empty).Trim(),
            (string?)r.Element(Aa + "Name") ?? string.Empty,
            ((string?)r.Element(Aa + "PrimaryAction"))?.Trim()))];
    }

    /// <summary>Creates a schedule; returns the id the device gave it.</summary>
    public static async Task<string> AddScheduleAsync(IVapixClient vapix, ConfigSchedule schedule, CancellationToken ct)
    {
        var body = $"<aev:NewScheduledEvent><aev:Name>{Escape(schedule.Name)}</aev:Name><aev:Schedule><aev:ICalendar>{Escape(schedule.Definition)}</aev:ICalendar></aev:Schedule></aev:NewScheduledEvent>";
        var doc = await CallAsync(vapix, EventNs, "AddScheduledEvent", body, ct).ConfigureAwait(false);
        return doc.Descendants(Aev + "EventID").FirstOrDefault()?.Value.Trim() is { Length: > 0 } id
            ? id
            : throw new ConfigDeviceException("The device did not return the new schedule.");
    }

    /// <summary>Creates an action configuration; returns its id.</summary>
    public static async Task<string> AddConfigurationAsync(IVapixClient vapix, ConfigAction action, CancellationToken ct)
    {
        var parameters = string.Concat(action.Parameters.Select(p => $"<aa:Parameter Name=\"{Attribute(p.Name)}\" Value=\"{Attribute(p.Value)}\"/>"));
        var body = $"<aa:NewActionConfiguration><aa:Name>{Escape(action.Name)}</aa:Name><aa:TemplateToken>{Escape(action.Template)}</aa:TemplateToken><aa:Parameters>{parameters}</aa:Parameters></aa:NewActionConfiguration>";
        var doc = await CallAsync(vapix, ActionNs, "AddActionConfiguration", body, ct).ConfigureAwait(false);
        return doc.Descendants(Aa + "ConfigurationID").FirstOrDefault()?.Value.Trim() is { Length: > 0 } id
            ? id
            : throw new ConfigDeviceException("The device did not return the new action.");
    }

    /// <summary>Creates a rule pointing to <paramref name="configurationId"/>; returns its id.</summary>
    public static async Task<string> AddRuleAsync(IVapixClient vapix, ConfigActionRule rule, string configurationId, CancellationToken ct)
    {
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

        if (!string.IsNullOrWhiteSpace(rule.ActivationTimeout))
        {
            body.Append(CultureInfo.InvariantCulture, $"<aa:ActivationTimeout>{Escape(rule.ActivationTimeout)}</aa:ActivationTimeout>");
        }

        body.Append(CultureInfo.InvariantCulture, $"<aa:PrimaryAction>{Escape(configurationId)}</aa:PrimaryAction></aa:NewActionRule>");
        var doc = await CallAsync(vapix, ActionNs, "AddActionRule", body.ToString(), ct).ConfigureAwait(false);
        return doc.Descendants(Aa + "RuleID").FirstOrDefault()?.Value.Trim() is { Length: > 0 } id
            ? id
            : throw new ConfigDeviceException("The device did not return the new action rule.");
    }

    public static Task RemoveRuleAsync(IVapixClient vapix, string ruleId, CancellationToken ct) =>
        CallAsync(vapix, ActionNs, "RemoveActionRule", $"<aa:RuleID>{Escape(ruleId)}</aa:RuleID>", ct);

    public static Task RemoveConfigurationAsync(IVapixClient vapix, string configurationId, CancellationToken ct) =>
        CallAsync(vapix, ActionNs, "RemoveActionConfiguration", $"<aa:ConfigurationID>{Escape(configurationId)}</aa:ConfigurationID>", ct);

    /// <summary>Removes an own schedule (the hardware write test cleans up with it).</summary>
    public static Task RemoveScheduleAsync(IVapixClient vapix, string eventId, CancellationToken ct) =>
        CallAsync(vapix, EventNs, "RemoveScheduledEvent", $"<aev:EventID>{Escape(eventId)}</aev:EventID>", ct);

    private static string Filter(ConfigEventFilter filter) =>
        $"<wsnt:TopicExpression Dialect=\"{TopicDialect}\">{Escape(filter.Topic)}</wsnt:TopicExpression>"
        + (string.IsNullOrEmpty(filter.Message) ? string.Empty : $"<wsnt:MessageContent Dialect=\"{MessageDialect}\">{Escape(filter.Message)}</wsnt:MessageContent>");

    public static IReadOnlyDictionary<string, ConfigAction> ParseConfigurations(XDocument doc) =>
        doc.Descendants(Aa + "ActionConfiguration")
            .Select(c => new ConfigAction(
                (string?)c.Element(Aa + "Name") ?? string.Empty,
                (string?)c.Element(Aa + "TemplateToken") ?? string.Empty,
                [.. (c.Element(Aa + "Parameters")?.Elements(Aa + "Parameter") ?? [])
                    .Select(p => new NameValue((string?)p.Attribute("Name") ?? string.Empty, (string?)p.Attribute("Value") ?? string.Empty))
                    .Where(p => p.Name.Length > 0)])
            {
                Id = (string?)c.Element(Aa + "ConfigurationID"),
            })
            .Where(c => c.Id is { Length: > 0 } && c.Template.Length > 0)
            .GroupBy(c => c.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public static IReadOnlyList<ConfigActionRule> ParseRules(XDocument doc, IReadOnlyDictionary<string, ConfigAction> configurations)
    {
        var result = new List<ConfigActionRule>();
        foreach (var rule in doc.Descendants(Aa + "ActionRule"))
        {
            var actionId = (string?)rule.Element(Aa + "PrimaryAction");
            if (actionId is null || !configurations.TryGetValue(actionId.Trim(), out var action))
            {
                continue;
            }

            result.Add(new ConfigActionRule(
                (string?)rule.Element(Aa + "Name") ?? string.Empty,
                !string.Equals((string?)rule.Element(Aa + "Enabled"), "false", StringComparison.OrdinalIgnoreCase),
                ReadFilter(rule.Element(Aa + "StartEvent")),
                [.. (rule.Element(Aa + "Conditions")?.Elements() ?? []).Select(ReadFilter).OfType<ConfigEventFilter>()],
                action)
            {
                ActivationTimeout = string.IsNullOrWhiteSpace((string?)rule.Element(Aa + "ActivationTimeout")) ? null : ((string?)rule.Element(Aa + "ActivationTimeout"))!.Trim(),
            });
        }

        return result;
    }

    public static IReadOnlyList<ConfigSchedule> ParseSchedules(XDocument doc) =>
        [.. doc.Descendants(Aev + "ScheduledEvent")
            .Select(e => new ConfigSchedule(
                (string?)e.Element(Aev + "EventID") ?? string.Empty,
                (string?)e.Element(Aev + "Name") ?? string.Empty,
                ((string?)e.Element(Aev + "Schedule")?.Element(Aev + "ICalendar") ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim()))
            .Where(s => s.Id.Length > 0)];

    private static ConfigEventFilter? ReadFilter(XElement? filter)
    {
        var topic = filter?.Elements().FirstOrDefault(e => e.Name.LocalName == "TopicExpression")?.Value.Trim();
        if (string.IsNullOrEmpty(topic))
        {
            return null;
        }

        var message = filter!.Elements().FirstOrDefault(e => e.Name.LocalName == "MessageContent")?.Value.Trim();
        return new ConfigEventFilter(topic, string.IsNullOrEmpty(message) ? null : message);
    }

    /// <summary>One SOAP call; a SOAP fault becomes <see cref="ConfigDeviceException"/> with the device's reason.</summary>
    private static async Task<XDocument> CallAsync(IVapixClient vapix, string ns, string method, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var envelope = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:aa=\"" + ActionNs + "\" xmlns:aev=\"" + EventNs
            + "\" xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\" xmlns:tns1=\"http://www.onvif.org/ver10/topics\""
            + " xmlns:tnsaxis=\"http://www.axis.com/2009/event/topics\"><soap:Body>"
            + $"<{(ns == ActionNs ? "aa" : "aev")}:{method}>{body}</{(ns == ActionNs ? "aa" : "aev")}:{method}>"
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
            throw new ConfigDeviceException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? "The device sent an unreadable answer.", ex);
        }

        if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault") is { } fault)
        {
            var reason = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value.Trim();
            throw new ConfigDeviceException(string.IsNullOrEmpty(reason) ? "The device refused the request." : "The device refused: " + reason);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ConfigDeviceException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? $"HTTP {(int)response.StatusCode}");
        }

        return doc;
    }

    private static string Escape(string text) => SecurityElement.Escape(text);

    /// <summary>An attribute value: line breaks and tabs as character references (a raw one would become a space).</summary>
    private static string Attribute(string text) =>
        Escape(text).Replace("\r", "&#xD;", StringComparison.Ordinal).Replace("\n", "&#xA;", StringComparison.Ordinal).Replace("\t", "&#x9;", StringComparison.Ordinal);
}
