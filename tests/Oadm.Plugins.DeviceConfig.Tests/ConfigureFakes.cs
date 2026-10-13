using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Tests;

internal static class ConfigureFixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "configure", name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "configure", name));

    /// <summary>Configurable parameters of 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77): 1,518 in all, 879 configurable.</summary>
    public const int ConfigurableParameters = 879;
}

/// <summary>
/// A camera that answers like 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77) from the recorded answers, and keeps what is
/// written: param.cgi (list, listdefinitions per group, update), the action service (rules, configurations), the event
/// service (schedules) and streamprofile.cgi. Everything else fails the test.
/// </summary>
internal sealed class ConfigureCamera : IVapixClient
{
    private static readonly XNamespace Pd = "http://www.axis.com/ParameterDefinitionsSchema";
    private static readonly XNamespace Aa = "http://www.axis.com/vapix/ws/action1";
    private static readonly XNamespace Aev = "http://www.axis.com/vapix/ws/event1";
    private static readonly XNamespace Wsnt = "http://docs.oasis-open.org/wsn/b-2";

    private readonly XDocument _definitions = XDocument.Parse(ConfigureFixtures.Read("listdefinitions-all.xml"));
    private int _nextId = 10;

    /// <summary>Own schedules get the next genid number like a real camera (it has id-0 already): new ids collide with old ones.</summary>
    private int _nextScheduleId = 1;

