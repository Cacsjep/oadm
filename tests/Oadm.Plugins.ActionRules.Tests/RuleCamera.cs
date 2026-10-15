using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

using Oadm.Plugins.ActionRules.Device;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// A camera's rule engine as verified on 10.0.0.202 (AXIS OS 12.11) and 10.0.0.200 (AXIS OS 10.12): SOAP reads with
/// passwords in plain text, add and remove; with <see cref="HasRest"/> also REST <c>action-rules</c> v2, which changes a
/// rule in place, refuses an unlimited action without a condition ("requires at least one condition"), answers "Internal
/// error" for missing or unknown parameters, and stores <c>**Redacted**</c> as the password when that is sent. Templates
/// come from the recorded fixtures. Every request is recorded ("SOAP AddActionRule", "PATCH rules/3").
/// </summary>
internal sealed class RuleCamera : IVapixClient
{
    private static readonly XNamespace Aa = SoapActionEngine.ActionNs;
    private static readonly XNamespace Aev = SoapActionEngine.EventNs;
    private readonly string _os;
    private readonly IReadOnlyList<ActionTemplate> _templates;
    private readonly IReadOnlyList<RecipientTemplate> _recipients;
    private int _nextRule = 1;
    private int _nextConfig = 1;

    public RuleCamera(string os = "os12", bool? hasRest = null)
    {
        _os = os;
        HasRest = hasRest ?? os == "os12";
        _templates = SoapActionEngine.ParseActionTemplates(Fixture("GetActionTemplates", os));
        _recipients = SoapActionEngine.ParseRecipientTemplates(Fixture("GetRecipientTemplates", os));
        foreach (var r in SoapActionEngine.ParseRecipients(Fixture("GetRecipientConfigurations", os)))
        {
            Recipients[r.Id] = r;
            _nextRecipient = Math.Max(_nextRecipient, int.Parse(r.Id, CultureInfo.InvariantCulture) + 1);
        }

        foreach (var schedule in SoapActionEngine.ParseSchedules(Fixture("GetScheduledEvents", os)))
        {
            Schedules[schedule.Id] = schedule;
        }
    }

    private int _nextRecipient = 1;

    /// <summary>The recipient presets (from the fixture, then as changed).</summary>
    public Dictionary<string, DeviceRecipient> Recipients { get; } = [];

    /// <summary>The schedules (from the fixture, then as changed).</summary>
    public Dictionary<string, DeviceSchedule> Schedules { get; } = [];

    public Uri BaseAddress { get; } = new("http://10.0.0.202/");

    public bool HasRest { get; set; }

    public Dictionary<string, Rule> Rules { get; } = [];

    public Dictionary<string, Config> Configs { get; } = [];

    public List<string> Requests { get; } = [];

    /// <summary>The JSON bodies of the REST requests, in order.</summary>
    public List<string> RestBodies { get; } = [];

    /// <summary>Requests the camera refuses: a substring of the recorded request line.</summary>
    public string? RefuseRequest { get; set; }

    /// <summary>How many matching requests are refused (then the camera accepts them again).</summary>
    public int RefuseTimes { get; set; } = int.MaxValue;

    /// <summary>Matching requests that go through before the refusals start.</summary>
    public int RefuseSkip { get; set; }

    /// <summary>A request that times out (the HTTP client throws), after <see cref="TimeoutSkip"/> matching ones went through.</summary>
    public string? TimeoutRequest { get; set; }

    public int TimeoutSkip { get; set; }

    /// <summary>How many matching requests time out (then the camera answers again).</summary>
    public int TimeoutTimes { get; set; } = int.MaxValue;

    /// <summary>The timed-out request is done by the camera first; only its answer is lost.</summary>
    public bool TimeoutAfterDoing { get; set; }

    public sealed record Config(string Name, string Template, List<NameValue> Parameters);

    public sealed record Rule(string Name, bool Enabled, EventFilter? Start, List<EventFilter> Conditions, string? Timeout, string ConfigId);

    public static XDocument Fixture(string name, string os = "os12") =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{os}-{name}.xml"));

    /// <summary>Adds a rule directly (test setup); returns its id.</summary>
    public string Add(string name, string template, IEnumerable<(string Name, string Value)> parameters, EventFilter? start = null, IEnumerable<EventFilter>? conditions = null, bool enabled = true, string? timeout = null)
    {
        var config = (_nextConfig++).ToString(CultureInfo.InvariantCulture);
        Configs[config] = new Config(name, template, [.. parameters.Select(p => new NameValue(p.Name, p.Value))]);
        var id = (_nextRule++).ToString(CultureInfo.InvariantCulture);
        Rules[id] = new Rule(name, enabled, start, [.. conditions ?? []], timeout, config);
        return id;
    }