    public ConfigureCamera()
    {
        foreach (var line in ConfigureFixtures.Read("param-list-all.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            Values[line[5..eq]] = line[(eq + 1)..];
        }

        Configurations = [.. ActionEngineApi.ParseConfigurations(XDocument.Parse(ConfigureFixtures.Read("action-configurations.xml"))).Values];
        var rules = XDocument.Parse(ConfigureFixtures.Read("action-rules.xml")).Descendants(Aa + "ActionRule").ToList();
        Rules = [.. rules.Select(r => new DeviceRule((string)r.Element(Aa + "RuleID")!, (string)r.Element(Aa + "Name")!, (string)r.Element(Aa + "PrimaryAction")!,
            r.Element(Aa + "StartEvent")!.ToString(SaveOptions.DisableFormatting)))];
        Schedules = [.. ActionEngineApi.ParseSchedules(XDocument.Parse(ConfigureFixtures.Read("scheduled-events.xml")))];
        Profiles = [.. StreamProfileApi.Parse((JsonObject)JsonNode.Parse(ConfigureFixtures.Read("stream-profiles.json"))!).Profiles];
    }

    public sealed record DeviceRule(string Id, string Name, string ConfigurationId, string StartEventXml);

    public Uri BaseAddress { get; } = new("https://10.0.0.48/");

    /// <summary>Every parameter value (without "root.").</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public List<ConfigAction> Configurations { get; }

    public List<DeviceRule> Rules { get; }

    public List<ConfigSchedule> Schedules { get; }

    public List<ConfigStreamProfile> Profiles { get; }

    public int MaxProfiles { get; set; } = 26;

    /// <summary>The answer for all groups is larger than the XML limit (like a camera with many sensors).</summary>
    public bool OversizeWholeTree { get; set; }

    public List<string> Requests { get; } = [];

    /// <summary>Parameter update bodies as sent (form fields).</summary>
    public List<IReadOnlyList<KeyValuePair<string, string>>> Updates { get; } = [];

    /// <summary>Action templates the device does not have (AddActionConfiguration answers a SOAP fault).</summary>
    public HashSet<string> RefuseTemplates { get; } = new(StringComparer.Ordinal);

    /// <summary>AddScheduledEvent answers a SOAP fault.</summary>
    public bool FailSchedules { get; set; }

    /// <summary>Parameters the device refuses on update ("# Error: ...").</summary>
    public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

    /// <summary>Called with "METHOD path" before each request; an exception it returns is thrown.</summary>
    public Func<string, Exception?>? Fail { get; set; }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) =>
        Task.FromResult(new BasicDeviceInfo("B8A44F631339", "P3265-V", "P3265-V", "AXIS P3265-V Dome Camera", "12.11.77", "9A3", "aarch64", "Dome Camera", "Axis Artpec-8"));

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add("GET param.cgi list");
        }

        var wanted = groups.ToList();
        IReadOnlyDictionary<string, string> result = Values
            .Where(p => wanted.Contains("root") || wanted.Any(g => p.Key == g || p.Key.StartsWith(g + ".", StringComparison.Ordinal)))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return Task.FromResult(result);
    }

    public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("not used");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DeviceApi>>([new DeviceApi("param-cgi", "1.0"), new DeviceApi("stream-profiles", "1.0")]);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var uri = request.RequestUri!.OriginalString;
        var path = uri.Split('?')[0];
        // The definitions are read in parallel: the list is shared.
        lock (Requests)
        {
            Requests.Add(request.Method + " " + uri);
        }

        if (Fail?.Invoke(request.Method + " " + path) is { } failure)
        {
            throw failure;
        }

        if (request.Method == HttpMethod.Get && path == ParamDefinitionsReader.ParamPath && uri.Contains("action=listdefinitions", StringComparison.Ordinal))
        {
            if (!uri.Contains("group=", StringComparison.Ordinal))
            {
                Fill(_definitions.Root!.Element(Pd + "group")!, string.Empty, isRoot: true);
                return Text(_definitions.ToString() + (OversizeWholeTree ? "<!--" + new string('x', 1_100_000) + "-->" : string.Empty), "text/xml");
            }

            var group = Uri.UnescapeDataString(uri[(uri.IndexOf("group=", StringComparison.Ordinal) + 6)..]);
            return Text(Definitions(group), "text/xml");
        }

        if (request.Method == HttpMethod.Post && path == ParamDefinitionsReader.ParamPath)
        {
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=', 2))
                // Strict: OADM sends %20 for a space like its other param.cgi writers; a '+' must arrive as itself.
                .Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0]), Uri.UnescapeDataString(p[1])))
                .ToList();
            Assert.Equal("update", form.Single(p => p.Key == "action").Value);
            var parameters = form.Where(p => p.Key != "action").ToList();
            Updates.Add(parameters);
            if (parameters.FirstOrDefault(p => !Values.ContainsKey(p.Key) || Refuse.Contains(p.Key)) is { Key: not null } bad)
            {
                return Text($"# Error: Error setting 'root.{bad.Key}' to '{bad.Value}'!", "text/plain");
            }

            foreach (var (name, value) in parameters)
            {
                Values[name] = value;
            }

            return Text("OK", "text/plain");
        }

        if (request.Method == HttpMethod.Post && path == ActionEngineApi.ServicePath)
        {
            return Text(Soap(XDocument.Parse(await request.Content!.ReadAsStringAsync(ct))), "application/soap+xml");
        }

        if (request.Method == HttpMethod.Post && path == StreamProfileApi.Path)
        {
            return Text(StreamProfiles((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!), "application/json");
        }

        throw new InvalidOperationException("Unexpected request " + request.Method + " " + uri);
    }

    /// <summary>The recorded definitions of one group ("Image" or "Image.I0"), with the current values.</summary>
    private string Definitions(string group)
    {
        var parts = group.Split('.');
        XElement? node = _definitions.Root!.Element(Pd + "group");
        foreach (var part in parts)
        {
            node = node?.Elements(Pd + "group").FirstOrDefault(g => (string?)g.Attribute("name") == part);
        }

        var root = new XElement(Pd + "group", new XAttribute("name", "root"));
        if (node is not null)
        {
            // The current values, found by the full path of each parameter.
            var copy = new XElement(node);
            Fill(copy, string.Join('.', parts[..^1]));
            XElement wrap = copy;
            for (var i = parts.Length - 2; i >= 0; i--)
            {
                wrap = new XElement(Pd + "group", new XAttribute("name", parts[i]), wrap);
            }

            root.Add(wrap);
        }

        return new XDocument(new XElement(Pd + "parameterDefinitions", new XElement(Pd + "model", "AXIS P3265-V"), root)).ToString();
    }

    private void Fill(XElement group, string path, bool isRoot = false)
    {
        var full = isRoot ? string.Empty : path.Length == 0 ? (string)group.Attribute("name")! : path + "." + (string)group.Attribute("name")!;
        foreach (var child in group.Elements())
        {
            var name = full.Length == 0 ? (string)child.Attribute("name")! : full + "." + (string)child.Attribute("name")!;
            if (child.Name == Pd + "group")
            {
                Fill(child, full);
            }
            else if (child.Name == Pd + "parameter" && Values.TryGetValue(name, out var value))
            {
                child.SetAttributeValue("value", value);
            }
        }
    }

    private string Soap(XDocument request)
    {
        var call = request.Descendants().First(e => e.Parent?.Name.LocalName == "Body");
        XElement answer;
        switch (call.Name.LocalName)
        {
            case "GetActionRules":
                answer = new XElement(Aa + "GetActionRulesResponse", new XElement(Aa + "ActionRules", Rules.Select(r =>
                    new XElement(Aa + "ActionRule",
                        new XElement(Aa + "RuleID", r.Id),
                        new XElement(Aa + "Name", r.Name),
                        new XElement(Aa + "Enabled", "true"),
                        XElement.Parse(r.StartEventXml),
                        new XElement(Aa + "PrimaryAction", r.ConfigurationId)))));
                break;
            case "GetActionConfigurations":
                answer = new XElement(Aa + "GetActionConfigurationsResponse", new XElement(Aa + "ActionConfigurations", Configurations.Select(c =>
                    new XElement(Aa + "ActionConfiguration",
                        new XElement(Aa + "ConfigurationID", c.Id),
                        new XElement(Aa + "Name", c.Name),
                        new XElement(Aa + "TemplateToken", c.Template),
                        new XElement(Aa + "Parameters", c.Parameters.Select(p => new XElement(Aa + "Parameter", new XAttribute("Value", p.Value), new XAttribute("Name", p.Name))))))));
                break;
            case "GetScheduledEvents":
                answer = new XElement(Aev + "GetScheduledEventsResponse", new XElement(Aev + "ScheduledEvents", Schedules.Select(s =>
                    new XElement(Aev + "ScheduledEvent",
                        new XElement(Aev + "EventID", s.Id),
                        new XElement(Aev + "Name", s.Name),
                        new XElement(Aev + "Schedule", new XElement(Aev + "ICalendar", s.Definition))))));
                break;
            case "AddScheduledEvent":
            {
                var item = call.Element(Aev + "NewScheduledEvent")!;
                if (FailSchedules)
                {
                    return Fault("Could not add the scheduled event");
                }

                var id = "com.axis.schedules.genid.id-" + (_nextScheduleId++).ToString(CultureInfo.InvariantCulture);
                Schedules.Add(new ConfigSchedule(id, (string)item.Element(Aev + "Name")!, (string)item.Element(Aev + "Schedule")!.Element(Aev + "ICalendar")!));
                answer = new XElement(Aev + "AddScheduledEventResponse", new XElement(Aev + "EventID", id));
                break;
            }

            case "AddActionConfiguration":
            {
                var item = call.Element(Aa + "NewActionConfiguration")!;
                if (RefuseTemplates.Contains((string)item.Element(Aa + "TemplateToken")!))
                {
                    return Fault("Invalid template token");
                }

                var id = (_nextId++).ToString(CultureInfo.InvariantCulture);
                Configurations.Add(new ConfigAction((string)item.Element(Aa + "Name")!, (string)item.Element(Aa + "TemplateToken")!,
                    [.. item.Element(Aa + "Parameters")!.Elements(Aa + "Parameter").Select(p => new NameValue((string)p.Attribute("Name")!, (string)p.Attribute("Value")!))])
                {
                    Id = id,
                });
                answer = new XElement(Aa + "AddActionConfigurationResponse", new XElement(Aa + "ConfigurationID", id));
                break;
            }

            case "AddActionRule":
            {
                var item = call.Element(Aa + "NewActionRule")!;
                var id = (_nextId++).ToString(CultureInfo.InvariantCulture);
                var start = item.Element(Aa + "StartEvent")!;
                Assert.NotNull(start.Element(Wsnt + "TopicExpression"));
                Assert.Contains(Configurations, c => c.Id == (string)item.Element(Aa + "PrimaryAction")!);
                Rules.Add(new DeviceRule(id, (string)item.Element(Aa + "Name")!, (string)item.Element(Aa + "PrimaryAction")!, start.ToString(SaveOptions.DisableFormatting)));
                answer = new XElement(Aa + "AddActionRuleResponse", new XElement(Aa + "RuleID", id));
                break;
            }

            case "RemoveActionRule":
                Assert.Equal(1, Rules.RemoveAll(r => r.Id == (string)call.Element(Aa + "RuleID")!));
                answer = new XElement(Aa + "RemoveActionRuleResponse");
                break;
            case "RemoveActionConfiguration":
                Assert.Equal(1, Configurations.RemoveAll(c => c.Id == (string)call.Element(Aa + "ConfigurationID")!));
                answer = new XElement(Aa + "RemoveActionConfigurationResponse");
                break;
            default:
                throw new InvalidOperationException("Unexpected SOAP call " + call.Name.LocalName);
        }

        XNamespace soap = "http://www.w3.org/2003/05/soap-envelope";
        return new XDocument(new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "SOAP-ENV", soap),
            new XAttribute(XNamespace.Xmlns + "aa", Aa),
            new XAttribute(XNamespace.Xmlns + "aev", Aev),
            new XAttribute(XNamespace.Xmlns + "wsnt", Wsnt),
            new XElement(soap + "Body", answer))).ToString();
    }

    private static string Fault(string reason)
    {
        XNamespace soap = "http://www.w3.org/2003/05/soap-envelope";
        return new XDocument(new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(soap + "Fault", new XElement(soap + "Reason", new XElement(soap + "Text", reason)))))).ToString();
    }

    private string StreamProfiles(JsonObject request)
    {
        var method = (string)request["method"]!;
        var items = (request["params"]!["streamProfile"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => new ConfigStreamProfile((string)p["name"]!, (string?)p["description"], (string)p["parameters"]!)).ToList();
        switch (method)
        {
            case "list":
                return new JsonObject
                {
                    ["method"] = "list",
                    ["apiVersion"] = "1.0",
                    ["data"] = new JsonObject
                    {
                        ["streamProfile"] = new JsonArray([.. Profiles.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["description"] = p.Description, ["parameters"] = p.Parameters })]),
                        ["maxProfiles"] = MaxProfiles,
                    },
                }.ToJsonString();
            case "create":
                foreach (var item in items)
                {
                    if (Profiles.Any(p => p.Name == item.Name))
                    {
                        return """{"apiVersion":"1.0","method":"create","error":{"code":2104,"message":"Stream profile already exists"}}""";
                    }

                    Profiles.Add(item);
                }

                return """{"apiVersion":"1.0","method":"create","data":{}}""";
            case "update":
                foreach (var item in items)
                {
                    var index = Profiles.FindIndex(p => p.Name == item.Name);
                    Assert.True(index >= 0);
                    Profiles[index] = item;
                }

                return """{"apiVersion":"1.0","method":"update","data":{}}""";
            default:
                throw new InvalidOperationException("Unexpected stream profile method " + method);
        }
    }

    private static HttpResponseMessage Text(string body, string type) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
}