    public string? Param(string ruleId, string name) => Configs[Rules[ruleId].ConfigId].Parameters.FirstOrDefault(p => p.Name == name)?.Value;

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeviceApi>>([]);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.OriginalString;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        if (path == SoapActionEngine.ServicePath)
        {
            return Soap(XDocument.Parse(body));
        }

        if (path == RestActionRules.DiscoverPath)
        {
            Requests.Add("GET discover");
            return HasRest
                ? Json(HttpStatusCode.OK, """{"apiVersion":"1.0","data":{"apis":{"action-rules":{"v2":{"version":"2.0.1"}},"event-schedules":{"v2":{"version":"2.0.1"}}}}}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (HasRest && (path.StartsWith(RestActionRules.RecipientsPath + "/", StringComparison.Ordinal) || path.StartsWith(RestActionRules.SchedulesPath + "/", StringComparison.Ordinal)))
        {
            var recipient = path.StartsWith(RestActionRules.RecipientsPath, StringComparison.Ordinal);
            var id = Uri.UnescapeDataString(path[((recipient ? RestActionRules.RecipientsPath : RestActionRules.SchedulesPath).Length + 1)..]);
            Requests.Add($"{request.Method} {(recipient ? "recipients" : "schedules")}/{id}");
            RestBodies.Add(body);
            var data = JsonNode.Parse(body)!["data"]!;
            if (recipient)
            {
                if (!Recipients.TryGetValue(id, out var r))
                {
                    return RestError(1, "Resource not found");
                }

                Recipients[id] = r with
                {
                    Name = data["name"]?.GetValue<string>() ?? r.Name,
                    Parameters = data["parameters"] is JsonArray a ? [.. a.Select(p => new NameValue(p!["name"]!.GetValue<string>(), p["value"]!.GetValue<string>()))] : r.Parameters,
                };
            }
            else
            {
                if (!Schedules.TryGetValue(id, out var sc))
                {
                    return RestError(1, "Resource not found");
                }

                Schedules[id] = sc with { Name = data["name"]?.GetValue<string>() ?? sc.Name, Definition = data["schedule"]?.GetValue<string>() ?? sc.Definition };
            }

            return Json(HttpStatusCode.OK, """{"status":"success"}""");
        }

        if (path.StartsWith(RestActionRules.RulesPath, StringComparison.Ordinal) && HasRest)
        {
            var id = path.Length > RestActionRules.RulesPath.Length ? path[(RestActionRules.RulesPath.Length + 1)..] : null;
            var line = $"{request.Method} rules" + (id is null ? string.Empty : "/" + id);
            Requests.Add(line);
            RestBodies.Add(body);
            if (RefuseRequest is not null && line.Contains(RefuseRequest, StringComparison.Ordinal))
            {
                return RestError(5, "Validation error: refused by test");
            }

            return Rest(request.Method.Method, id, body.Length == 0 ? null : JsonNode.Parse(body)?["data"]);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Rest(string method, string? id, JsonNode? data)
    {
        if (method == "DELETE")
        {
            if (id is null || !Rules.Remove(id, out var gone))
            {
                return RestError(1, "Resource not found");
            }

            Configs.Remove(gone.ConfigId);
            return Json(HttpStatusCode.OK, """{"status":"success"}""");
        }

        if (method == "POST")
        {
            var config = (_nextConfig++).ToString(CultureInfo.InvariantCulture);
            var (template, parameters, error) = ReadAction(data!["actionConfig"]!);
            if (error is not null)
            {
                return error;
            }

            var (start, conditions, timeout) = ReadActivation(data["activationConfig"]!);
            if (conditions.Prepend(start).OfType<EventFilter>().Any(f => MissingSchedule(f.Message)))
            {
                return RestError(0, "Internal error");
            }

            if (Unlimited(template) && conditions.Count == 0)
            {
                return RestError(5, "Validation error: Action configuration is of type 'continuous' and requires at least one condition");
            }

            Configs[config] = new Config(template, template, parameters);
            var newId = (_nextRule++).ToString(CultureInfo.InvariantCulture);
            Rules[newId] = new Rule(data["name"]!.GetValue<string>(), data["enabled"]!.GetValue<bool>(), start, conditions, timeout, config);
            return Json(HttpStatusCode.OK, new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["id"] = newId } }.ToJsonString());
        }

        if (method != "PATCH" || id is null || !Rules.TryGetValue(id, out var rule))
        {
            return RestError(1, "Resource not found");
        }

        var next = rule;
        if (data!["name"] is { } name)
        {
            next = next with { Name = name.GetValue<string>() };
        }

        if (data["enabled"] is { } enabled)
        {
            next = next with { Enabled = enabled.GetValue<bool>() };
        }

        if (data["activationConfig"] is { } activation)
        {
            var (start, conditions, timeout) = ReadActivation(activation);
            next = next with { Start = start, Conditions = conditions, Timeout = timeout };
        }

        var configNow = Configs[rule.ConfigId];
        if (data["actionConfig"] is { } action)
        {
            var (template, parameters, error) = ReadAction(action);
            if (error is not null)
            {
                return error;
            }

            configNow = configNow with { Template = template, Parameters = parameters };
        }

        if (Unlimited(configNow.Template) && next.Conditions.Count == 0)
        {
            return RestError(5, "Validation error: Action configuration is of type 'continuous' and requires at least one condition");
        }

        Configs[rule.ConfigId] = configNow;
        Rules[id] = next;
        return Json(HttpStatusCode.OK, """{"status":"success"}""");
    }

    private (string Template, List<NameValue> Parameters, HttpResponseMessage? Error) ReadAction(JsonNode action)
    {
        var template = action["template"]?.GetValue<string>() ?? string.Empty;
        var parameters = new List<NameValue>();
        foreach (var key in new[] { "actionParameters", "recipientParameters" })
        {
            foreach (var p in action[key]?.AsArray() ?? [])
            {
                parameters.Add(new NameValue(p!["name"]!.GetValue<string>(), p["value"]!.GetValue<string>()));
            }
        }

        var t = _templates.FirstOrDefault(x => x.Token == template);
        var expected = t is null ? [] : t.Parameters.Concat(t.RecipientTemplate is { } r ? _recipients.First(x => x.Token == r).Parameters : []).ToHashSet();
        return t is null || !expected.SetEquals(parameters.Select(p => p.Name))
            ? (template, parameters, RestError(0, "Internal error"))
            : (template, parameters, null);
    }

    private static (EventFilter? Start, List<EventFilter> Conditions, string? Timeout) ReadActivation(JsonNode activation)
    {
        static EventFilter Filter(JsonNode n) => new(n["topicExpression"]!.GetValue<string>(), n["messageContent"]?.GetValue<string>());
        var start = activation["startEvent"] is { } s ? Filter(s) : null;
        var conditions = activation["condition"]?.AsArray().Select(c => Filter(c!)).ToList() ?? [];
        return (start, conditions, activation["timeout"]?.GetValue<string>());
    }

    private bool MissingSchedule(string? message) =>
        message is not null && Oadm.Plugins.ActionRules.Rules.RuleText.Terms(message).FirstOrDefault(t => t.Name == "id").Value is { } id
        && id.StartsWith("com.axis.schedules.", StringComparison.Ordinal) && !Schedules.ContainsKey(id);

    private static bool Unlimited(string template) => template.StartsWith("com.axis.action.unlimited.", StringComparison.Ordinal);

    private HttpResponseMessage Soap(XDocument doc)
    {
        var call = doc.Descendants().First(e => e.Name.LocalName == "Body").Elements().First();
        var method = call.Name.LocalName;
        Requests.Add("SOAP " + method);
        if (TimeoutRequest is not null && ("SOAP " + method).Contains(TimeoutRequest, StringComparison.Ordinal) && TimeoutSkip-- <= 0 && TimeoutTimes-- > 0)
        {
            if (TimeoutAfterDoing)
            {
                TimeoutRequest = null;
                _ = Soap(doc);
            }

            throw new TaskCanceledException("The request timed out.");
        }

        if (RefuseRequest is not null && ("SOAP " + method).Contains(RefuseRequest, StringComparison.Ordinal) && RefuseSkip-- <= 0 && RefuseTimes-- > 0)
        {
            return Fault("refused by test");
        }

        switch (method)
        {
            case "GetActionRules":
                return Answer(new XElement(Aa + "GetActionRulesResponse", new XElement(Aa + "ActionRules", Rules.Select(r => RuleXml(r.Key, r.Value)))));
            case "GetActionConfigurations":
                return Answer(new XElement(Aa + "GetActionConfigurationsResponse", new XElement(Aa + "ActionConfigurations", Configs.Select(c =>
                    new XElement(Aa + "ActionConfiguration",
                        new XElement(Aa + "ConfigurationID", c.Key),
                        new XElement(Aa + "Name", c.Value.Name),
                        new XElement(Aa + "TemplateToken", c.Value.Template),
                        new XElement(Aa + "Parameters", c.Value.Parameters.Select(p => new XElement(Aa + "Parameter", new XAttribute("Name", p.Name), new XAttribute("Value", p.Value)))))))));
            case "GetRecipientConfigurations":
                return Answer(new XElement(Aa + "GetRecipientConfigurationsResponse", new XElement(Aa + "RecipientConfigurations", Recipients.Select(r =>
                    new XElement(Aa + "RecipientConfiguration",
                        new XElement(Aa + "ConfigurationID", r.Key),
                        new XElement(Aa + "TemplateToken", r.Value.Template),
                        new XElement(Aa + "Name", r.Value.Name),
                        new XElement(Aa + "Parameters", r.Value.Parameters.Select(p => new XElement(Aa + "Parameter", new XAttribute("Name", p.Name), new XAttribute("Value", p.Value)))))))));
            case "GetScheduledEvents":
                return Answer(new XElement(Aev + "GetScheduledEventsResponse", new XElement(Aev + "ScheduledEvents", Schedules.Values.Select(sc =>
                    new XElement(Aev + "ScheduledEvent",
                        new XElement(Aev + "EventID", sc.Id),
                        new XElement(Aev + "Name", sc.Name),
                        new XElement(Aev + "Schedule", new XElement(Aev + "ICalendar", sc.Definition)))))));
            case "AddRecipientConfiguration":
                var nrc = call.Element(Aa + "NewRecipientConfiguration")!;
                var recipientId = (_nextRecipient++).ToString(CultureInfo.InvariantCulture);
                Recipients[recipientId] = new DeviceRecipient(
                    recipientId,
                    (string)nrc.Element(Aa + "Name")!,
                    (string)nrc.Element(Aa + "TemplateToken")!,
                    [.. nrc.Element(Aa + "Parameters")!.Elements().Select(p => new NameValue((string)p.Attribute("Name")!, (string)p.Attribute("Value")!))]);
                return Answer(new XElement(Aa + "AddRecipientConfigurationResponse", new XElement(Aa + "ConfigurationID", recipientId)));
            case "RemoveRecipientConfiguration":
                return Recipients.Remove(((string)call.Element(Aa + "ConfigurationID")!).Trim()) ? Answer(new XElement(Aa + "RemoveRecipientConfigurationResponse")) : Fault("Recipient not found");
            case "AddScheduledEvent":
                var ns = call.Element(Aev + "NewScheduledEvent")!;
                var scheduleId = ((string?)ns.Element(Aev + "EventID"))?.Trim() is { Length: > 0 } given ? given : "com.axis.schedules.genid.id-" + Schedules.Count.ToString(CultureInfo.InvariantCulture);
                if (Schedules.ContainsKey(scheduleId))
                {
                    return Fault($"scheduled event with id '{scheduleId}' already exists");
                }

                Schedules[scheduleId] = new DeviceSchedule(scheduleId, (string)ns.Element(Aev + "Name")!, ((string)ns.Element(Aev + "Schedule")!.Element(Aev + "ICalendar")!).Trim());
                return Answer(new XElement(Aev + "AddScheduledEventResponse"));
            case "RemoveScheduledEvent":
                return Schedules.Remove(((string)call.Element(Aev + "EventID")!).Trim()) ? Answer(new XElement(Aev + "RemoveScheduledEventResponse")) : Fault("Schedule not found");
            case "GetActionTemplates":
            case "GetRecipientTemplates":
            case "GetEventInstances":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture(method, _os).ToString(), Encoding.UTF8, "application/soap+xml") };
            case "AddActionConfiguration":
                var nc = call.Element(Aa + "NewActionConfiguration")!;
                var configId = (_nextConfig++).ToString(CultureInfo.InvariantCulture);
                Configs[configId] = new Config(
                    (string)nc.Element(Aa + "Name")!,
                    (string)nc.Element(Aa + "TemplateToken")!,
                    [.. nc.Element(Aa + "Parameters")!.Elements().Select(p => new NameValue((string)p.Attribute("Name")!, (string)p.Attribute("Value")!))]);
                return Answer(new XElement(Aa + "AddActionConfigurationResponse", new XElement(Aa + "ConfigurationID", configId)));
            case "AddActionRule":
                var nr = call.Element(Aa + "NewActionRule")!;
                if (nr.Descendants().Where(e => e.Name.LocalName == "MessageContent").Select(e => e.Value).Any(m => MissingSchedule(m)))
                {
                    return Fault("could not match any property events");
                }

                // AXIS OS 10.12 appends "//." to condition topics on read but refuses it back on a leaf topic.
                if (nr.Descendants().Where(e => e.Name.LocalName == "TopicExpression").Any(e => e.Value.EndsWith("Interval//.", StringComparison.Ordinal)))
                {
                    return Fault("failed to parse topic expression");
                }

                var primary = (string)nr.Element(Aa + "PrimaryAction")!;
                if (Configs.TryGetValue(primary, out var action) && Unlimited(action.Template) && nr.Element(Aa + "Conditions") is null)
                {
                    // AXIS OS 10.12 and 12.11 over SOAP, like REST.
                    return Fault($"Primary action with id '{primary}' and of type 'continuous' requires at least one condition");
                }

                var ruleId = (_nextRule++).ToString(CultureInfo.InvariantCulture);
                Rules[ruleId] = new Rule(
                    (string)nr.Element(Aa + "Name")!,
                    (string)nr.Element(Aa + "Enabled")! == "true",
                    ReadFilter(nr.Element(Aa + "StartEvent")),
                    [.. (nr.Element(Aa + "Conditions")?.Elements() ?? []).Select(ReadFilter).OfType<EventFilter>()],
                    (string?)nr.Element(Aa + "ActivationTimeout"),
                    (string)nr.Element(Aa + "PrimaryAction")!);
                return Answer(new XElement(Aa + "AddActionRuleResponse", new XElement(Aa + "RuleID", ruleId)));
            case "RemoveActionRule":
                return Rules.Remove(((string)call.Element(Aa + "RuleID")!).Trim()) ? Answer(new XElement(Aa + "RemoveActionRuleResponse")) : Fault("Rule not found");
            case "RemoveActionConfiguration":
                return Configs.Remove(((string)call.Element(Aa + "ConfigurationID")!).Trim()) ? Answer(new XElement(Aa + "RemoveActionConfigurationResponse")) : Fault("Configuration not found");
            default:
                return Fault("Optional action not implemented");
        }
    }

    private XElement RuleXml(string id, Rule rule)
    {
        static XElement Filter(XName name, EventFilter f) => new(
            name,
            new XElement(XName.Get("TopicExpression", "http://docs.oasis-open.org/wsn/b-2"), f.Topic),
            f.Message is null ? null : new XElement(XName.Get("MessageContent", "http://docs.oasis-open.org/wsn/b-2"), f.Message));
        return new XElement(
            Aa + "ActionRule",
            new XElement(Aa + "RuleID", id),
            new XElement(Aa + "Name", rule.Name),
            new XElement(Aa + "Enabled", rule.Enabled ? "true" : "false"),
            rule.Start is null ? null : Filter(Aa + "StartEvent", rule.Start),
            rule.Conditions.Count == 0 ? null : new XElement(Aa + "Conditions", rule.Conditions.Select(c => Filter(Aa + "Condition", _os == "os10" ? c with { Topic = c.Topic + "//." } : c))),
            rule.Timeout is null ? null : new XElement(Aa + "ActivationTimeout", rule.Timeout),
            new XElement(Aa + "PrimaryAction", rule.ConfigId));
    }

    private static EventFilter? ReadFilter(XElement? e)
    {
        var topic = e?.Elements().FirstOrDefault(x => x.Name.LocalName == "TopicExpression")?.Value;
        return topic is null ? null : new EventFilter(topic, e!.Elements().FirstOrDefault(x => x.Name.LocalName == "MessageContent")?.Value);
    }

    private static HttpResponseMessage Answer(XElement content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            new XDocument(new XElement(XName.Get("Envelope", "http://www.w3.org/2003/05/soap-envelope"), new XElement(XName.Get("Body", "http://www.w3.org/2003/05/soap-envelope"), content))).ToString(),
            Encoding.UTF8,
            "application/soap+xml"),
    };

    private static HttpResponseMessage Fault(string text) => new(HttpStatusCode.InternalServerError)
    {
        Content = new StringContent(
            $"<?xml version=\"1.0\"?><SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\"><SOAP-ENV:Body><SOAP-ENV:Fault><SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">{SecurityElement.Escape(text)}</SOAP-ENV:Text></SOAP-ENV:Reason></SOAP-ENV:Fault></SOAP-ENV:Body></SOAP-ENV:Envelope>",
            Encoding.UTF8,
            "application/soap+xml"),
    };

    private static HttpResponseMessage RestError(int code, string message) =>
        Json(HttpStatusCode.OK, new JsonObject { ["status"] = "error", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString());

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
